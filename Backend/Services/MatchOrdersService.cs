using System.Text.Json;
using System.Xml.Linq;
using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Wysylanie ustawienia meczowego do Hattricka (file=matchorders, actionType=setmatchorder).
/// Wymaga tokenu OAuth autoryzowanego ze scope "set_matchorder" — bez niego CHPP
/// odrzuca zadanie (wtedy trzeba ponowic autoryzacje przez /api/oauth/start?scope=set_matchorder).
/// </summary>
public class MatchOrdersService
{
    private readonly OAuthService _oauthService;
    private readonly TokenStore _tokenStore;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MatchOrdersService> _logger;

    public MatchOrdersService(
        OAuthService oauthService,
        TokenStore tokenStore,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MatchOrdersService> logger)
    {
        _oauthService = oauthService;
        _tokenStore = tokenStore;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<MatchOrdersResult> SendLineupAsync(MatchOrdersRequest request)
    {
        var sessionId = _httpContextAccessor.HttpContext?.Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || string.IsNullOrEmpty(stored.AccessToken))
        {
            throw new UnauthorizedAccessException("Brak autoryzacji OAuth — zaloguj się do Hattricka.");
        }

        var lineupJson = BuildLineupJson(request);
        var queryParams = new Dictionary<string, string>
        {
            { "file", "matchorders" },
            { "version", "3.0" },
            { "actionType", "setmatchorder" },
            { "matchID", request.MatchId.ToString() },
            { "sourceSystem", "hattrick" },
            { "lineup", lineupJson }
        };

        // CHPP nie wspiera POST ("POST isn't supported. Use GET instead") — zapis idzie
        // GET-em z lineup JSON w query stringu, obowiazkowo z pominieciem cache.
        string xml;
        try
        {
            xml = await _oauthService.MakeAuthenticatedRequestAsync(stored.AccessToken, stored.AccessTokenSecret, queryParams, useCache: false);
        }
        catch (Exception ex)
        {
            // CHPP odpowiada golym HTTP 401 (HTML IIS), gdy token nie ma scope set_matchorder —
            // identycznie podpisane zapytania read-only przechodza.
            if (ex.Message.Contains("401", StringComparison.Ordinal))
            {
                throw new ChppApiException(
                    "CHPP odmówiło dostępu (401): token OAuth nie ma uprawnienia set_matchorder. " +
                    "Wyloguj się i autoryzuj ponownie z opcją wysyłania składu " +
                    "(aplikacja CHPP musi mieć zatwierdzone to uprawnienie przez Hattrick).");
            }
            throw new ChppApiException($"CHPP odrzuciło wysłanie składu: {ex.Message}", ex);
        }

        var doc = XDocument.Parse(xml);
        var error = doc.Descendants("Error").FirstOrDefault();
        if (error != null)
        {
            var message = error.Value;
            // Najczestszy przypadek: token bez scope set_matchorder.
            if (message.Contains("authoriz", StringComparison.OrdinalIgnoreCase)
                || message.Contains("scope", StringComparison.OrdinalIgnoreCase)
                || message.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChppApiException(
                    "CHPP odmówiło: token nie ma uprawnienia set_matchorder. " +
                    "Wyloguj się i autoryzuj ponownie przez /api/oauth/start?scope=set_matchorder. " +
                    $"Oryginalny błąd: {message}");
            }
            throw new ChppApiException($"CHPP zwróciło błąd przy wysyłaniu składu: {message}");
        }

        var success = doc.Descendants("MatchOrdersSet").FirstOrDefault()?.Value;
        return new MatchOrdersResult
        {
            Success = string.Equals(success, "True", StringComparison.OrdinalIgnoreCase) || success == "1",
            RawResponse = doc.Root?.Name.LocalName ?? ""
        };
    }

    /// <summary>
    /// JSON lineup w formacie matchorders 3.0. Zachowania i kody pozycji CHPP:
    /// pozycje 100-113 (odwrotnosc MapRoleIdToSlot), zachowania 0-4.
    /// </summary>
    internal static string BuildLineupJson(MatchOrdersRequest request)
    {
        var positions = new List<object>();
        foreach (var (slot, order) in request.Positions)
        {
            var roleId = SlotToRoleId(slot);
            if (roleId == 0 || order.PlayerId == 0) continue;
            positions.Add(new
            {
                id = order.PlayerId,
                behaviour = BehaviourCode(slot, order.Behaviour),
                positionCode = roleId
            });
        }

        var lineup = new
        {
            positions,
            bench = Array.Empty<object>(),
            kickers = Array.Empty<object>(),
            captain = request.CaptainId,
            setPieces = request.SetPiecesTakerId,
            settings = new
            {
                tactic = TacticCode(request.Tactic),
                speechLevel = AttitudeCode(request.Attitude)
            },
            substitutions = Array.Empty<object>()
        };

        // Bez spacji — tresc wchodzi do podpisu OAuth (spojnosc percent-encodingu).
        return JsonSerializer.Serialize(lineup);
    }

    internal static int SlotToRoleId(string slot) => slot switch
    {
        "GK" => 100,
        "RWB" => 101, "RCD" => 102, "CD" => 103, "LCD" => 104, "LWB" => 105,
        "RW" => 106, "RIM" => 107, "IM" => 108, "LIM" => 109, "LW" => 110,
        "RFW" => 111, "FW" => 112, "LFW" => 113,
        _ => 0
    };

    // Odwrotnosc CalibrationService.MapSlotBehaviour: klucz zachowania -> kod CHPP.
    internal static int BehaviourCode(string slot, string behaviour) => behaviour switch
    {
        "WBO" or "CDO" or "WO" or "IMO" => 1,
        "WBD" or "WD" or "IMD" or "DF" => 2,
        "WBTM" or "WTM" or "IMTW" => 3,
        "CDTW" or "FTW" => 4,
        _ => 0 // normalne (klucz == slot)
    };

    internal static int TacticCode(string tactic) => tactic switch
    {
        "Pressing" => 1,
        "Counter" => 2,
        "AttackInMiddle" => 3,
        "AttackOnWings" => 4,
        "PlayCreatively" => 7,
        "LongShots" => 8,
        _ => 0
    };

    internal static int AttitudeCode(string attitude) => attitude switch
    {
        "PIC" => -1,
        "MOTS" => 1,
        _ => 0
    };
}

public class MatchOrdersRequest
{
    public long MatchId { get; set; }
    // slot (GK, RWB, ...) -> zawodnik + zachowanie (klucze z optymalizatora, np. "WBO").
    public Dictionary<string, MatchOrderSlot> Positions { get; set; } = new();
    public string Tactic { get; set; } = "Normal";
    public string Attitude { get; set; } = "Normal";
    public int CaptainId { get; set; }
    public int SetPiecesTakerId { get; set; }
}

public class MatchOrderSlot
{
    public int PlayerId { get; set; }
    public string Behaviour { get; set; } = "";
}

public class MatchOrdersResult
{
    public bool Success { get; set; }
    public string RawResponse { get; set; } = "";
}
