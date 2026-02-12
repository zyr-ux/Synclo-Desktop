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
    [ObservableProperty] private ObservableCollection<ClipboardDbModel> _historyEntries = [];
    [ObservableProperty] private string? _homeStatusMessage;
    [ObservableProperty] private bool _homeStatusMessageVisibility;
    
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
            await Task.Delay(500);
            await RefreshDataAsync(silent: true);
            if (ErrorMessage == null)
            {
                _notificationService.ShowSuccess("Clipboard history refreshed.");
            }
            await UpdateHomeStatusAsync();
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

                var loadCount = Math.Max(HistoryEntries.Count, PageSize);
                var entries = await _clipboardSyncService.GetHistoryForUI(loadCount, 0);
                if (token.IsCancellationRequested) return;

                ApplyCollectionDiff(entries);
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
    private void ApplyCollectionDiff(IReadOnlyList<ClipboardDbModel> newEntries)
    {
        if (newEntries.Count > 0 && HistoryEntries.Count > 0)
        {
            if (newEntries[0].Id != HistoryEntries[0].Id)
            {
                HistoryEntries.Clear();
                foreach (var entry in newEntries) HistoryEntries.Add(entry);
                return;
            }
        }

        var existing = HistoryEntries;
        
        for (int i = 0; i < newEntries.Count; i++)
        {
            var desired = newEntries[i];
            
            if (i >= existing.Count)
            {
                existing.Add(desired);
                continue;
            }

            var current = existing[i];

            if (current.Id == desired.Id)
            {
                if (!AreEntriesEqual(current, desired))
                {
                    existing[i] = desired;
                }
            }
            else
            {
                var foundIndex = -1;
                for (int j = i + 1; j < existing.Count; j++)
                {
                    if (existing[j].Id == desired.Id)
                    {
                        foundIndex = j;
                        break;
                    }
                }

                if (foundIndex != -1)
                {
                    existing.Move(foundIndex, i);
                    if (!AreEntriesEqual(existing[i], desired))
                    {
                        existing[i] = desired;
                    }
                }
                else
                {
                    existing.Insert(i, desired);
                }
            }
        }

        while (existing.Count > newEntries.Count)
        {
            existing.RemoveAt(existing.Count - 1);
        }
    }

    // Checks if two clipboard entries are equal based on key properties
    private static bool AreEntriesEqual(ClipboardDbModel a, ClipboardDbModel b)
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
    private async Task ItemClicked(ClipboardDbModel entry)
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
    private async Task DeleteItemClicked(ClipboardDbModel entry)
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
                    HomeStatusMessageVisibility = true;
                    break;
                case true when HistoryEntries.Count == 0:
                    HomeStatusMessage = "Looks like this is empty! Copy something rn!";
                    HomeStatusMessageVisibility = true;
                    break;
                default:
                    HomeStatusMessageVisibility = false;
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