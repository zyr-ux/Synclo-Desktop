using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Synclo.Models;
using Microsoft.Extensions.Logging;

namespace Synclo.Services.ClipboardService;

// ---------------------------------------------------------
// 1. RepoChange Definitions
// ---------------------------------------------------------
public enum RepoChangeType
{
    Upsert,
    Delete,
    Reset
}

public readonly struct RepoChangeEvent
{
    public RepoChangeType Type { get; }
    public IReadOnlyList<ClipboardDbModel> Items { get; }
    
    public RepoChangeEvent(RepoChangeType type, IReadOnlyList<ClipboardDbModel>? items = null)
    {
        Type = type;
        Items = items ?? Array.Empty<ClipboardDbModel>();
    }
    
    public static RepoChangeEvent Upsert(List<ClipboardDbModel> items) =>
        new(RepoChangeType.Upsert, items);
    
    public static RepoChangeEvent Delete(string id) =>
        new(RepoChangeType.Delete, new[] { new ClipboardDbModel { Id = id } });
    
    public static RepoChangeEvent Reset() =>
        new(RepoChangeType.Reset);
}

// ---------------------------------------------------------
// 2. Main Class Implementation
// ---------------------------------------------------------
public sealed class ClipboardRepository : IClipboardRepository, IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<ClipboardRepository> _logger;
    private readonly RepositoryConfig _config;
    
    // Locks
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    
    private bool _disposed;
    
    // Task Scheduling
    private static readonly TaskScheduler _dbScheduler =
        new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;
    private static readonly TaskFactory _db =
        new(CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskContinuationOptions.None, _dbScheduler);
    
    public event Action? OnDataChanged;

    public ClipboardRepository(ILogger<ClipboardRepository> logger, RepositoryConfig config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appDataPath, "Synclo");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "clipboard.db");
    }

    public Task InitializeAsync()
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                // Verify WAL mode is enabled
                await VerifyWALModeAsync(connection).ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA busy_timeout=5000;

CREATE TABLE IF NOT EXISTS clipboard_entries (
    id TEXT PRIMARY KEY,
    content TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    ciphertext TEXT NOT NULL,
    nonce TEXT NOT NULL,
    blob_version INTEGER NOT NULL,
    is_remote_deleted INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    synced_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_content_hash ON clipboard_entries(content_hash);
CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_entries(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_synced_at ON clipboard_entries(synced_at);
CREATE INDEX IF NOT EXISTS idx_is_remote_deleted ON clipboard_entries(is_remote_deleted);
";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                
                _logger.LogInformation("Database initialized successfully. WAL mode verified.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
                throw;
            }
            finally { _dbLock.Release(); }
        }).Unwrap();
    }

    private async Task VerifyWALModeAsync(SqliteConnection connection)
    {
        try
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        
            // Fixed: Handle null result and proper string comparison
            if (result == null || !string.Equals(result.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"WAL mode not enabled. Current mode: {result?.ToString() ?? "null"}. This may impact concurrency performance.");
            }
            else
            {
                _logger.LogInformation("WAL mode enabled successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify WAL mode");
            throw;
        }
    }

    // ---------------------------------------------------------
    // READS
    // ---------------------------------------------------------


    // INTERFACE IMPLEMENTATION: Parameterless GetAllAsync
    public Task<List<ClipboardDbModel>> GetAllAsync()
    {
        // Default to a reasonable limit (e.g. 100) to satisfy the interface safely
        return GetAllAsync(_config.DefaultHistoryLimit, 0);
    }

    public Task<List<ClipboardDbModel>> GetAllAsync(int limit, int offset = 0)
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT id, content, content_hash, ciphertext, nonce, blob_version,
       is_remote_deleted, created_at, synced_at
