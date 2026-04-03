namespace HattrickAnalizer.Models;

public class OAuthToken
{
    public string Token { get; set; } = string.Empty;
    public string TokenSecret { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsAccessToken { get; set; } = false;
}

public class OAuthSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public string? RequestToken { get; set; }
    public string? RequestTokenSecret { get; set; }
    public string? AccessToken { get; set; }
    public string? AccessTokenSecret { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AuthorizedAt { get; set; }
}
