namespace Synclo.Models;

public class RegisterRequest
{
    public string email { get; set; }
    public string password { get; set; }
    public string device_id { get; set; }
    public string device_name { get; set; }
}