FROM clipboard_entries
ORDER BY created_at DESC, ROWID DESC
LIMIT $limit OFFSET $offset
";
                cmd.Parameters.AddWithValue("$limit", limit);
                cmd.Parameters.AddWithValue("$offset", offset);
                
                var list = new List<ClipboardDbModel>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(MapFromReader(reader));
                }
                
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all clipboard entries");
                throw;
            }
            finally { _dbLock.Release(); }
        }).Unwrap();
    }

    public Task<ClipboardDbModel?> GetByIdAsync(string id)
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM clipboard_entries WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    return MapFromReader(reader);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get entry by ID: {id}");
                throw;
            }
            finally { _dbLock.Release(); }
        }).Unwrap();
    }

    public Task<ClipboardDbModel?> GetByHashAsync(string hash)
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM clipboard_entries WHERE content_hash = $hash LIMIT 1";
                cmd.Parameters.AddWithValue("$hash", hash);
                
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    return MapFromReader(reader);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get entry by hash: {hash}");
                throw;
            }
            finally { _dbLock.Release(); }
        }).Unwrap();
    }

    public Task<List<ClipboardDbModel>> GetUnsyncedAsync()
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = new List<ClipboardDbModel>();
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM clipboard_entries WHERE synced_at IS NULL AND is_remote_deleted = 0 ORDER BY created_at ASC";
                
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    list.Add(MapFromReader(reader));
                }
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get unsynced entries");
                throw;
            }
            finally { _dbLock.Release(); }
        }).Unwrap();
    }

    // ---------------------------------------------------------
    // WRITES
    // ---------------------------------------------------------
    /// <summary>
    /// Insert or update a single clipboard entry (convenience method)
    /// </summary>
    public Task UpsertAsync(ClipboardDbModel entry)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));
        if (string.IsNullOrEmpty(entry.Id))
            throw new ArgumentException("Entry ID cannot be null or empty", nameof(entry));
        if (string.IsNullOrEmpty(entry.Content))
            throw new ArgumentException("Entry Content cannot be null or empty", nameof(entry));
        if (string.IsNullOrEmpty(entry.ContentHash))
            throw new ArgumentException("Entry ContentHash cannot be null or empty", nameof(entry));
        
        return UpsertAsync(new[] { entry });
    }

    /// <summary>
    /// Insert or update multiple clipboard entries with optimized batching.
    /// Fires a single event after all changes complete.
    /// </summary>
    public Task UpsertAsync(IEnumerable<ClipboardDbModel> entries)
    {
        if (entries == null)
            throw new ArgumentNullException(nameof(entries));
        
        var entryList = entries.ToList();
        if (entryList.Count == 0)
            return Task.CompletedTask;
        
        // Validate all entries
        foreach (var entry in entryList)
        {
            if (entry == null)
                throw new ArgumentException("Entry collection contains null entry", nameof(entries));
            if (string.IsNullOrEmpty(entry.Id))
                throw new ArgumentException($"Entry with null/empty ID found", nameof(entries));
            if (string.IsNullOrEmpty(entry.Content))
                throw new ArgumentException($"Entry {entry.Id} has null/empty Content", nameof(entries));
            if (string.IsNullOrEmpty(entry.ContentHash))
                throw new ArgumentException($"Entry {entry.Id} has null/empty ContentHash", nameof(entries));
        }
        
        return _db.StartNew(async () =>
        {
            RepoChangeEvent? eventToFire = null;
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var changed = new List<ClipboardDbModel>();
                
                // Smart batching: Use transaction only for multiple items
                if (entryList.Count == 1)
                {
                    // Single item - direct insert (no transaction overhead)
                    var entry = entryList[0];
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = @"
INSERT OR REPLACE INTO clipboard_entries
(id, content, content_hash, ciphertext, nonce, blob_version, is_remote_deleted, created_at, synced_at)
VALUES ($id, $content, $hash, $cipher, $nonce, $ver, $del, $created, $synced)";
                    AddParams(cmd, entry);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    changed.Add(entry);
                }
                else
                {
                    // Multiple items - use transaction for atomicity
                    using var tx = connection.BeginTransaction();
                    foreach (var entry in entryList)
                    {
                        var cmd = connection.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT OR REPLACE INTO clipboard_entries
(id, content, content_hash, ciphertext, nonce, blob_version, is_remote_deleted, created_at, synced_at)
VALUES ($id, $content, $hash, $cipher, $nonce, $ver, $del, $created, $synced)";
                        AddParams(cmd, entry);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        changed.Add(entry);
                    }
                    tx.Commit();
                }
                
                // Fire single event only if changes occurred
                if (changed.Count > 0)
                    eventToFire = RepoChangeEvent.Upsert(changed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert clipboard entries");
                throw;
            }
            finally { _dbLock.Release(); }
            
            // Fire event outside the lock
            if (eventToFire.HasValue)
                NotifyObservers(eventToFire.Value);
        }).Unwrap();
    }


    public Task DeleteByIdAsync(string id)
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM clipboard_entries WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete entry by ID: {id}");
                throw;
            }
            finally { _dbLock.Release(); }
            
            NotifyObservers(RepoChangeEvent.Delete(id));
        }).Unwrap();
    }

    public Task ClearAllAsync()
    {
        return _db.StartNew(async () =>
        {
            await _dbLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);
                
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM clipboard_entries";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear all entries");
                throw;
            }
            finally { _dbLock.Release(); }
            
            NotifyObservers(RepoChangeEvent.Reset());
        }).Unwrap();
    }

    // ---------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------
    private void NotifyObservers(RepoChangeEvent change)
    {
        try
        {
            OnDataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying observers");
        }
    }

    private static void AddParams(SqliteCommand cmd, ClipboardDbModel entry)
    {
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$content", entry.Content);
        cmd.Parameters.AddWithValue("$hash", entry.ContentHash);
        cmd.Parameters.AddWithValue("$cipher", entry.Ciphertext);
        cmd.Parameters.AddWithValue("$nonce", entry.Nonce);
        cmd.Parameters.AddWithValue("$ver", entry.BlobVersion);
        cmd.Parameters.AddWithValue("$del", entry.IsRemoteDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$synced", entry.SyncedAt?.ToString("O") ?? (object)DBNull.Value);
    }

    private static ClipboardDbModel MapFromReader(SqliteDataReader reader)
    {
        return new ClipboardDbModel
        {
            Id = reader.GetString(0),
            Content = reader.GetString(1),
            ContentHash = reader.GetString(2),
            Ciphertext = reader.GetString(3),
            Nonce = reader.GetString(4),
            BlobVersion = reader.GetInt32(5),
            IsRemoteDeleted = reader.GetInt32(6) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            SyncedAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try
        {
            _dbLock.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disposal");
        }
    }
}

/// <summary>
/// Configuration for repository settings
/// </summary>
public class RepositoryConfig
{
    public int DefaultHistoryLimit { get; set; } = 100;
}