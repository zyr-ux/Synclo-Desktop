using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Synclo.Models;
using Synclo.Services;

namespace Synclo.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly ClipboardService _clipboardService;
    private readonly NotificationService _notificationService;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private ObservableCollection<ClipboardEntry> _historyEntries = new();
    [ObservableProperty] private bool _isLoading;

    public HomeViewModel(ClipboardService clipboardService,
        NotificationService notificationService)
    {
        _clipboardService = clipboardService;
        _notificationService = notificationService;
        _ = RefreshClipboardHistory();
    }

    [RelayCommand]
    private async Task RefreshClipboardHistory()
    {
        try
        {
            IsLoading = true;
            ErrorMessage = null;

            var history = await _clipboardService.GetClipboardHistoryAsync();
            HistoryEntries.Clear();
            foreach (var entry in history.history) HistoryEntries.Add(entry);
            _notificationService.ShowSuccess("Clipboard history refreshed.");
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
    private async Task ItemClicked(ClipboardEntry entry)
    {
        try
        {
            if (entry == null || string.IsNullOrEmpty(entry.plaintext))
                return;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(entry.plaintext);
                _notificationService.ShowSuccess("Copied to clipboard.");
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message);
        }
    }
}