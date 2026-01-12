using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Synclo.Models;

namespace Synclo.Services.ClipboardService;

/// <summary>
/// SQLite implementation of clipboard repository.
/// Thread-safe database operations for clipboard entries.
/// </summary>
public class ClipboardRepository : IClipboardRepository, IDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public event Action? OnDataChanged;

    public ClipboardRepository()
    {
        // Database location: %APPDATA%/Synclo/clipboard.db (Windows) or equivalent
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var syncloDir = Path.Combine(appDataPath, "Synclo");
        Directory.CreateDirectory(syncloDir);
        _dbPath = Path.Combine(syncloDir, "clipboard.db");
    }

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var createTableCmd = connection.CreateCommand();
            createTableCmd.CommandText = @"
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
            ";
            await createTableCmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ClipboardDbModel>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var entries = new List<ClipboardDbModel>();
            
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                       is_remote_deleted, created_at, synced_at
                FROM clipboard_entries
                ORDER BY created_at DESC
            ";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(MapFromReader(reader));
            }

            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ClipboardDbModel>> GetAllAsync(int limit, int offset = 0)
    {
        await _lock.WaitAsync();
        try
        {
            var entries = new List<ClipboardDbModel>();
            
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                       is_remote_deleted, created_at, synced_at
                FROM clipboard_entries
                ORDER BY created_at DESC
                LIMIT $limit OFFSET $offset
            ";
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(MapFromReader(reader));
            }

            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ClipboardDbModel?> GetByIdAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                       is_remote_deleted, created_at, synced_at
                FROM clipboard_entries
                WHERE id = $id
            ";
            command.Parameters.AddWithValue("$id", id);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapFromReader(reader);
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ClipboardDbModel?> GetByHashAsync(string hash)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                       is_remote_deleted, created_at, synced_at
                FROM clipboard_entries
                WHERE content_hash = $hash
                ORDER BY created_at DESC
                LIMIT 1
            ";
            command.Parameters.AddWithValue("$hash", hash);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapFromReader(reader);
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(ClipboardDbModel entry)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO clipboard_entries 
                (id, content, content_hash, ciphertext, nonce, blob_version, 
                 is_remote_deleted, created_at, synced_at)
                VALUES ($id, $content, $content_hash, $ciphertext, $nonce, $blob_version, 
                        $is_remote_deleted, $created_at, $synced_at)
            ";
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$content", entry.Content);
            command.Parameters.AddWithValue("$content_hash", entry.ContentHash);
            command.Parameters.AddWithValue("$ciphertext", entry.Ciphertext);
            command.Parameters.AddWithValue("$nonce", entry.Nonce);
            command.Parameters.AddWithValue("$blob_version", entry.BlobVersion);
            command.Parameters.AddWithValue("$is_remote_deleted", entry.IsRemoteDeleted ? 1 : 0);
            command.Parameters.AddWithValue("$created_at", entry.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$synced_at", 
                entry.SyncedAt.HasValue ? entry.SyncedAt.Value.ToString("O") : (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
        
        // Fire event OUTSIDE the lock to prevent deadlock if subscribers call back into repository
        OnDataChanged?.Invoke();
    }

    public async Task MarkAsDeletedAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE clipboard_entries
                SET is_remote_deleted = 1
                WHERE id = $id
            ";
            command.Parameters.AddWithValue("$id", id);

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
        
        // Fire event OUTSIDE the lock to prevent deadlock
        OnDataChanged?.Invoke();
    }

    public async Task DeleteByIdAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM clipboard_entries WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
        
        // Fire event OUTSIDE the lock to prevent deadlock
        OnDataChanged?.Invoke();
    }

    public async Task DeleteAllMarkedAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM clipboard_entries
                WHERE is_remote_deleted = 1
            ";

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ClipboardDbModel>> GetUnsyncedAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var entries = new List<ClipboardDbModel>();
            
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, content, content_hash, ciphertext, nonce, blob_version, 
                       is_remote_deleted, created_at, synced_at
                FROM clipboard_entries
                WHERE synced_at IS NULL AND is_remote_deleted = 0
                ORDER BY created_at ASC
            ";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(MapFromReader(reader));
            }

            return entries;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM clipboard_entries";

            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
        
        // Fire event OUTSIDE the lock to prevent deadlock
        OnDataChanged?.Invoke();
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
        _lock.Dispose();
    }
}
