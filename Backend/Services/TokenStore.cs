using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<string, StoredToken> _tokens = new();

    public StoredToken? Get(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        _tokens.TryGetValue(sessionId, out var token);
        return token;
    }

    public bool IsAuthorized(string sessionId)
    {
        var token = Get(sessionId);
        return token != null
            && !string.IsNullOrEmpty(token.AccessToken)
            && !string.IsNullOrEmpty(token.AccessTokenSecret);
    }

    public void Save(string sessionId, StoredToken token)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _tokens[sessionId] = token;
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _tokens.TryRemove(sessionId, out _);
    }
}
