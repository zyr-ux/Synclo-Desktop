using System;
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

namespace Synclo.ViewModels;

public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly IClipboardMonitor _clipboardMonitor;
    private readonly INotificationService _notificationService;
    private readonly IClipboardSyncService _clipboardSyncService;
    private readonly IAccountService _accountService;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private CancellationTokenSource? _updateCts;
    private bool _isLoggedIn;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private ObservableCollection<ClipboardDbModel> _historyEntries = new();
    [ObservableProperty] private string? _homeStatusMessage;
    [ObservableProperty] private bool _homeStatusMessageVisibility;
    

    public HomeViewModel(
        IClipboardMonitor clipboardMonitor,
        INotificationService notificationService,
        IClipboardSyncService clipboardSyncService,
        IAccountService accountService)
    {
        _clipboardMonitor = clipboardMonitor;
        _notificationService = notificationService;
        _clipboardSyncService = clipboardSyncService;
        _accountService = accountService;
        
        _clipboardSyncService.OnHistoryUpdated += OnHistoryUpdated;
        _accountService.OnLogin += async () => await UpdateHomeStatusAsync();
        _accountService.OnLogout += async () => await UpdateHomeStatusAsync();
        
        
        // Fire and forget initial load with delay to ensure services are ready
        Task.Run(async () =>
        {
            await Task.Delay(1000);
            await RefreshDataAsync(silent: true);
            // Only show success if no error occurred
            if (ErrorMessage == null)
            {
                _notificationService.ShowSuccess("Clipboard history refreshed.");
            }
            await UpdateHomeStatusAsync();
        });
    }

    private void OnHistoryUpdated()
    {
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        var token = _updateCts.Token;

        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Try to acquire lock without waiting - if another update is in progress, skip this one
            if (!await _updateLock.WaitAsync(0))
                return;
            
            try
            {
                await Task.Delay(50, token); // Debounce
                if (token.IsCancellationRequested) return;

                var entries = await _clipboardSyncService.GetHistoryForUI();
                if (token.IsCancellationRequested) return; // Check again after async call

                ApplyCollectionDiff(entries);
                await UpdateHomeStatusAsync();
            }
            catch (OperationCanceledException) { /* Ignored */ }
            finally
            {
                _updateLock.Release();
            }
        });
    }

    private void ApplyCollectionDiff(IReadOnlyList<ClipboardDbModel> newEntries)
    {
        // 1. Optimize for "New Item at Top" scenario (prevents flicker)
        if (newEntries.Count > 0 && HistoryEntries.Count > 0)
        {
            // If the very first item is different, it's likely a new copy.
            // Rebuilding the whole list is often smoother visually than inserting at 0 
            // and letting the UI shift everything down.
            if (newEntries[0].Id != HistoryEntries[0].Id)
            {
                HistoryEntries.Clear();
                foreach (var entry in newEntries) HistoryEntries.Add(entry);
                return;
            }
        }

        // 2. Standard synchronization
        var existing = HistoryEntries;
        
        // Update/Insert existing items
        for (int i = 0; i < newEntries.Count; i++)
        {
            var desired = newEntries[i];
            
            // If we are past the end of existing, just add
            if (i >= existing.Count)
            {
                existing.Add(desired);
                continue;
            }

            var current = existing[i];

            // Match at current position?
            if (current.Id == desired.Id)
            {
                // Update content if changed
                if (!AreEntriesEqual(current, desired))
                {
                    existing[i] = desired; // Replace to trigger UI update
                }
            }
            else
            {
                // Not a match. Is the desired item further down? (Moved up)
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
                    // Move it up
                    existing.Move(foundIndex, i);
                    // Check if content update is needed after move
                    if (!AreEntriesEqual(existing[i], desired))
                    {
                        existing[i] = desired;
                    }
                }
                else
                {
                    // New item inserted here
                    existing.Insert(i, desired);
                }
            }
        }

        // 3. Remove excess items
        while (existing.Count > newEntries.Count)
        {
            existing.RemoveAt(existing.Count - 1);
        }
    }

    private static bool AreEntriesEqual(ClipboardDbModel a, ClipboardDbModel b)
    {
        return a.Id == b.Id &&
               a.Content == b.Content &&
               a.ContentHash == b.ContentHash &&
               a.IsDeleting == b.IsDeleting; 
    }
    
    [RelayCommand]
    private async Task RefreshClipboardHistory()
    {
        await RefreshDataAsync(silent: false);
    }

    private async Task RefreshDataAsync(bool silent)
    {
        try
        {
            if (!silent) IsLoading = true;
            ErrorMessage = null;
            
            var entries = await _clipboardSyncService.RefreshFromServerAsync(limit: 100);
            
            // UI update is handled by OnHistoryUpdated event triggered within RefreshFromServerAsync

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

    private async Task UpdateHomeStatusAsync()
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
    }

    public void Dispose()
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateLock.Dispose();
        _clipboardSyncService.OnHistoryUpdated -= OnHistoryUpdated;
    }
}