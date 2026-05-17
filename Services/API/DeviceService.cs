using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.Services.SecretsManager;
using Synclo.Services.Utilities;

namespace Synclo.Services.API;

public interface IDeviceService
{
    Task<List<DeviceModel>> GetDevicesAsync(CancellationToken ct = default);
    Task DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    Task<List<DeviceModel>> LoadAsync();
    Task SaveAsync(List<DeviceModel> devices);
    Task ClearAsync();
}

public sealed class DeviceService(IApiService api, ISettingsService settings, ISecureStorage secureStorage) : IDeviceService
{
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _cachedPath;

    public async Task<List<DeviceModel>> GetDevicesAsync(CancellationToken ct = default)
    {
        using var res = await api.GetAsync("/api/devices", ct);
        var content = await res.Content.ReadAsStringAsync(ct);
        
        // Check if device was deleted (unauthorized/forbidden)
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized || 
            res.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new DeviceNotFoundException("This device is no longer registered. It may have been removed remotely.");
        }
        
        if (!res.IsSuccessStatusCode)
            throw new ServerFailureException(content);

        var list = api.Deserialize<List<DeviceModel>>(content);
        var currentDeviceId = settings.Settings.device_id;
        foreach (var device in list)
        {
            device.IsThisDevice = device.device_id == currentDeviceId;
        }
        return list;
    }

    public async Task DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var res = await api.DeleteAsync($"/api/devices/{deviceId}", ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadAsStringAsync(ct);
            throw new ServerFailureException(error);
        }
    }

    public async Task<List<DeviceModel>> LoadAsync()
    {
        var path = await GetPathAsync();
        if (!File.Exists(path))
            return [];

        await _fileLock.WaitAsync();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var data = await JsonSerializer.DeserializeAsync<List<DeviceModel>>(fs, _options);
            if (data == null) return [];
            var currentDeviceId = settings.Settings.device_id;
            foreach (var device in data)
            {
                if (device != null)
                {
                    device.IsThisDevice = device.device_id == currentDeviceId;
                }
            }
            return data;
        }
        catch
        {
            return [];
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(List<DeviceModel> devices)
    {
        var path = await GetPathAsync();
        var tempPath = path + ".tmp";

        await _fileLock.WaitAsync();
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, devices, _options);
            }

            File.Move(tempPath, path, true);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DeviceCache] Failed to save cache: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            _fileLock.Release();
        }
    }

    public async Task ClearAsync()
    {
        var path = await GetPathAsync();

        await _fileLock.WaitAsync();
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            _cachedPath = null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<string> GetPathAsync()
    {
        if (_cachedPath != null) return _cachedPath;

        var email = await secureStorage.LoadAsync(AccountService.UserEmail);
        var identifier = "anonymous";
        if (!string.IsNullOrWhiteSpace(email))
        {
            var atIndex = email.IndexOf('@');
            identifier = atIndex > 0 ? email.Substring(0, atIndex) : email;
        }

        var safeName = SanitizeFileName(identifier);
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Synclo",
            "DeviceCache"
        );

        if (!Directory.Exists(baseDir))
            Directory.CreateDirectory(baseDir);

        _cachedPath = Path.Combine(baseDir, $"devices_{safeName}.json");
        return _cachedPath;
    }

    private static string SanitizeFileName(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars) input = input.Replace(c, '_');
        return input.ToLowerInvariant();
    }
}