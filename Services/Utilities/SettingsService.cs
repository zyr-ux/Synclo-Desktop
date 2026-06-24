using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Synclo.Models;

namespace Synclo.Services.Utilities;

public interface ISettingsService
{
    AppSettings Settings { get; }
    void Save();
    event Action<AppSettings>? SettingsChanged;
}

public sealed class SettingsService : ISettingsService
{
    private const string FileName = "settings.json";
    private readonly string _path;
    public AppSettings Settings { get; }
    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
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