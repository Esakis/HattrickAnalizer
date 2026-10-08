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
        var validationError = ValidateRequest(request);
        if (validationError != null)
            return new MatchOrdersResult { Success = false, Error = validationError, RawResponse = "validation" };

        var sessionId = _httpContextAccessor.HttpContext?.Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || string.IsNullOrEmpty(stored.AccessToken))
        {
            return new MatchOrdersResult { Success = false, Error = "OAuth authorization is unavailable." };
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
            _logger.LogWarning(ex, "CHPP match order request failed for {MatchId}", request.MatchId);
            return new MatchOrdersResult { Success = false, Error = ex.Message, RawResponse = "request-error" };
        }
        try
        {
            var doc = XDocument.Parse(xml);
            var error = doc.Descendants("Error").FirstOrDefault();
            if (error != null)
                return new MatchOrdersResult { Success = false, Error = error.Value, RawResponse = "chpp-error" };
            var matchData = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "MatchData");
            var ordersSet = matchData?.Attributes().FirstOrDefault(a => a.Name.LocalName == "OrdersSet")?.Value;
            var accepted = bool.TryParse(ordersSet, out var parsedAccepted) && parsedAccepted;
            var reason = matchData?.Elements().FirstOrDefault(e => e.Name.LocalName == "Reason")?.Value;
            return new MatchOrdersResult
            {
                Success = accepted,
                Error = accepted ? null : !string.IsNullOrWhiteSpace(reason)
                    ? reason : ordersSet == null
                        ? "CHPP response did not include MatchData OrdersSet."
                        : "CHPP did not confirm the match order.",
                RawResponse = doc.Root?.Name.LocalName ?? ""
            };
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "CHPP returned an invalid match order response for {MatchId}", request.MatchId);
            return new MatchOrdersResult { Success = false, Error = "CHPP returned an invalid response.", RawResponse = "invalid-response" };
        }
    }

    public static string? ValidateRequest(MatchOrdersRequest? request)
    {
        if (request == null) return "Request body is required.";
        if (request.MatchId <= 0) return "A positive matchId is required.";
        if (request.Positions == null || request.Positions.Count != 11) return "Exactly 11 starting positions are required.";
        if (request.Positions.Any(p => SlotToRoleId(p.Key) == 0 || p.Value == null || p.Value.PlayerId <= 0))
            return "Every position must be legal and contain a positive playerId.";
        var ids = request.Positions.Values.Select(p => p.PlayerId).ToList();
        if (ids.Distinct().Count() != 11) return "Starting players must be unique.";
        if (!request.Positions.ContainsKey("GK")) return "A goalkeeper is required.";
        if (!FormationData.Formations.Values.Any(f => f.Positions.ToHashSet(StringComparer.Ordinal).SetEquals(request.Positions.Keys)))
            return "Position slots do not match a legal formation.";
        if (!ids.Contains(request.CaptainId) || !ids.Contains(request.SetPiecesTakerId))
            return "Captain and set-piece taker must be in the starting eleven.";
        if (request.Positions.Any(p => !IsLegalBehaviour(p.Key, p.Value.Behaviour)))
            return "A position has an unknown or illegal player behaviour.";
        if (request.Tactic is not ("Normal" or "Pressing" or "Counter" or "AttackInMiddle" or "AttackOnWings" or "PlayCreatively" or "LongShots"))
            return "Tactic is unknown or unsupported.";
        if (request.Attitude is not ("Normal" or "PIC" or "MOTS"))
            return "Attitude is unknown or unsupported.";
        if (request.AssistantManagerLevel is < 0 or > 5) return "AssistantManagerLevel must be 0..5.";
        var customBehaviours = request.Positions.Count(p => p.Key != p.Value.Behaviour);
        if (customBehaviours > Math.Min(10, 5 + request.AssistantManagerLevel))
            return $"This lineup uses {customBehaviours} custom behaviours; the allowed maximum is {Math.Min(10, 5 + request.AssistantManagerLevel)}.";
        return null;
    }

    private static bool IsLegalBehaviour(string slot, string? behaviour) => slot switch
    {
        "GK" => behaviour == "GK",
        "RWB" or "LWB" => behaviour == slot || behaviour is "WBO" or "WBD" or "WBTM",
        "RCD" or "CD" or "LCD" => behaviour == slot || behaviour == "CDO" || (slot is "RCD" or "LCD" && behaviour == "CDTW"),
        "RW" or "LW" => behaviour == slot || behaviour is "WO" or "WD" or "WTM",
        "RIM" or "IM" or "LIM" => behaviour == slot || behaviour is "IMO" or "IMD"
            || (slot is "RIM" or "LIM" && behaviour == "IMTW"),
        "RFW" or "FW" or "LFW" => behaviour == slot || behaviour is "DF" or "FTW",
        _ => false
    };

    /// <summary>
    /// JSON lineup w formacie matchorders 3.0. Zachowania i kody pozycji CHPP:
    /// pozycje 100-113 (odwrotnosc MapRoleIdToSlot), zachowania 0-4.
    /// </summary>
    public static string BuildLineupJson(MatchOrdersRequest request)
    {
        var error = ValidateRequest(request);
        if (error != null) throw new ArgumentException(error, nameof(request));
        var positions = new List<object>();
        foreach (var (slot, order) in request.Positions)
        {
            var roleId = SlotToRoleId(slot);
            if (roleId == 0 || order.PlayerId == 0) throw new ArgumentException("Invalid position in lineup.", nameof(request));
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

    public static int SlotToRoleId(string slot) => slot switch
    {
        "GK" => 100,
        "RWB" => 101, "RCD" => 102, "CD" => 103, "LCD" => 104, "LWB" => 105,
        "RW" => 106, "RIM" => 107, "IM" => 108, "LIM" => 109, "LW" => 110,
        "RFW" => 111, "FW" => 112, "LFW" => 113,
        _ => 0
    };

    // Odwrotnosc CalibrationService.MapSlotBehaviour: klucz zachowania -> kod CHPP.
    public static int BehaviourCode(string slot, string behaviour) => behaviour switch
    {
        var normal when normal == slot => 0,
        "WBO" or "CDO" or "WO" or "IMO" => 1,
        "WBD" or "WD" or "IMD" or "DF" => 2,
        "WBTM" or "WTM" => 3,
        "CDTW" or "FTW" or "IMTW" => 4,
        _ => throw new ArgumentException($"Unknown behaviour '{behaviour}' for {slot}.", nameof(behaviour))
    };

    internal static int TacticCode(string tactic) => tactic switch
    {
        "Pressing" => 1,
        "Counter" => 2,
        "AttackInMiddle" => 3,
        "AttackOnWings" => 4,
        "PlayCreatively" => 7,
        "LongShots" => 8,
        "Normal" => 0,
        _ => throw new ArgumentException($"Unknown tactic '{tactic}'.", nameof(tactic))
    };

    internal static int AttitudeCode(string attitude) => attitude switch
    {
        "PIC" => -1,
        "MOTS" => 1,
        "Normal" => 0,
        _ => throw new ArgumentException($"Unknown attitude '{attitude}'.", nameof(attitude))
    };
}

public class MatchOrdersRequest
{
    public long MatchId { get; set; }
    // slot (GK, RWB, ...) -> zawodnik + zachowanie (klucze z optymalizatora, np. "WBO").
    public Dictionary<string, MatchOrderSlot> Positions { get; set; } = new();
    public string Tactic { get; set; } = "Normal";
    public string Attitude { get; set; } = "Normal";
    public int AssistantManagerLevel { get; set; }
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
    public string? Error { get; set; }
    public string RawResponse { get; set; } = "";
}
