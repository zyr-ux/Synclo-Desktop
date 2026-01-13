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
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var entries = _clipboardSyncService.GetCachedHistoryForUI();
            HistoryEntries.Clear();
            foreach (var entry in entries)
            {
                HistoryEntries.Add(entry);
            }
        });
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
                HistoryEntries.Clear();
                foreach (var entry in entries.Where(e => !e.IsRemoteDeleted))
                {
                    HistoryEntries.Add(entry);
                }
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
                HistoryEntries.Clear();
                foreach (var entry in entries.Where(e => !e.IsRemoteDeleted))
                {
                    HistoryEntries.Add(entry);
                }
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