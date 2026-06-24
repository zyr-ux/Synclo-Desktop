using System;
using System.Text.Json.Serialization;

namespace Synclo.Models;

public sealed class AppSettings
{
    public const string DefaultServerUrl = "https://synclo.zyrux.dev";

    public string Theme { get; set; } = "System";
    public string? device_id { get; set; }
    public string? device_name { get; set; }
    private string _serverUrl = DefaultServerUrl;
    [JsonIgnore]
    public string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = string.IsNullOrWhiteSpace(value) ? DefaultServerUrl : value;
    }
    public int kdf_version { get; set; } = 1;
    public int blob_version { get; set; } = 1;
    public DateTime? last_sync { get; set; }
    public bool background_sync_enabled { get; set; } = false;
    public bool start_on_boot { get; set; } = false;
    public bool minimize_to_tray { get; set; } = false;
    public bool is_mica_enabled { get; set; } = true;
    public bool is_sidebar_collapsed { get; set; } = false;
    public int sync_page_size { get; set; } = 100;
}