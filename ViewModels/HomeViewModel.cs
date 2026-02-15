using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
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
    [ObservableProperty] private ObservableCollection<HistoryItemModel> _historyEntries = [];
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
        int newIndex = 0;

        // 1. Update/Insert loops
        // We iterate through the new batch and overlay it onto the existing list
        while (newIndex < newEntries.Count)
        {
            var desired = newEntries[newIndex];

            // If we exceeded existing list bounds, just add the rest
            if (newIndex >= existing.Count)
            {
                existing.Add(desired);
                newIndex++;
                continue;
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
                // Check if 'desired' (new item) exists later in the current list (Moved/Shifted down)
                // OR if 'current' (old item) exists later in the new batch (Moved/Shifted up - unlikely for history)
                
                // Strategy: prioritizing the NEW batch as truth for this range.
                // Does existing[newIndex] exist anywhere in the rest of newEntries?
                bool currentIsStillInNewBatch = false;
                for (int check = newIndex + 1; check < newEntries.Count; check++)
                {
                    if (newEntries[check].Id == current.Id)
                    {
                        currentIsStillInNewBatch = true;
                        break;
                    }
                }

                if (currentIsStillInNewBatch)
                {
                    // The current item IS in the new batch, but later. 
                    // This implies 'desired' is a NEW insert before it.
                    existing.Insert(newIndex, desired);
                    newIndex++;
                }
                else
                {
                    // The current item is NOT in the new batch.
                    // THIS IS TRICKY: 
                    // If we only fetched a partial page, we cannot validly say "It was deleted".
                    // It might just be pushed out of the page.
                    // BUT, if we assume the newEntries represents the TOP N items:
                    // If 'current' is NOT in newEntries, does that mean it's deleted? Or pushed down?
                    
                    // Safe heuristics for "Top of List" updates:
                    // If 'desired' is NOT found in existing list, it's an Insert.
                    // If 'desired' IS found in existing list (index J), move it to newIndex.
                    
                    var indexInExisting = -1;
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
                        // Found logic: It moved up.
                        existing.Move(indexInExisting, newIndex);
                        if (!AreEntriesEqual(existing[newIndex], desired))
                        {
                            existing[newIndex] = desired;
                        }
                        newIndex++;
                    }
                    else
                    {
                        // New item insert
                        existing.Insert(newIndex, desired);
                        newIndex++;
                    }
                }
            }
        }
        
        // Note: We DO NOT truncate 'existing' list here. 
        // We keep the infinite scroll buffer. 
        // This solves "Inefficient List Updates" by not reloading the whole tail.
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
                foreach (var entry in newEntries)
                {
                    if (!HistoryEntries.Any(x => x.Id == entry.Id))
                    {
                        HistoryEntries.Add(entry);
                    }
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