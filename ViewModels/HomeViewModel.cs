using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Models;
using Synclo.Services;
using Synclo.Services.API;
using Synclo.Services.ClipboardMonitor;
using Synclo.Services.ClipboardService;
using Synclo.Services.Utilities;

namespace Synclo.ViewModels;

public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly IClipboardMonitor _clipboardMonitor;
    private readonly INotificationService _notificationService;
    private readonly IClipboardSyncService _clipboardSyncService;
    private readonly IAccountService _accountService;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private CancellationTokenSource? _updateCts;
    private bool _isLoggedIn;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private AvaloniaList<HistoryItemModel> _historyEntries = new();
    [ObservableProperty] private string? _homeStatusMessage;
    
    private int PageSize => _settingsService.Settings.sync_page_size;
    private bool _isLoadingMore;
    

    public HomeViewModel(
        IClipboardMonitor clipboardMonitor,
        INotificationService notificationService,
        IClipboardSyncService clipboardSyncService,
        IAccountService accountService,
        ISettingsService settingsService)
    {
        _clipboardMonitor = clipboardMonitor;
        _notificationService = notificationService;
        _clipboardSyncService = clipboardSyncService;
        _accountService = accountService;
        _settingsService = settingsService;

        _clipboardSyncService.OnHistoryUpdated += OnHistoryUpdated;
        _accountService.OnLogin += async () => await UpdateHomeStatusAsync();
        _accountService.OnLogout += async () =>
        {
            HistoryEntries.Clear();
            await UpdateHomeStatusAsync();
        };

        Task.Run(async () =>
        {
            await RefreshDataAsync(silent: true);
            if (ErrorMessage == null)
            {
                _notificationService.ShowSuccess("Clipboard history refreshed.");
            }
        });
    }

    // Debounces history updates and refreshes the UI with new clipboard entries
    private void OnHistoryUpdated()
    {
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (!await _updateLock.WaitAsync(0))
                return;
            
            try
            {
                await Task.Delay(50, token);
                if (token.IsCancellationRequested) return;

                // Optimization: Fetch only one page of updates instead of full history
                // This keeps the UI responsive even with thousands of items.
                // New items are inserted/merged at the top.
                var entries = await _clipboardSyncService.GetHistoryForUI(PageSize, 0);
                if (token.IsCancellationRequested) return;

                MergeNewEntries(entries);
                await UpdateHomeStatusAsync();
            }
            catch (OperationCanceledException) { }
            finally
            {
                _updateLock.Release();
            }
        });
    }

    // Efficiently syncs the UI collection with new entries, handling insertions, updates, moves, and removals
    // Merges new entries into the start of the list without clearing the rest
    private void MergeNewEntries(IReadOnlyList<HistoryItemModel> newEntries)
    {
        var existing = HistoryEntries;
        int oldPageBoundary = Math.Min(PageSize, existing.Count);
        var newIdSet = new HashSet<string>(newEntries.Select(e => e.Id));
        var removedIds = new HashSet<string>();
        for (int i = 0; i < oldPageBoundary; i++)
        {
            if (!newIdSet.Contains(existing[i].Id))
                removedIds.Add(existing[i].Id);
        }

        int newIndex = 0;

        while (newIndex < newEntries.Count)
        {
            var desired = newEntries[newIndex];

            // If we exceeded existing list bounds, just add the rest
            if (newIndex >= existing.Count)
            {
                // Optimization: use AddRange for the remainder
                // We convert to list/array to avoid multiple enumerations if needed, though Skip is fine here
                var remaining = new List<HistoryItemModel>();
                for (int i = newIndex; i < newEntries.Count; i++) remaining.Add(newEntries[i]);
                
                existing.AddRange(remaining);
                break;
            }

            var current = existing[newIndex];

            if (current.Id == desired.Id)
            {
                // IDs match, just check for updates
                if (!AreEntriesEqual(current, desired))
                {
                    existing[newIndex] = desired;
                }
                newIndex++;
            }
            else
            {
                // Mismatch.
                // Robust Strategy: Ensure 'desired' is at 'newIndex'.
                // 1. Is 'desired' already in the list (moved)? -> Move it here.
                // 2. Is it new? -> Insert it here.
                
                var indexInExisting = -1;
                
                // Optimization: Scan forward to find if the item moved up.
                // We limit the scan if needed, but for correctness we scan.
                // (Performance note: scanning 'existing' is O(N), but done at most M times where M=PageSize)
                for (int j = newIndex + 1; j < existing.Count; j++)
                {
                    if (existing[j].Id == desired.Id)
                    {
                        indexInExisting = j;
                        break;
                    }
                }

                if (indexInExisting != -1)
                {
                    // It was found later, so it moved up (or current moved down)
                    existing.Move(indexInExisting, newIndex);
                    
                    // Check for content updates after move
                    if (!AreEntriesEqual(existing[newIndex], desired))
                    {
                        existing[newIndex] = desired;
                    }
                }
                else
                {
                    // Not found, so it's a new item
                    existing.Insert(newIndex, desired);
                }
                
                newIndex++;
            }
        }

        if (removedIds.Count > 0)
        {
            for (int i = existing.Count - 1; i >= newEntries.Count; i--)
            {
                if (removedIds.Contains(existing[i].Id))
                    existing.RemoveAt(i);
            }
        }
    }

    // Checks if two clipboard entries are equal based on key properties
    private static bool AreEntriesEqual(HistoryItemModel a, HistoryItemModel b)
    {
        return a.Id == b.Id &&
               a.Content == b.Content &&
               a.ContentHash == b.ContentHash &&
               a.IsDeleting == b.IsDeleting; 
    }
    
    // Triggers a manual refresh of the clipboard history
    [RelayCommand]
    private async Task RefreshClipboardHistory()
    {
        await RefreshDataAsync(silent: false);
    }

    // Refreshes clipboard history from the server with optional UI feedback
    private async Task RefreshDataAsync(bool silent)
    {
        try
        {
            if (!silent) IsLoading = true;
            ErrorMessage = null;
            
            var entries = await _clipboardSyncService.RefreshFromServerAsync(limit: PageSize);

            if (!silent) 
                _notificationService.ShowSuccess("Clipboard history refreshed.");
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to load clipboard history.";
            if (!silent) 
                _notificationService.ShowError("Refresh failed: " + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Copies the selected clipboard entry to the current clipboard
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ItemClicked(HistoryItemModel entry)
    {
        if (string.IsNullOrEmpty(entry.Content)) return;

        try
        {
            await _clipboardMonitor.SetClipboardTextAsync(entry.Content);
            _notificationService.ShowSuccess("Copied to clipboard.");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message);
        }
    }
    
    // Loads the next batch of clipboard history entries
    [RelayCommand]
    private async Task LoadMore()
    {
        if (_isLoadingMore || IsLoading) return;

        try
        {
            _isLoadingMore = true;
            var nextOffset = HistoryEntries.Count;
            var newEntries = await _clipboardSyncService.GetHistoryForUI(PageSize, nextOffset);

            if (newEntries.Count > 0)
            {
                // Optimization: Use HashSet to filter duplicates O(N) instead of O(N*M)
                var existingIds = new HashSet<string>(HistoryEntries.Select(x => x.Id));
                var toAdd = new List<HistoryItemModel>();

                foreach (var entry in newEntries)
                {
                    if (existingIds.Add(entry.Id)) // Returns true if added (so it was new)
                    {
                        toAdd.Add(entry);
                    }
                }
                
                if (toAdd.Count > 0)
                {
                    HistoryEntries.AddRange(toAdd);
                }
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError("Failed to load more items: " + ex.Message);
        }
        finally
        {
            _isLoadingMore = false;
        }
    }
    
    // Deletes a clipboard entry and marks it as deleting
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteItemClicked(HistoryItemModel entry)
    {
        if (entry.IsDeleting) return;

        try
        {
            entry.IsDeleting = true; 
            
            await _clipboardSyncService.DeleteClipboardEntryAsync(entry.Id);
        }
        catch (Exception)
        {
            entry.IsDeleting = false;
        }
    }

    // Updates the home page status message based on login state and history entries
    private async Task UpdateHomeStatusAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _isLoggedIn = await _accountService.IsAuthenticatedAsync();

            switch (_isLoggedIn)
            {
                case false:
                    HomeStatusMessage = "You are not logged in. Pls log in to use Synclo!";
                    break;
                case true when HistoryEntries.Count == 0:
                    HomeStatusMessage = "Looks like this is empty! Copy something rn!";
                    break;
                default:
                    HomeStatusMessage = null;
                    break;
            }
        });
    }

    // Cleans up resources and unregisters event handlers
    public void Dispose()
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateLock.Dispose();
        _clipboardSyncService.OnHistoryUpdated -= OnHistoryUpdated;
    }
}