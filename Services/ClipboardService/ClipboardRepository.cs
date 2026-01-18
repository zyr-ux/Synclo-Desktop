using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Synclo.Models;

namespace Synclo.Services.ClipboardService;

public interface IClipboardRepository
{
    Task InitializeAsync();
    Task<List<ClipboardDbModel>> GetAllAsync();
    Task<List<ClipboardDbModel>> GetAllAsync(int limit, int offset = 0);
    Task<ClipboardDbModel?> GetByIdAsync(string id);
    Task<ClipboardDbModel?> GetByHashAsync(string hash);
    Task<Dictionary<string, ClipboardDbModel>> GetHashMapAsync(IEnumerable<string> hashes);
    Task<Dictionary<string, ClipboardDbModel>> GetByIdsAsync(IEnumerable<string> ids);
    Task<List<ClipboardDbModel>> GetUnsyncedAsync();
    Task UpsertAsync(ClipboardDbModel entry);
    Task UpsertAsync(IEnumerable<ClipboardDbModel> entries);
    Task MarkAsSyncedAsync(string id);
    Task MarkDeletedAsync(string id);
    Task DeleteByIdAsync(string id);
    Task ClearAllAsync();
    Task RunInTransactionAsync(Func<Task> action);
    event Action? OnDataChanged;
}

public sealed class ClipboardRepository : IClipboardRepository, IDisposable
{
    private const int DefaultHistoryLimit = 100;

