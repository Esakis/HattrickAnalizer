using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OAuthController : ControllerBase
{
    private readonly OAuthService _oauthService;
    private readonly TokenStore _tokenStore;
    private static readonly Dictionary<string, OAuthSession> _sessions = new();

    public OAuthController(OAuthService oauthService, TokenStore tokenStore)
    {
        _oauthService = oauthService;
        _tokenStore = tokenStore;
    }

    [HttpGet("start")]
    public async Task<IActionResult> StartAuthorization()
    {
        try
        {
            var (token, tokenSecret, authUrl) = await _oauthService.GetRequestTokenAsync();

            var session = new OAuthSession
            {
                RequestToken = token,
                RequestTokenSecret = tokenSecret
            };

            _sessions[session.SessionId] = session;

            return Ok(new
            {
                sessionId = session.SessionId,
                authorizationUrl = authUrl,
                message = "Otwórz authorizationUrl w przeglądarce, zaloguj się i skopiuj PIN"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("complete")]
    public async Task<IActionResult> CompleteAuthorization([FromBody] CompleteAuthRequest request)
    {
        try
        {
            if (!_sessions.TryGetValue(request.SessionId, out var session))
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            if (string.IsNullOrEmpty(session.RequestToken) || string.IsNullOrEmpty(session.RequestTokenSecret))
            {
                return BadRequest(new { error = "Session not initialized properly" });
            }

            var (accessToken, accessTokenSecret) = await _oauthService.ExchangeRequestTokenAsync(
                session.RequestToken,
                session.RequestTokenSecret,
                request.Verifier
            );

            session.AccessToken = accessToken;
            session.AccessTokenSecret = accessTokenSecret;
            session.AuthorizedAt = DateTime.UtcNow;

            var (ownTeamId, ownTeamName) = await FetchOwnTeamInfoAsync(accessToken, accessTokenSecret);

            _tokenStore.Save(new StoredToken
            {
                AccessToken = accessToken,
                AccessTokenSecret = accessTokenSecret,
                OwnTeamId = ownTeamId,
                OwnTeamName = ownTeamName,
                AuthorizedAt = DateTime.UtcNow
            });

            return Ok(new
            {
                sessionId = session.SessionId,
                accessToken,
                ownTeamId,
                ownTeamName,
                message = "Autoryzacja zakończona pomyślnie! Token zapisany."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("status/{sessionId}")]
    public IActionResult GetStatus(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return NotFound(new { error = "Session not found" });
        }

        return Ok(new
        {
            sessionId = session.SessionId,
            hasRequestToken = !string.IsNullOrEmpty(session.RequestToken),
            hasAccessToken = !string.IsNullOrEmpty(session.AccessToken),
            isAuthorized = session.AuthorizedAt.HasValue,
            createdAt = session.CreatedAt,
            authorizedAt = session.AuthorizedAt
        });
    }

    [HttpGet("current")]
    public IActionResult GetCurrent()
    {
        var stored = _tokenStore.Get();
        if (stored == null || !_tokenStore.IsAuthorized())
        {
            return Ok(new { authorized = false });
        }
        return Ok(new
        {
            authorized = true,
            ownTeamId = stored.OwnTeamId,
            ownTeamName = stored.OwnTeamName,
            authorizedAt = stored.AuthorizedAt
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        _tokenStore.Clear();
        return Ok(new { success = true });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] TestConnectionRequest request)
    {
        try
        {
            if (!_sessions.TryGetValue(request.SessionId, out var session))
            {
                return BadRequest(new { error = "Invalid session ID" });
            }

            if (string.IsNullOrEmpty(session.AccessToken) || string.IsNullOrEmpty(session.AccessTokenSecret))
            {
                return BadRequest(new { error = "Not authorized. Complete authorization first." });
            }

            var queryParams = new Dictionary<string, string>
            {
                { "file", "teamdetails" },
                { "version", "3.9" }
            };

            if (request.TeamId.HasValue)
            {
                queryParams["teamId"] = request.TeamId.Value.ToString();
            }

            var response = await _oauthService.MakeAuthenticatedRequestAsync(
                session.AccessToken,
                session.AccessTokenSecret,
                queryParams
            );

            return Ok(new
            {
                success = true,
                data = response
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public static OAuthSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    private async Task<(int teamId, string teamName)> FetchOwnTeamInfoAsync(string accessToken, string accessTokenSecret)
    {
        var queryParams = new Dictionary<string, string>
        {
            { "file", "teamdetails" },
            { "version", "3.6" }
        };
        var xml = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
        var doc = XDocument.Parse(xml);

        var teamElement = doc.Descendants("Team").FirstOrDefault();
        var teamId = int.Parse(teamElement?.Element("TeamID")?.Value ?? "0");
        var teamName = teamElement?.Element("TeamName")?.Value ?? string.Empty;
        return (teamId, teamName);
    }
}

public class CompleteAuthRequest
{
    public string SessionId { get; set; } = string.Empty;
    public string Verifier { get; set; } = string.Empty;
}

public class TestConnectionRequest
{
    public string SessionId { get; set; } = string.Empty;
    public int? TeamId { get; set; }
}
