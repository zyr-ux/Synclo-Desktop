using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Synclo.Models;

namespace Synclo.Services.ClipboardService;

/// <summary>
/// Repository interface for clipboard database operations.
/// Provides abstraction over SQLite storage for clipboard entries.
/// </summary>
public interface IClipboardRepository
{
    /// <summary>
    /// Initialize database schema and connection
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Fetch all clipboard entries from database
    /// </summary>
    Task<List<ClipboardDbModel>> GetAllAsync();
    
    /// <summary>
    /// Fetch clipboard entries with pagination (most recent first)
    /// </summary>
    /// <param name="limit">Maximum number of entries to return</param>
    /// <param name="offset">Number of entries to skip</param>
    Task<List<ClipboardDbModel>> GetAllAsync(int limit, int offset = 0);
    
    /// <summary>
    /// Fetch clipboard entry by server ID
    /// </summary>
    Task<ClipboardDbModel?> GetByIdAsync(string id);
    
    /// <summary>
    /// Fetch clipboard entry by content hash
    /// </summary>
    Task<ClipboardDbModel?> GetByHashAsync(string hash);
    
    /// <summary>
    /// Insert or update clipboard entry
    /// </summary>
    Task UpsertAsync(ClipboardDbModel entry);
    
    /// <summary>
    /// Mark entry as remotely deleted (soft delete for runtime)
    /// </summary>
    Task MarkAsDeletedAsync(string id);
    
    /// <summary>
    /// Permanently delete a specific entry by ID
    /// </summary>
    Task DeleteByIdAsync(string id);
    
    /// <summary>
    /// Permanently delete all entries marked as remotely deleted (cleanup on shutdown)
    /// </summary>
    Task DeleteAllMarkedAsync();
    
    /// <summary>
    /// Fetches entries that were created locally but never synced (SyncedAt == null)
    /// </summary>
    Task<List<ClipboardDbModel>> GetUnsyncedAsync();
    
    /// <summary>
    /// Wipe entire database (used on logout or cold start)
    /// </summary>
    Task ClearAllAsync();
    
    /// <summary>
    /// Event fired when database data changes (for ViewModel updates)
    /// </summary>
    event Action? OnDataChanged;
}
