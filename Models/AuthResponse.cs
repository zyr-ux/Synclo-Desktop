namespace Synclo.Models;

public class AuthResponse
{
    public string access_token { get; set; }
    public string refresh_token { get; set; }
    public string token_type { get; set; }
}