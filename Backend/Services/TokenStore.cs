using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace HattrickAnalizer.Services;

public class StoredToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string AccessTokenSecret { get; set; } = string.Empty;
    public int OwnTeamId { get; set; }
    public string OwnTeamName { get; set; } = string.Empty;
    public DateTime AuthorizedAt { get; set; }
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

// Tokeny żądania OAuth między /oauth/start a /oauth/complete.
public class PendingAuth
{
    public string RequestToken { get; set; } = string.Empty;
    public string RequestTokenSecret { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Cache w pamięci z write-through do SQL — tokeny przeżywają restart/skalowanie.
// Gdy baza jest niedostępna (np. brak konfiguracji), działa wyłącznie w pamięci.
public class TokenStore
{
    private static readonly TimeSpan SlidingExpiry = TimeSpan.FromDays(30);
    private static readonly TimeSpan PendingExpiry = TimeSpan.FromHours(1);
    private static readonly TimeSpan DbRetryWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, StoredToken> _tokens = new();
    private readonly ConcurrentDictionary<string, PendingAuth> _pending = new();
    private readonly string? _connectionString;
    private readonly IDataProtector _protector;
    private readonly ILogger<TokenStore> _logger;
    private DateTime _dbDisabledUntil = DateTime.MinValue;
    private DateTime _lastPurge = DateTime.MinValue;
    private bool _schemaEnsured;
    private readonly object _schemaLock = new();

    public TokenStore(IConfiguration configuration, IDataProtectionProvider dataProtection, ILogger<TokenStore> logger)
    {
        _connectionString = configuration.GetConnectionString("HattrickDb");
        _protector = dataProtection.CreateProtector("HattrickAnalizer.OAuthTokens");
        _logger = logger;
        if (string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogWarning("Brak connection stringa HattrickDb — tokeny OAuth będą przechowywane tylko w pamięci i znikną po restarcie.");
        }
    }

    public StoredToken? Get(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        if (!_tokens.TryGetValue(sessionId, out var token))
        {
            token = LoadFromDb(sessionId);
            if (token != null) _tokens[sessionId] = token;
        }

        if (token == null) return null;

        if (DateTime.UtcNow - token.LastUsedAt > SlidingExpiry)
        {
            Clear(sessionId);
            return null;
        }

        if (DateTime.UtcNow - token.LastUsedAt > TouchInterval)
        {
            token.LastUsedAt = DateTime.UtcNow;
            TryDb(conn =>
            {
                using var cmd = new SqlCommand("UPDATE dbo.OAuthSessions SET LastUsedAt = @now WHERE SessionId = @sid", conn);
                cmd.Parameters.AddWithValue("@sid", sessionId);
                cmd.Parameters.AddWithValue("@now", token.LastUsedAt);
                cmd.ExecuteNonQuery();
            });
        }

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
        token.LastUsedAt = DateTime.UtcNow;
        _tokens[sessionId] = token;

        TryDb(conn =>
        {
            const string sql = """
                MERGE dbo.OAuthSessions AS t
                USING (SELECT @sid AS SessionId) AS s ON t.SessionId = s.SessionId
                WHEN MATCHED THEN UPDATE SET
                    AccessToken = @at, AccessTokenSecret = @ats,
                    RequestToken = NULL, RequestTokenSecret = NULL,
                    OwnTeamId = @teamId, OwnTeamName = @teamName,
                    AuthorizedAt = @authAt, LastUsedAt = @lastUsed
                WHEN NOT MATCHED THEN INSERT
                    (SessionId, AccessToken, AccessTokenSecret, OwnTeamId, OwnTeamName, AuthorizedAt, LastUsedAt)
                    VALUES (@sid, @at, @ats, @teamId, @teamName, @authAt, @lastUsed);
                """;
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@at", _protector.Protect(token.AccessToken));
            cmd.Parameters.AddWithValue("@ats", _protector.Protect(token.AccessTokenSecret));
            cmd.Parameters.AddWithValue("@teamId", token.OwnTeamId);
            cmd.Parameters.AddWithValue("@teamName", token.OwnTeamName);
            cmd.Parameters.AddWithValue("@authAt", token.AuthorizedAt);
            cmd.Parameters.AddWithValue("@lastUsed", token.LastUsedAt);
            cmd.ExecuteNonQuery();
        });

        PurgeExpiredIfDue();
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _tokens.TryRemove(sessionId, out _);
        _pending.TryRemove(sessionId, out _);

        TryDb(conn =>
        {
            using var cmd = new SqlCommand("DELETE FROM dbo.OAuthSessions WHERE SessionId = @sid", conn);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.ExecuteNonQuery();
        });
    }

    public void SavePending(string sessionId, PendingAuth pending)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _pending[sessionId] = pending;

        TryDb(conn =>
        {
            const string sql = """
                MERGE dbo.OAuthSessions AS t
                USING (SELECT @sid AS SessionId) AS s ON t.SessionId = s.SessionId
                WHEN MATCHED THEN UPDATE SET
                    RequestToken = @rt, RequestTokenSecret = @rts, LastUsedAt = @now
                WHEN NOT MATCHED THEN INSERT
                    (SessionId, RequestToken, RequestTokenSecret, OwnTeamId, OwnTeamName, LastUsedAt)
                    VALUES (@sid, @rt, @rts, 0, N'', @now);
                """;
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@rt", _protector.Protect(pending.RequestToken));
            cmd.Parameters.AddWithValue("@rts", _protector.Protect(pending.RequestTokenSecret));
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        });
    }

    public PendingAuth? GetPending(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        if (!_pending.TryGetValue(sessionId, out var pending))
        {
            pending = LoadPendingFromDb(sessionId);
            if (pending != null) _pending[sessionId] = pending;
        }

        if (pending == null) return null;
        if (DateTime.UtcNow - pending.CreatedAt > PendingExpiry)
        {
            _pending.TryRemove(sessionId, out _);
            return null;
        }
        return pending;
    }

    public void ClearPending(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _pending.TryRemove(sessionId, out _);
    }

    private StoredToken? LoadFromDb(string sessionId)
    {
        StoredToken? result = null;
        TryDb(conn =>
        {
            const string sql = """
                SELECT AccessToken, AccessTokenSecret, OwnTeamId, OwnTeamName, AuthorizedAt, LastUsedAt
                FROM dbo.OAuthSessions
                WHERE SessionId = @sid AND AccessToken IS NOT NULL
                """;
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return;
            result = new StoredToken
            {
                AccessToken = Unprotect(r.GetString(0)) ?? "",
                AccessTokenSecret = Unprotect(r.GetString(1)) ?? "",
                OwnTeamId = r.GetInt32(2),
                OwnTeamName = r.GetString(3),
                AuthorizedAt = r.IsDBNull(4) ? DateTime.MinValue : r.GetDateTime(4),
                LastUsedAt = r.GetDateTime(5)
            };
            if (string.IsNullOrEmpty(result.AccessToken)) result = null;
        });
        return result;
    }

    private PendingAuth? LoadPendingFromDb(string sessionId)
    {
        PendingAuth? result = null;
        TryDb(conn =>
        {
            const string sql = """
                SELECT RequestToken, RequestTokenSecret, LastUsedAt
                FROM dbo.OAuthSessions
                WHERE SessionId = @sid AND RequestToken IS NOT NULL
                """;
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return;
            var token = Unprotect(r.GetString(0));
            var secret = Unprotect(r.GetString(1));
            if (token == null || secret == null) return;
            result = new PendingAuth
            {
                RequestToken = token,
                RequestTokenSecret = secret,
                CreatedAt = r.GetDateTime(2)
            };
        });
        return result;
    }

    private string? Unprotect(string value)
    {
        try
        {
            return _protector.Unprotect(value);
        }
        catch (Exception ex)
        {
            // Klucze Data Protection mogły się zmienić — token jest nie do odzyskania.
            _logger.LogWarning(ex, "Nie udało się odszyfrować tokenu z bazy — wymagana ponowna autoryzacja.");
            return null;
        }
    }

    private void PurgeExpiredIfDue()
    {
        if (DateTime.UtcNow - _lastPurge < TimeSpan.FromDays(1)) return;
        _lastPurge = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - SlidingExpiry;
        foreach (var kvp in _tokens)
        {
            if (kvp.Value.LastUsedAt < cutoff) _tokens.TryRemove(kvp.Key, out _);
        }
        TryDb(conn =>
        {
            using var cmd = new SqlCommand("DELETE FROM dbo.OAuthSessions WHERE LastUsedAt < @cutoff", conn);
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            cmd.ExecuteNonQuery();
        });
    }

    private void TryDb(Action<SqlConnection> action)
    {
        if (string.IsNullOrEmpty(_connectionString)) return;
        if (DateTime.UtcNow < _dbDisabledUntil) return;

        try
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            EnsureSchema(conn);
            action(conn);
        }
        catch (Exception ex)
        {
            _dbDisabledUntil = DateTime.UtcNow + DbRetryWindow;
            _logger.LogWarning(ex, "Baza tokenów niedostępna — działanie tylko w pamięci przez {Minutes} min.", DbRetryWindow.TotalMinutes);
        }
    }

    private void EnsureSchema(SqlConnection conn)
    {
        if (_schemaEnsured) return;
        lock (_schemaLock)
        {
            if (_schemaEnsured) return;
            const string sql = """
                IF OBJECT_ID(N'dbo.OAuthSessions', N'U') IS NULL
                CREATE TABLE dbo.OAuthSessions (
                    SessionId NVARCHAR(64) NOT NULL PRIMARY KEY,
                    RequestToken NVARCHAR(MAX) NULL,
                    RequestTokenSecret NVARCHAR(MAX) NULL,
                    AccessToken NVARCHAR(MAX) NULL,
                    AccessTokenSecret NVARCHAR(MAX) NULL,
                    OwnTeamId INT NOT NULL DEFAULT 0,
                    OwnTeamName NVARCHAR(256) NOT NULL DEFAULT N'',
                    AuthorizedAt DATETIME2 NULL,
                    LastUsedAt DATETIME2 NOT NULL
                );
                """;
            using var cmd = new SqlCommand(sql, conn);
            cmd.ExecuteNonQuery();
            _schemaEnsured = true;
        }
    }
}
