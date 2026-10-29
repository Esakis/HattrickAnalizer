using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace HattrickAnalizer.Services;

// One encrypted, atomically replaced record per browser session. No credentials in filenames.
public sealed class LocalOAuthStore
{
    private readonly string _directory;
    private readonly IDataProtector _protector;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    public LocalOAuthStore(string directory, IDataProtectionProvider protection, ILogger logger)
    {
        _directory = Path.GetFullPath(directory);
        _protector = protection.CreateProtector("HattrickAnalizer.LocalOAuthSessions.v1");
        _logger = logger;
    }

    public sealed class Session
    {
        public StoredToken? Token { get; set; }
        public PendingAuth? Pending { get; set; }
    }

    private string FilePath(string sessionId) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))) + ".protected");

    public Session? Read(string sessionId)
    {
        lock (_gate)
        {
            var path = FilePath(sessionId);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<Session>(_protector.Unprotect(File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                _logger.LogWarning("Cannot read a saved OAuth session ({ErrorType}). Authorization is required again.", ex.GetType().Name);
                return null;
            }
        }
    }

    public void Update(string sessionId, Action<Session> update)
    {
        lock (_gate)
        {
            var session = Read(sessionId) ?? new Session();
            update(session);
            if (session.Token == null && session.Pending == null)
            {
                Delete(sessionId);
                return;
            }
            Directory.CreateDirectory(_directory);
            var path = FilePath(sessionId);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, _protector.Protect(JsonSerializer.Serialize(session)));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    public void Delete(string sessionId)
    {
        lock (_gate)
        {
            var path = FilePath(sessionId);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
