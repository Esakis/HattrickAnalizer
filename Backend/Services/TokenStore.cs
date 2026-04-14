using System.Text.Json;

namespace HattrickAnalizer.Services;

public class StoredToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string AccessTokenSecret { get; set; } = string.Empty;
    public int OwnTeamId { get; set; }
    public string OwnTeamName { get; set; } = string.Empty;
    public DateTime AuthorizedAt { get; set; }
}

public class TokenStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private StoredToken? _cached;

    public TokenStore(IConfiguration configuration, IWebHostEnvironment env)
    {
        var dir = env.ContentRootPath;
        _filePath = Path.Combine(dir, "oauth_tokens.json");
        Load();
    }

    public StoredToken? Get()
    {
        lock (_lock) return _cached;
    }

    public bool IsAuthorized()
    {
        lock (_lock)
        {
            return _cached != null
                && !string.IsNullOrEmpty(_cached.AccessToken)
                && !string.IsNullOrEmpty(_cached.AccessTokenSecret);
        }
    }

    public void Save(StoredToken token)
    {
        lock (_lock)
        {
            _cached = token;
            var json = JsonSerializer.Serialize(token, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cached = null;
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _cached = JsonSerializer.Deserialize<StoredToken>(json);
        }
        catch
        {
            _cached = null;
        }
    }
}
