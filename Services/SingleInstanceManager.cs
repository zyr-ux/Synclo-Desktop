using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Synclo.Services;

public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexNameWindows = @"Global\Synclo.SingleInstance.Mutex";
    private const string MutexNameUnix = "Synclo.SingleInstance.Mutex";
    private const string PipeName = "Synclo.SingleInstance.IPC";

    private const byte CommandShowWindow = 0x01;

    private const int MaxRetryAttempts = 5;
    private const int RetryDelayMs = 200;
    private const int ConnectTimeoutMs = 400;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<SingleInstanceManager>? _logger;
    private readonly bool _isPrimary;
    private Task? _acceptLoopTask;
    private bool _disposed;

    public bool IsPrimary => _isPrimary;

    public SingleInstanceManager(ILogger<SingleInstanceManager>? logger = null)
    {
        _logger = logger;

        var mutexName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? MutexNameWindows
            : MutexNameUnix;

        _mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        _isPrimary = createdNew;

        if (_isPrimary)
        {
            _acceptLoopTask = Task.Run(AcceptLoopAsync);
        }
    }

    /// <summary>
    /// Call from secondary instance before exiting.
    /// </summary>
    public bool SignalPrimary()
    {
        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                using var client = CreatePipeClient();
                client.Connect(ConnectTimeoutMs);

                client.WriteByte(CommandShowWindow);
                client.Flush();
                return true;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException) when (attempt < MaxRetryAttempts)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to signal primary instance");
                return false;
            }

            if (attempt < MaxRetryAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = CreatePipeServer();
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                _ = Task.Run(() => HandleClientAsync(server), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _logger?.LogError(ex, "IPC accept loop error");

                try
                {
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server)
    {
        await using (server.ConfigureAwait(false))
        {
            int command = server.ReadByte();
            if (command == CommandShowWindow)
            {
                ActivateMainWindow();
            }
        }
    }

    private static NamedPipeClientStream CreatePipeClient()
    {
        return new NamedPipeClientStream(
            serverName: ".",
            pipeName: PipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.None);
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pipeSecurity = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;

            if (user != null)
            {
                pipeSecurity.AddAccessRule(
                    new PipeAccessRule(
                        user,
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                pipeName: PipeName,
                direction: PipeDirection.In,
                maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                transmissionMode: PipeTransmissionMode.Byte,
                options: PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: pipeSecurity);
        }

        return new NamedPipeServerStream(
            pipeName: PipeName,
            direction: PipeDirection.In,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous);
    }

    private static void ActivateMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = desktop.MainWindow;
                if (window == null) return;

                window.WindowState = WindowState.Normal;
                window.Show();

                // Windows foreground workaround
                window.Topmost = true;
                window.Activate();
                window.Topmost = false;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try
        {
            _acceptLoopTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
        }

        _cts.Dispose();

        if (_isPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }
        }

        _mutex.Dispose();
    }
}