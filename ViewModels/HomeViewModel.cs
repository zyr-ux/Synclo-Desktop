using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Models;
using Synclo.Services;
using Synclo.Services.ClipboardService;

namespace Synclo.ViewModels;

public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly IClipboardMonitor _clipboardMonitor;
    private readonly NotificationService _notificationService;
    private readonly ClipboardSyncService _clipboardSyncService;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private ObservableCollection<ClipboardDbModel> _historyEntries = new();
    [ObservableProperty] private bool _isLoading;

    public HomeViewModel(
        IClipboardMonitor clipboardMonitor,
        NotificationService notificationService,
        ClipboardSyncService clipboardSyncService
        )
    {
        _clipboardMonitor = clipboardMonitor;
        _notificationService = notificationService;
        _clipboardSyncService = clipboardSyncService;
        
        _clipboardSyncService.OnHistoryUpdated += OnHistoryUpdated;
        
        LoadInitialData();
    }

    private void OnHistoryUpdated()
    {
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var entries = await _clipboardSyncService.GetHistoryForUI();
            ApplyCollectionDiff(entries);
        });
    }

    private void ApplyCollectionDiff(IReadOnlyList<ClipboardDbModel> newEntries)
    {
        // Run on UI thread only - minimal edits to avoid UI flicker
        // Greedy algorithm: align existing collection with new list by moving, inserting, replacing, and trimming.
        var existing = HistoryEntries;

        // Iterate desired list and ensure each position matches
        for (int i = 0; i < newEntries.Count; i++)
        {
            var desired = newEntries[i];

            if (i < existing.Count)
            {
                var current = existing[i];
                if (current.Id == desired.Id)
                {
                    // Same item at this position: update if contents changed (replace object to respect immutability)
                    if (!AreEntriesEqual(current, desired))
                    {
                        var isDeleting = current.IsDeleting;
                        var replacement = desired.CopyWith();
                        replacement.IsDeleting = isDeleting;
                        existing[i] = replacement;
                    }
                }
                else
                {
                    // Try to find desired further down the list and move it up
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

                        // After move, check if content changed (replace if necessary)
                        var movedItem = existing[i];
                        if (!AreEntriesEqual(movedItem, desired))
                        {
                            var isDeleting = movedItem.IsDeleting;
                            var replacement = desired.CopyWith();
                            replacement.IsDeleting = isDeleting;
                            existing[i] = replacement;
                        }
                    }
                    else
                    {
                        // Not found - insert new item at this position
                        existing.Insert(i, desired);
                    }
                }
            }
            else
            {
                // Append any remaining new entries
                existing.Add(newEntries[i]);
            }
        }

        // Remove any excess items not present in the new list
        while (existing.Count > newEntries.Count)
        {
            existing.RemoveAt(existing.Count - 1);
        }
    }

    private static bool AreEntriesEqual(ClipboardDbModel a, ClipboardDbModel b)
    {
        if (a == null || b == null) return false;
        return a.Id == b.Id &&
               a.Content == b.Content &&
               a.ContentHash == b.ContentHash &&
               a.Ciphertext == b.Ciphertext &&
               a.Nonce == b.Nonce &&
               a.BlobVersion == b.BlobVersion &&
               a.IsRemoteDeleted == b.IsRemoteDeleted &&
               a.SyncedAt == b.SyncedAt &&
               a.CreatedAt == b.CreatedAt;
    }

    private async void LoadInitialData()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            
            var entries = await _clipboardSyncService.RefreshFromServerAsync(limit: 100);
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyCollectionDiff(entries.Where(e => !e.IsRemoteDeleted).ToList());
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to load clipboard history. Please try refreshing.";
            System.Diagnostics.Debug.WriteLine($"LoadInitialData error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshClipboardHistory()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            
            var entries = await _clipboardSyncService.RefreshFromServerAsync(limit: 100);
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyCollectionDiff(entries.Where(e => !e.IsRemoteDeleted).ToList());
            });
            
            _notificationService.ShowSuccess("Clipboard history refreshed.");
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to load clipboard history. Please try refreshing.";
            _notificationService.ShowError("Failed to refresh clipboard history");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ItemClicked(ClipboardDbModel entry)
    {
        try
        {
            if (entry == null || string.IsNullOrEmpty(entry.Content))
                return;

            // Don't allow copying deleted entries
            if (entry.IsRemoteDeleted)
                return;

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
        if (entry == null || string.IsNullOrEmpty(entry.Content))
            return;

        if (entry.IsRemoteDeleted || entry.IsDeleting)
            return;

        entry.IsDeleting = true;

        try
        {
            await _clipboardSyncService.DeleteClipboardEntryAsync(entry.Id);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"Failed to delete entry: {ex.Message}");
        }
        finally
        {
            entry.IsDeleting = false;
        }
    }


    public void Dispose()
    {
        _clipboardSyncService.OnHistoryUpdated -= OnHistoryUpdated;
    }
}