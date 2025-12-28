using System;
using System.IO;
using System.Text.Json;
using Synclo.Models;

namespace Synclo.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
}

public sealed class SettingsService : ISettingsService
{
    private const string FileName = "settings.json";
    private readonly string _path;

    public SettingsService()
    {
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Synclo", FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Settings = Load();
    }

    public AppSettings Settings { get; }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_path, json);
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