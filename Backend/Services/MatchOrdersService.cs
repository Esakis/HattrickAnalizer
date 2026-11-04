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
    private readonly HattrickApiService _api;
    public bool MockMode => _api.MockMode;

    public MatchOrdersService(
        OAuthService oauthService,
        TokenStore tokenStore,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MatchOrdersService> logger, HattrickApiService api)
    {
        _oauthService = oauthService;
        _tokenStore = tokenStore;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _api = api;
    }

    public async Task<MatchOrdersResult> SendLineupAsync(MatchOrdersRequest request)
    {
        if (_api.MockMode)
            return new MatchOrdersResult { Success = false, Error = "Sending match orders is disabled in demo mode. No CHPP request was made.", RawResponse = "mock-write-blocked" };
        var validationError = ValidateRequest(request);
        if (validationError != null)
            return new MatchOrdersResult { Success = false, Error = validationError, RawResponse = "validation" };

        var sessionId = _httpContextAccessor.HttpContext?.Request.Cookies["ht_session"] ?? "";
        var stored = _tokenStore.Get(sessionId);
        if (stored == null || string.IsNullOrEmpty(stored.AccessToken) || stored.OwnTeamId <= 0)
        {
            return new MatchOrdersResult { Success = false, Error = "OAuth authorization is unavailable." };
        }

        string? preflightError;
        try { preflightError = await ValidateLiveContextAsync(request, stored); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify match order context for {MatchId}", request.MatchId);
            return new MatchOrdersResult { Success = false, Error = "Current match and player availability could not be verified. Nothing was sent.", RawResponse = "preflight-unavailable" };
        }
        if (preflightError != null)
            return new MatchOrdersResult { Success = false, Error = preflightError, RawResponse = "preflight" };

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

    private async Task<string?> ValidateLiveContextAsync(MatchOrdersRequest request, StoredToken stored)
    {
        // These reads bypass the OAuth cache so transfers/injuries and a closed order window are checked again.
        var matchXml = await _oauthService.MakeAuthenticatedRequestAsync(stored.AccessToken, stored.AccessTokenSecret,
            new Dictionary<string, string> { ["file"] = "matchorders", ["version"] = "3.0",
                ["actionType"] = "view", ["matchID"] = request.MatchId.ToString(), ["sourceSystem"] = "hattrick",
                ["teamID"] = stored.OwnTeamId.ToString() }, useCache: false);
        var matchDoc = XDocument.Parse(matchXml);
        var match = matchDoc.Descendants("MatchData").FirstOrDefault();
        if (matchDoc.Descendants("Error").Any() || match == null
            || !bool.TryParse(match.Attribute("Available")?.Value, out var available) || !available)
            return "CHPP did not confirm that this match is available for orders.";
        var teamIds = match.Descendants().Where(e => e.Name.LocalName is "HomeTeamID" or "AwayTeamID")
            .Select(e => int.TryParse(e.Value, out var id) ? id : 0);
        if (!teamIds.Contains(stored.OwnTeamId)) return "The match does not belong to your authenticated team.";
        var rosterXml = await _oauthService.MakeAuthenticatedRequestAsync(stored.AccessToken, stored.AccessTokenSecret,
            new Dictionary<string, string> { ["file"] = "players", ["version"] = "2.8",
                ["teamID"] = stored.OwnTeamId.ToString() }, useCache: false);
        var rosterDoc = XDocument.Parse(rosterXml);
        if (rosterDoc.Descendants("Error").Any()) return "CHPP could not verify the current roster.";
        var roster = rosterDoc.Descendants("Player").Select(_api.ParsePlayer).ToDictionary(p => p.PlayerId);
        var selected = request.Positions.Values.Select(p => p.PlayerId).Concat(request.Bench.Select(p => p.PlayerId))
            .Concat(request.Kickers).Distinct();
        foreach (var id in selected)
        {
            if (!roster.TryGetValue(id, out var player)) return $"Player {id} is no longer in your team's roster.";
            if (!player.InjuryStatusKnown || player.InjuryLevel > 0 || !player.SuspensionStatusKnown || player.IsSuspended)
                return $"Player {id} is unavailable, or current injury/suspension status could not be verified.";
        }
        return null;
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
        if (request.Bench == null || request.Kickers == null || request.Substitutions == null)
            return "Bench, kickers and substitutions must not be null.";
        if (request.Bench.Any(p => p == null || p.PlayerId <= 0 || p.PositionCode is < 0 or > 13)
            || request.Bench.Select(p => p.PositionCode).Distinct().Count() != request.Bench.Count)
            return "Bench positions must be unique indices 0..13 with positive player IDs.";
        var primary = request.Bench.Where(p => p.PositionCode < 7).Select(p => p.PlayerId).ToHashSet();
        if (primary.Count != request.Bench.Count(p => p.PositionCode < 7) || primary.Overlaps(ids))
            return "Primary substitutes must be distinct and cannot be in the starting XI.";
        if (request.Bench.Any(p => p.PositionCode >= 7 && !primary.Contains(p.PlayerId)))
            return "Backup bench slots must refer to a primary substitute.";
        var squad = ids.Concat(primary).ToHashSet();
        if (request.Kickers.Count > 11 || request.Kickers.Distinct().Count() != request.Kickers.Count
            || request.Kickers.Any(id => !squad.Contains(id)))
            return "Select up to eleven distinct penalty takers from the starting XI and bench.";
        if (request.Substitutions.Count > 5) return "At most five player orders are supported.";
        foreach (var order in request.Substitutions)
        {
            if (order == null || !squad.Contains(order.PlayerOutId) || !squad.Contains(order.PlayerInId))
                return "Every substitution player must be in the selected match squad.";
            if (order.PlayerInId != order.PlayerOutId && !primary.Contains(order.PlayerInId))
                return "A substitute entering the pitch must be on the bench.";
            if (order.Minute != -1 && order.Minute != 46 && order.Minute != 91
                && (order.Minute is < 5 or > 115 || order.Minute % 5 != 0))
                return "Choose any time (-1), half time (46), before extra time (91), or a five-minute mark from 5 to 115.";
            if (order.Position is < -1 or > 13 || order.Behaviour is < -1 or > 4 || order.Standing is < -1 or > 9)
                return "A substitution position, behaviour or score condition is invalid.";
            if (order.Position >= 0 && order.Behaviour >= 0)
            {
                var slot = RoleIdToSlot(100 + order.Position);
                if (!FormationData.SlotBehaviourOptions[slot].Any(b => BehaviourCode(slot, b) == order.Behaviour))
                    return "The substitution behaviour is not legal for its target position.";
            }
            if (order.PlayerInId == order.PlayerOutId && order.Position == -1 && order.Behaviour == -1)
                return "A behaviour-change order must change position or behaviour.";
        }
        if (request.Substitutions.All(o => o.Minute >= 0 && o.Standing == -1))
        {
            var pitch = request.Positions.ToDictionary(p => p.Key, p => (Id: p.Value.PlayerId, Behaviour: BehaviourCode(p.Key, p.Value.Behaviour)));
            var used = ids.ToHashSet();
            foreach (var order in request.Substitutions.OrderBy(o => o.Minute == 46 ? 45 : o.Minute == 91 ? 90 : o.Minute))
            {
                var from = pitch.FirstOrDefault(p => p.Value.Id == order.PlayerOutId);
                if (from.Key == null) return "An unconditional order removes a player who is not on the pitch at that time.";
                if (order.PlayerInId != order.PlayerOutId && !used.Add(order.PlayerInId))
                    return "A player cannot enter the pitch twice.";
                var target = order.Position < 0 ? from.Key : RoleIdToSlot(100 + order.Position);
                if (target != from.Key && pitch.ContainsKey(target)) return "An unconditional order moves a player into an occupied position.";
                var behaviour = order.Behaviour < 0 ? from.Value.Behaviour : order.Behaviour;
                if (!FormationData.SlotBehaviourOptions[target].Any(b => BehaviourCode(target, b) == behaviour))
                    return "The order behaviour is not legal for the player's position at that time.";
                pitch.Remove(from.Key);
                pitch[target] = (order.PlayerInId, behaviour);
                if (!pitch.ContainsKey("GK") || !FormationData.Formations.Values.Any(f => f.Positions.ToHashSet(StringComparer.Ordinal).SetEquals(pitch.Keys)))
                    return "An unconditional order creates an unsupported formation.";
            }
        }
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
        // CHPP 3.0 uses fixed-length positional arrays, not sparse role-code objects.
        // Reference implementation: github.com/lucianoq/hattrick/chpp/type_lineup.go
        var positions = Enumerable.Range(0, 14).Select(_ => new { id = 0, behaviour = 0 }).ToArray();
        foreach (var (slot, order) in request.Positions)
            positions[SlotToRoleId(slot) - 100] = new { id = order.PlayerId, behaviour = BehaviourCode(slot, order.Behaviour) };
        var bench = Enumerable.Range(0, 14).Select(_ => new { id = 0, behaviour = 0 }).ToArray();
        foreach (var entry in request.Bench) bench[entry.PositionCode] = new { id = entry.PlayerId, behaviour = 0 };
        var kickers = Enumerable.Range(0, 11).Select(i => new { id = i < request.Kickers.Count ? request.Kickers[i] : 0, behaviour = 0 }).ToArray();
        var lineup = new
        {
            positions, bench, kickers,
            captain = request.CaptainId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            setPieces = request.SetPiecesTakerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings = new
            {
                tactic = TacticCode(request.Tactic).ToString(System.Globalization.CultureInfo.InvariantCulture),
                speechLevel = AttitudeCode(request.Attitude).ToString(System.Globalization.CultureInfo.InvariantCulture),
                newLineup = "", coachModifier = "0", manMarkerPlayerId = "0", manMarkingPlayerId = "0"
            },
            substitutions = request.Substitutions.Select(s => new
            {
                playerin = s.PlayerInId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                playerout = s.PlayerOutId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                orderType = "1", min = s.Minute.ToString(System.Globalization.CultureInfo.InvariantCulture),
                pos = s.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
                beh = s.Behaviour.ToString(System.Globalization.CultureInfo.InvariantCulture),
                card = "-1", standing = s.Standing.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }).ToArray()
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

    private static string RoleIdToSlot(int roleId) => new[]
    { "GK", "RWB", "RCD", "CD", "LCD", "LWB", "RW", "RIM", "IM", "LIM", "LW", "RFW", "FW", "LFW" }[roleId - 100];

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
    public List<MatchBenchSlot> Bench { get; set; } = new();
    public List<int> Kickers { get; set; } = new();
    public List<MatchSubstitution> Substitutions { get; set; } = new();
    public long MatchId { get; set; }
    // slot (GK, RWB, ...) -> zawodnik + zachowanie (klucze z optymalizatora, np. "WBO").
    public Dictionary<string, MatchOrderSlot> Positions { get; set; } = new();
    public string Tactic { get; set; } = "Normal";
    public string Attitude { get; set; } = "Normal";
    public int AssistantManagerLevel { get; set; }
    public int CaptainId { get; set; }
    public int SetPiecesTakerId { get; set; }
}

public class MatchBenchSlot
{
    public int PlayerId { get; set; }
    // Primary GK, CD, WB, IM, FW, W, extra: 0..6. Backup slots: 7..13.
    public int PositionCode { get; set; }
}

public class MatchSubstitution
{
    public int PlayerOutId { get; set; }
    public int PlayerInId { get; set; }
    public int Minute { get; set; } = -1;
    public int Position { get; set; } = -1;
    public int Behaviour { get; set; } = -1;
    public int Standing { get; set; } = -1;
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
