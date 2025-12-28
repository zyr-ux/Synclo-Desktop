using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.SecretsManager;

namespace Synclo.Services;

public sealed class DeviceCacheService
{
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _options;
    private string? _cachedPath;

    public DeviceCacheService()
    {
        _options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    // Resolves the secure path based on the logged-in user's email.
    private async Task<string> GetPathAsync()
    {
        if (_cachedPath != null) return _cachedPath;

        // 1. Load email from Secure Storage
        var email = await SecureStorage.LoadAsync(AuthService.UserEmail);

        // 2. Extract the part before the '@'
        var identifier = "anonymous";
        if (!string.IsNullOrWhiteSpace(email))
        {
            var atIndex = email.IndexOf('@');
            identifier = atIndex > 0 ? email.Substring(0, atIndex) : email;
        }

        // 3. Sanitize (Replace invalid chars with _)
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

    public async Task<List<DeviceModel>> LoadAsync()
    {
        var path = await GetPathAsync();

        if (!File.Exists(path))
            return new List<DeviceModel>();

        await _fileLock.WaitAsync();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var data = await JsonSerializer.DeserializeAsync<List<DeviceModel>>(fs, _options);
            return data ?? new List<DeviceModel>();
        }
        catch
        {
            return new List<DeviceModel>();
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
            // Write to temporary file first for atomicity
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, devices, _options);
            }

            // Atomic move
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

            _cachedPath = null; // Reset path so it's re-evaluated if a new user logs in
        }
        finally
        {
            _fileLock.Release();
        }
    }
}