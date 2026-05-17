using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Synclo.Services.API;
using Synclo.Services.ClipboardService;
using Synclo.ViewModels;

namespace Synclo.Services.Utilities;

public interface IAppBootstrapper
{
    void Initialize(IServiceProvider services);
    Task ShutdownAsync();
}

public sealed class AppBootstrapper : IAppBootstrapper
{
    private IServiceProvider? _services;

    public void Initialize(IServiceProvider services)
    {
        _services = services;

        var accountService = services.GetRequiredService<IAccountService>();
        var webSocketService = services.GetRequiredService<IWebSocketService>();
        var refreshTokenService = services.GetRequiredService<IRefreshTokenService>();

        // Wire up event connections
        refreshTokenService.SessionExpired += OnSessionExpired;
        accountService.OnLogout += webSocketService.DisconnectAsync;

        // Initialize clipboard subsystem asynchronously on the UI thread with proper sequencing
        var clipboardRepository = services.GetRequiredService<IClipboardRepository>();
        var clipboardSyncService = services.GetRequiredService<IClipboardSyncService>();
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await clipboardRepository.InitializeAsync();
            await clipboardSyncService.InitializeAsync();
        });
    }

    private void OnSessionExpired()
    {
        if (_services == null) return;
        var accountService = _services.GetRequiredService<IAccountService>();
        _ = accountService.LogoutAsync();
    }

    public async Task ShutdownAsync()
    {
        if (_services == null) return;

        // Graceful cleanup of registered services
        var clipboardSyncService = _services.GetRequiredService<IClipboardSyncService>();
        await clipboardSyncService.ShutdownAsync();

        var webSocketService = _services.GetRequiredService<IWebSocketService>();
        webSocketService.Dispose();

        var apiService = _services.GetRequiredService<IApiService>();
        apiService.Dispose();

        var instanceManager = _services.GetRequiredService<SingleInstanceManager>();
        instanceManager.Dispose();

        var mainVm = _services.GetRequiredService<MainWindowViewModel>();
        mainVm.Dispose();
    }
}
