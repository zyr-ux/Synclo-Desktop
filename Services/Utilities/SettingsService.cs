using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Synclo.Models;
using Synclo.Services.SecretsManager;

namespace Synclo.Services.Utilities;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
    event Action<AppSettings>? SettingsChanged;
    Task LoadServerUrlAsync();
    Task SaveServerUrlAsync(string url);
    Task DeleteServerUrlAsync();
    string GetAbsoluteUrl(string url);
    bool TryNormalizeServerUrl(string? raw, out string normalized, out string? error);
}

public sealed class SettingsService : ISettingsService
{
    private const string FileName = "settings.json";
    private readonly string _path;
    private readonly ISecureStorage _secureStorage;
    public AppSettings Settings { get; }
    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(ISecureStorage secureStorage)
    {
        _secureStorage = secureStorage;
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Synclo", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Settings = Load();
    }
    
    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_path, json);
        SettingsChanged?.Invoke(Settings);
    }

    public async Task LoadServerUrlAsync()
    {
        var url = await _secureStorage.LoadAsync(Constants.ServerUrl);
        if (!string.IsNullOrEmpty(url))
        {
            Settings.ServerUrl = url;
        }
    }

    public async Task SaveServerUrlAsync(string url)
    {
        Settings.ServerUrl = url;
        await _secureStorage.SaveAsync(Constants.ServerUrl, url);
        Save();
    }

    public async Task DeleteServerUrlAsync()
    {
        Settings.ServerUrl = AppSettings.DefaultServerUrl;
        await _secureStorage.DeleteAsync(Constants.ServerUrl);
        Save();
    }

    public string GetAbsoluteUrl(string url)
    {
        var serverUrl = Settings.ServerUrl.TrimEnd('/');
        var baseAddress = $"{serverUrl}/api/v1/";
        var relative = url.TrimStart('/');
        return relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               ? relative
               : $"{baseAddress}{relative}";
    }

    public bool TryNormalizeServerUrl(string? raw, out string normalized, out string? error)
    {
        var input = raw?.Trim() ?? "";

        // Blank input or explicit default → fall back to the canonical default
        if (string.IsNullOrWhiteSpace(input))
        {
            normalized = AppSettings.DefaultServerUrl;
            error = null;
            return true;
        }

        // If the user typed the default URL exactly, accept it as-is
        if (string.Equals(input, AppSettings.DefaultServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            normalized = AppSettings.DefaultServerUrl;
            error = null;
            return true;
        }

        // Prepend https:// when the user omits the scheme
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            input = "https://" + input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var tempUri) ||
            (tempUri.Scheme != Uri.UriSchemeHttp && tempUri.Scheme != Uri.UriSchemeHttps))
        {
            normalized = "";
            error = "Invalid URL format. Please enter a valid HTTP/HTTPS URL.";
            return false;
        }

        input = tempUri.ToString().TrimEnd('/');

        // Strip a trailing /api/v1 suffix so users can paste the full API endpoint URL
        if (input.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            input = input[..^"/api/v1".Length].TrimEnd('/');

        if (!Uri.TryCreate(input, UriKind.Absolute, out var finalUri) ||
            (finalUri.Scheme != Uri.UriSchemeHttp && finalUri.Scheme != Uri.UriSchemeHttps))
        {
            normalized = "";
            error = "Invalid URL format.";
            return false;
        }

        normalized = finalUri.ToString().TrimEnd('/');
        error = null;
        return true;
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}