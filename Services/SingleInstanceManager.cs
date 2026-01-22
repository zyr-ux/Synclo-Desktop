using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Synclo.Services;

public sealed class SingleInstanceManager : IDisposable
{
    public event Action? SignalReceived;
    private const string MutexNameWindows = @"Global\Synclo.SingleInstance.Mutex";
    private const string MutexNameUnix = "Synclo.SingleInstance.Mutex";
    private const string PipeName = "Synclo.SingleInstance.IPC";
    private const byte CommandShowWindow = 0x01;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<SingleInstanceManager>? _logger;

    private Task? _acceptLoopTask;
    private volatile bool _disposed;

    public bool IsPrimary { get; }

    public SingleInstanceManager(ILogger<SingleInstanceManager>? logger = null)
    {
        _logger = logger;

        var mutexName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? MutexNameWindows
            : MutexNameUnix;

        _mutex = new Mutex(false, mutexName);

        try
        {
            IsPrimary = _mutex.WaitOne(TimeSpan.Zero, false);
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed — we are now primary
            IsPrimary = true;
        }

        if (IsPrimary)
        {
            _acceptLoopTask = Task.Run(AcceptLoopAsync);
        }
    }

    /// <summary>
    /// Call from secondary instance before exiting.
    /// </summary>
    public bool SignalPrimary()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            client.Connect(1500);
            client.WriteByte(CommandShowWindow);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to signal primary instance");
            return false;
        }
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

                _ = HandleClientAsync(server, _cts.Token);
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
                await Task.Delay(100, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream server,
        CancellationToken ct)
    {
        await using (server.ConfigureAwait(false))
        {
            var buffer = new byte[1];
            var read = await server.ReadAsync(buffer, 0, 1, ct).ConfigureAwait(false);

            if (read == 1 && buffer[0] == CommandShowWindow)
            {
                SignalReceived?.Invoke();
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;

            if (user != null)
            {
                security.AddAccessRule(
                    new PipeAccessRule(
                        user,
                        PipeAccessRights.ReadWrite,
                        AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);
        }

        return new NamedPipeServerStream(
            PipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }



    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try
        {
            _acceptLoopTask?.Wait(300);
        }
        catch
        {
        }

        _cts.Dispose();

        try
        {
            if (IsPrimary)
                _mutex.ReleaseMutex();
        }
        catch
        {
        }

        _mutex.Dispose();
    }
}
