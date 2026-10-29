using System.Xml.Linq;
using System.Net;
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
    private readonly bool _development;

    public OAuthController(OAuthService oauthService, TokenStore tokenStore, ILogger<OAuthController> logger, IConfiguration configuration,
        IHostEnvironment environment)
    {
        _oauthService = oauthService;
        _tokenStore = tokenStore;
        _logger = logger;
        _mockMode = configuration.GetValue<bool>("UseMockData");
        _development = environment.IsDevelopment();
    }

    private bool LocalHttp => _development && !Request.IsHttps
        && (string.Equals(Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(Request.Host.Host, out var ip) && IPAddress.IsLoopback(ip)));

    private CookieOptions SessionCookieOptions => new()
    {
        HttpOnly = true,
        Secure = !LocalHttp,
        SameSite = LocalHttp ? SameSiteMode.Lax : SameSiteMode.None,
        Path = "/",
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
        if (_mockMode) return BadRequest(new { error = "Tryb demonstracyjny jest już aktywny. Nie łączy się z kontem Hattrick." });
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
        if (_mockMode) return BadRequest(new { error = "Autoryzacja Hattrick jest wyłączona w trybie demonstracyjnym." });
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
        if (_mockMode) return Ok(new { authorized = true, mockMode = true,
            ownTeamId = MockChppData.TeamId, ownTeamName = MockChppData.TeamName });
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || !_tokenStore.IsAuthorized(sessionId))
        {
            return Ok(new { authorized = false, mockMode = _mockMode });
        }
        Response.Cookies.Append("ht_session", sessionId, SessionCookieOptions);
        return Ok(new
        {
            authorized = true,
            mockMode = _mockMode,
            ownTeamId = stored.OwnTeamId,
            ownTeamName = stored.OwnTeamName,
            authorizedAt = stored.AuthorizedAt
        });
    }

    /// <summary>
    /// Wszystkie druzyny konta (Hattrick pozwala prowadzic kilka klubow, np. meski i kobiecy).
    /// teamdetails bez teamId zwraca wszystkie zespoly uzytkownika.
    /// </summary>
    [HttpGet("my-teams")]
    public async Task<IActionResult> GetMyTeams()
    {
        if (_mockMode) return Ok(new { currentTeamId = MockChppData.TeamId, teams = new[] {
            new AccountTeam { TeamId = MockChppData.TeamId, TeamName = MockChppData.TeamName,
                LeagueName = "Liga demonstracyjna", IsPrimaryClub = true } } });
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || string.IsNullOrEmpty(stored.AccessToken))
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var teams = await FetchAccountTeamsAsync(stored.AccessToken, stored.AccessTokenSecret);
        return Ok(new { currentTeamId = stored.OwnTeamId, teams });
    }

    /// <summary>
    /// Przelacza aktywna druzyne sesji (musi nalezec do konta). Wszystkie endpointy
    /// oparte o OwnTeamId (next-opponent, liga, trening) dzialaja od razu w nowym kontekscie.
    /// </summary>
    [HttpPost("select-team")]
    public async Task<IActionResult> SelectTeam([FromBody] SelectTeamRequest request)
    {
        if (_mockMode)
            return request.TeamId == MockChppData.TeamId
                ? Ok(new { ownTeamId = MockChppData.TeamId, ownTeamName = MockChppData.TeamName })
                : BadRequest(new { error = "W trybie demonstracyjnym dostępny jest jeden klub." });
        var sessionId = Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || string.IsNullOrEmpty(stored.AccessToken))
        {
            return Unauthorized(new { error = "Brak autoryzacji OAuth — zaloguj się do Hattricka." });
        }

        var teams = await FetchAccountTeamsAsync(stored.AccessToken, stored.AccessTokenSecret);
        var selected = teams.FirstOrDefault(t => t.TeamId == request.TeamId);
        if (selected == null)
        {
            return BadRequest(new { error = $"Drużyna {request.TeamId} nie należy do tego konta." });
        }

        stored.OwnTeamId = selected.TeamId;
        stored.OwnTeamName = selected.TeamName;
        _tokenStore.Save(sessionId, stored);

        return Ok(new { ownTeamId = selected.TeamId, ownTeamName = selected.TeamName });
    }

    private async Task<List<AccountTeam>> FetchAccountTeamsAsync(string accessToken, string accessTokenSecret)
    {
        var queryParams = new Dictionary<string, string>
        {
            { "file", "teamdetails" },
            { "version", "3.6" }
        };
        var xml = await _oauthService.MakeAuthenticatedRequestAsync(accessToken, accessTokenSecret, queryParams);
        var doc = XDocument.Parse(xml);

        var teams = new List<AccountTeam>();
        foreach (var teamEl in doc.Descendants("Team"))
        {
            var id = int.Parse(teamEl.Element("TeamID")?.Value ?? "0");
            if (id == 0) continue;
            teams.Add(new AccountTeam
            {
                TeamId = id,
                TeamName = teamEl.Element("TeamName")?.Value ?? "",
                LeagueName = teamEl.Element("LeagueLevelUnit")?.Element("LeagueLevelUnitName")?.Value ?? "",
                IsPrimaryClub = string.Equals(teamEl.Element("IsPrimaryClub")?.Value, "True", StringComparison.OrdinalIgnoreCase)
                                || teamEl.Element("IsPrimaryClub")?.Value == "1"
            });
        }
        return teams;
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        if (_mockMode) return Ok(new { success = true, mockMode = true });
        var sessionId = Request.Cookies["ht_session"] ?? "";
        _tokenStore.Clear(sessionId);
        // Usunięcie cross-site cookie wymaga tych samych opcji, z którymi było ustawione.
        Response.Cookies.Delete("ht_session", SessionCookieOptions);
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

public class SelectTeamRequest
{
    public int TeamId { get; set; }
}

public class AccountTeam
{
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string LeagueName { get; set; } = string.Empty;
    public bool IsPrimaryClub { get; set; }
}
