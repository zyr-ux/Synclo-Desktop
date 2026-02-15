using System;

namespace Synclo.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string? device_id { get; set; }
    public string? device_name { get; set; }
    
    // NEW - E2EE settings
    public int kdf_version { get; set; } = 1;
    public int blob_version { get; set; } = 1;
    public DateTime? last_sync { get; set; }
    public bool background_sync_enabled { get; set; } = false;
    public bool start_on_boot { get; set; } = false;
    public bool minimize_to_tray { get; set; } = false;
    
    // Hidden setting for sync/page limit
    public int sync_page_size { get; set; } = 100;
}