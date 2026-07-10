using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using HattrickAnalizer.Services;

namespace HattrickAnalizer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OAuthController : ControllerBase
{
    private readonly OAuthService _oauthService;
    private readonly TokenStore _tokenStore;
    private readonly ILogger<OAuthController> _logger;
    private readonly bool _mockMode;

    public OAuthController(OAuthService oauthService, TokenStore tokenStore, ILogger<OAuthController> logger, IConfiguration configuration)
    {
        _oauthService = oauthService;
        _tokenStore = tokenStore;
        _logger = logger;
        _mockMode = configuration.GetValue<bool>("UseMockData");
    }

    private static readonly CookieOptions SessionCookieOptions = new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.None,
        MaxAge = TimeSpan.FromDays(30)
    };

    private string GetOrCreateSessionId()
    {
        var sessionId = Request.Cookies["ht_session"];
        if (string.IsNullOrEmpty(sessionId))
        {
            sessionId = Guid.NewGuid().ToString();
        }
        // Odśwież cookie przy każdym wywołaniu (przesuwne 30 dni).
        Response.Cookies.Append("ht_session", sessionId, SessionCookieOptions);
        return sessionId;
    }

    // scope np. "set_matchorder" — wymagany do wysyłania składu; pusty = tylko odczyt.
    [HttpGet("start")]
    public async Task<IActionResult> StartAuthorization([FromQuery] string? scope = null)
    {
        try
        {
            var sessionId = GetOrCreateSessionId();
            var (token, tokenSecret, authUrl) = await _oauthService.GetRequestTokenAsync("oob", scope ?? "");

            _tokenStore.SavePending(sessionId, new PendingAuth
            {
                RequestToken = token,
                RequestTokenSecret = tokenSecret
            });

            return Ok(new
            {
                sessionId,
                authorizationUrl = authUrl,
                message = "Otwórz authorizationUrl w przeglądarce, zaloguj się i skopiuj PIN"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd przy starcie autoryzacji OAuth");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("complete")]
    public async Task<IActionResult> CompleteAuthorization([FromBody] CompleteAuthRequest request)
    {
        try
        {
            // Fallback na sessionId z body: pierwszy request mógł nie mieć jeszcze cookie.
            var sessionId = Request.Cookies["ht_session"] ?? request.SessionId;
            var pending = _tokenStore.GetPending(sessionId);
            if (pending == null)
            {
                return BadRequest(new { error = "Sesja autoryzacji wygasła lub nie istnieje — zacznij od nowa." });
            }

            var (accessToken, accessTokenSecret) = await _oauthService.ExchangeRequestTokenAsync(
                pending.RequestToken,
                pending.RequestTokenSecret,
                request.Verifier
            );

            var (ownTeamId, ownTeamName) = await FetchOwnTeamInfoAsync(accessToken, accessTokenSecret);

            _tokenStore.Save(sessionId, new StoredToken
            {
                AccessToken = accessToken,
                AccessTokenSecret = accessTokenSecret,
                OwnTeamId = ownTeamId,
                OwnTeamName = ownTeamName,
                AuthorizedAt = DateTime.UtcNow
            });
            _tokenStore.ClearPending(sessionId);

            Response.Cookies.Append("ht_session", sessionId, SessionCookieOptions);

            // Celowo bez accessToken — sekret nie opuszcza serwera.
            return Ok(new
            {
                sessionId,
                ownTeamId,
                ownTeamName,
                message = "Autoryzacja zakończona pomyślnie! Token zapisany."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Błąd przy finalizacji autoryzacji OAuth");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("current")]
    public IActionResult GetCurrent()
    {
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || !_tokenStore.IsAuthorized(sessionId))
        {
            return Ok(new { authorized = false, mockMode = _mockMode });
        }
        return Ok(new
        {
            authorized = true,
            mockMode = _mockMode,
            ownTeamId = stored.OwnTeamId,
            ownTeamName = stored.OwnTeamName,
            authorizedAt = stored.AuthorizedAt
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        var sessionId = Request.Cookies["ht_session"] ?? "";
        _tokenStore.Clear(sessionId);
        // Usunięcie cross-site cookie wymaga tych samych opcji, z którymi było ustawione.
        Response.Cookies.Delete("ht_session", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None
        });
        return Ok(new { success = true });
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