    // Task Scheduling
    private static readonly TaskScheduler _dbScheduler =
        new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 1).ExclusiveScheduler;

    private static readonly TaskFactory _db =
        new(CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskContinuationOptions.None, _dbScheduler);

    private readonly string _dbPath;
    private readonly ILogger<ClipboardRepository> _logger;

    private bool _disposed;

    public ClipboardRepository(ILogger<ClipboardRepository> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appDataPath, "Synclo");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "clipboard.db");
    }

    public event Action? OnDataChanged;

    public Task InitializeAsync()
    {
        return _db.StartNew(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

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
    created_at TEXT NOT NULL,
    is_synced INTEGER NOT NULL DEFAULT 0,
    is_deleted INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_content_hash ON clipboard_entries(content_hash);
CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_entries(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_is_synced ON clipboard_entries(is_synced);
";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                _logger.LogInformation("Database initialized successfully. WAL mode verified.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
                throw;
            }
        }).Unwrap();
    }

    // ---------------------------------------------------------
    // READS
    // ---------------------------------------------------------


    // INTERFACE IMPLEMENTATION: Parameterless GetAllAsync
    public Task<List<ClipboardDbModel>> GetAllAsync()
    {
        // Default to a reasonable limit (e.g. 100) to satisfy the interface safely
        return GetAllAsync(DefaultHistoryLimit);
    }

    public Task<List<ClipboardDbModel>> GetAllAsync(int limit, int offset = 0)
    {
        return _db.StartNew(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT id, content, content_hash, ciphertext, nonce, blob_version,
       created_at, is_synced, is_deleted
FROM clipboard_entries
ORDER BY created_at DESC, ROWID DESC
LIMIT $limit OFFSET $offset
";
                cmd.Parameters.AddWithValue("$limit", limit);
                cmd.Parameters.AddWithValue("$offset", offset);

                var list = new List<ClipboardDbModel>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(MapFromReader(reader));

                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all clipboard entries");
                throw;
            }
        }).Unwrap();
    }

    public Task<ClipboardDbModel?> GetByIdAsync(string id)
    {
        return _db.StartNew(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM clipboard_entries WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false)) return MapFromReader(reader);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get entry by ID: {id}");
                throw;
            }
        }).Unwrap();
    }

    public Task<ClipboardDbModel?> GetByHashAsync(string hash)
    {
        return _db.StartNew(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT * FROM clipboard_entries WHERE content_hash = $hash LIMIT 1";
                cmd.Parameters.AddWithValue("$hash", hash);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false)) return MapFromReader(reader);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get entry by hash: {hash}");
                throw;
            }
        }).Unwrap();
    }

    public Task<List<ClipboardDbModel>> GetUnsyncedAsync()
    {
        return _db.StartNew(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
SELECT id, content, content_hash, ciphertext, nonce, blob_version,
       created_at, is_synced, is_deleted
FROM clipboard_entries
WHERE is_synced = 0
ORDER BY created_at ASC
";

                var list = new List<ClipboardDbModel>();
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) list.Add(MapFromReader(reader));

                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get unsynced entries");
                throw;
            }
        }).Unwrap();
    }

    // Issue #4 fix: Bulk hash fetch to avoid N+1 queries (Robust chunked implementation)
    public Task<Dictionary<string, ClipboardDbModel>> GetHashMapAsync(IEnumerable<string> hashes)
    {
        return _db.StartNew((Func<Task<Dictionary<string, ClipboardDbModel>>>)(async () =>
        {
            var result = new Dictionary<string, ClipboardDbModel>();
            var uniqueHashes = hashes.Distinct().ToList();
            
            if (uniqueHashes.Count == 0) return result;

            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                // Chunk size to stay safely under SQLite's default variable limit (999)
                const int ChunkSize = 900; 
                
                foreach (var chunk in uniqueHashes.Chunk(ChunkSize))
                {
                    var cmd = connection.CreateCommand();
                    var placeholders = string.Join(",", chunk.Select((_, i) => $"$p{i}"));
                    
                    cmd.CommandText = $@"
                        SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                               created_at, is_synced, is_deleted
                        FROM clipboard_entries
                        WHERE content_hash IN ({placeholders})";

                    for (int i = 0; i < chunk.Length; i++)
                    {
                        cmd.Parameters.AddWithValue($"$p{i}", chunk[i]);
                    }

                    using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    // Process chunk results
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var entry = MapFromReader(reader);
                        // Use TryAdd safely
                        if (!result.ContainsKey(entry.ContentHash))
                        {
                            result[entry.ContentHash] = entry;
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get hash map");
                throw;
            }
        })).Unwrap();
    }

    public Task<Dictionary<string, ClipboardDbModel>> GetByIdsAsync(IEnumerable<string> ids)
    {
        return _db.StartNew((Func<Task<Dictionary<string, ClipboardDbModel>>>)(async () =>
        {
            var result = new Dictionary<string, ClipboardDbModel>();
            var uniqueIds = ids.Distinct().ToList();
            
            if (uniqueIds.Count == 0) return result;

            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                const int ChunkSize = 900; 
                
                foreach (var chunk in uniqueIds.Chunk(ChunkSize))
                {
                    var cmd = connection.CreateCommand();
                    var placeholders = string.Join(",", chunk.Select((_, i) => $"$p{i}"));
                    
                    cmd.CommandText = $@"
                        SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                               created_at, is_synced, is_deleted
                        FROM clipboard_entries
                        WHERE id IN ({placeholders})";

                    for (int i = 0; i < chunk.Length; i++)
                    {
                        cmd.Parameters.AddWithValue($"$p{i}", chunk[i]);
                    }

                    using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var entry = MapFromReader(reader);
                        if (!result.ContainsKey(entry.Id))
                        {
                            result[entry.Id] = entry;
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get entries by IDs");
                throw;
            }
        })).Unwrap();
    }


    // ---------------------------------------------------------
    // WRITES
    // ---------------------------------------------------------
    /// <summary>
    ///     Insert or update a single clipboard entry (convenience method)
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
    ///     Insert or update multiple clipboard entries with optimized batching.
    ///     Fires a single event after all changes complete.
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
                throw new ArgumentException("Entry with null/empty ID found", nameof(entries));
            if (string.IsNullOrEmpty(entry.Content))
                throw new ArgumentException($"Entry {entry.Id} has null/empty Content", nameof(entries));
            if (string.IsNullOrEmpty(entry.ContentHash))
                throw new ArgumentException($"Entry {entry.Id} has null/empty ContentHash", nameof(entries));
        }

        return _db.StartNew((Func<Task>)(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                using var tx = connection.BeginTransaction();
                foreach (var entry in entryList)
                {
                    var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT OR REPLACE INTO clipboard_entries
(id, content, content_hash, ciphertext, nonce, blob_version, created_at, is_synced, is_deleted)
VALUES ($id, $content, $hash, $cipher, $nonce, $ver, $created, $synced, $deleted)";
                    AddParams(cmd, entry);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                tx.Commit();

                NotifyObservers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert clipboard entries");
                throw;
            }
        })).Unwrap();
    }


    public Task MarkAsSyncedAsync(string id)
    {
        return _db.StartNew((Func<Task>)(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE clipboard_entries SET is_synced = 1 WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                
                var rowsAffected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                
                if (rowsAffected > 0)
                {
                    NotifyObservers();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to mark entry {id} as synced");
                throw;
            }
        })).Unwrap();
    }

    /// <summary>
    /// Marks an entry as deleted (tombstone). Sets is_deleted_remotely=1 and is_synced=0.
    /// </summary>
    public Task MarkDeletedAsync(string id)
    {
        return _db.StartNew((Func<Task>)(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE clipboard_entries 
                    SET is_deleted = 1, is_synced = 0 
                    WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                
                var rowsAffected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                
                if (rowsAffected > 0)
                {
                    _logger.LogInformation($"Entry {id} marked as deleted (tombstone)");
                    NotifyObservers();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to mark entry {id} as deleted");
                throw;
            }
        })).Unwrap();
    }


    public Task DeleteByIdAsync(string id)
    {
        return _db.StartNew((Func<Task>)(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM clipboard_entries WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                NotifyObservers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete entry by ID: {id}");
                throw;
            }
        })).Unwrap();
    }

    public Task ClearAllAsync()
    {
        return _db.StartNew((Func<Task>)(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM clipboard_entries";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                NotifyObservers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear all entries");
                throw;
            }
        })).Unwrap();
    }

    public Task RunInTransactionAsync(Func<Task> action)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        return _db.StartNew((Func<Task>)(async () =>
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                await connection.OpenAsync().ConfigureAwait(false);

                using var tx = connection.BeginTransaction();
                try
                {
                    await action().ConfigureAwait(false);
                    tx.Commit();
                    NotifyObservers();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute transaction");
                throw;
            }
        })).Unwrap();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
                _logger.LogWarning(
                    $"WAL mode not enabled. Current mode: {result?.ToString() ?? "null"}. This may impact concurrency performance.");
            else
                _logger.LogInformation("WAL mode enabled successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify WAL mode");
            throw;
        }
    }

    // ---------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------
    private void NotifyObservers()
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
        cmd.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$synced", entry.IsSynced ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted", entry.IsDeleted ? 1 : 0);
    }

    private static ClipboardDbModel MapFromReader(SqliteDataReader reader)
    {
        return new ClipboardDbModel
        {
            Id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Content = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            ContentHash = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Ciphertext = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Nonce = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            BlobVersion = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            CreatedAt = reader.IsDBNull(6)
                ? DateTime.UtcNow
                : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            IsSynced = reader.FieldCount > 7 && !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
            IsDeleted = reader.FieldCount > 8 && !reader.IsDBNull(8) && reader.GetInt32(8) == 1
        };
    }
}