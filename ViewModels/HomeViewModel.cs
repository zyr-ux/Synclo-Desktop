using System;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly IClipboardRepository _clipboardRepository;
    private readonly NotificationService _notificationService;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private ObservableCollection<ClipboardDbModel> _historyEntries = new();
    [ObservableProperty] private bool _isLoading;

    public HomeViewModel(
        IClipboardMonitor clipboardMonitor,
        IClipboardRepository clipboardRepository,
        NotificationService notificationService)
    {
        _clipboardMonitor = clipboardMonitor;
        _clipboardRepository = clipboardRepository;
        _notificationService = notificationService;
        
        // Subscribe to database changes for real-time updates
        _clipboardRepository.OnDataChanged += OnDatabaseChanged;
        
        // Load initial data
        _ = LoadClipboardHistoryAsync();
    }

    private void OnDatabaseChanged()
    {
        // Reload data when database changes (from WebSocket or local sync)
        _ = LoadClipboardHistoryAsync();
    }

    private async Task LoadClipboardHistoryAsync()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            // Use pagination to limit memory usage (load most recent 100 entries)
            var entries = await _clipboardRepository.GetAllAsync(limit: 100);
            var filteredEntries = entries.Where(e => !e.IsRemoteDeleted).ToList();
            
            // Update observable collection on UI thread (thread-safe)
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                HistoryEntries.Clear();
                foreach (var entry in filteredEntries)
                {
                    HistoryEntries.Add(entry);
                }
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _notificationService.ShowError(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshClipboardHistory()
    {
        await LoadClipboardHistoryAsync();
        _notificationService.ShowSuccess("Clipboard history refreshed.");
    }

    [RelayCommand]
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

    public void Dispose()
    {
        _clipboardRepository.OnDataChanged -= OnDatabaseChanged;
    }
}