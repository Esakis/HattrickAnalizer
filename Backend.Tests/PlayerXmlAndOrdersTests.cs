using System.Text.Json;
using System.Xml.Linq;
using HattrickAnalizer.Models;
using HattrickAnalizer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class PlayerXmlAndOrdersTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;

    public PlayerXmlAndOrdersTests(ApiTestFactory factory) => _factory = factory;

    [Fact]
    public void Player_parser_requires_complete_private_skills_and_preserves_injury_suspension_and_mother_club_flags()
    {
        using var scope = _factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<HattrickApiService>();
        var complete = api.ParsePlayer(PlayerXml(includePassing: true));
        var suspended = api.ParsePlayer(PlayerXml(includePassing: true, suspended: true));
        var incomplete = api.ParsePlayer(PlayerXml(includePassing: false));
        var malformed = api.ParsePlayer(PlayerXml(includePassing: true, passing: "not-a-skill"));

        Assert.True(complete.SkillsAvailable);
        Assert.True(complete.InjuryStatusKnown);
        Assert.True(complete.SuspensionStatusKnown);
        Assert.True(complete.MotherClubBonus);
        Assert.True(suspended.SuspensionStatusKnown);
        Assert.True(suspended.IsSuspended);
        Assert.False(incomplete.SkillsAvailable);
        Assert.False(malformed.SkillsAvailable);
        var unknownStatuses = api.ParsePlayer(new XElement("Player", new XElement("PlayerID", "99")));
        Assert.False(unknownStatuses.InjuryStatusKnown);
        Assert.False(unknownStatuses.SuspensionStatusKnown);
    }

    [Fact]
    public void Current_identity_and_mother_club_bonus_survive_historical_stats_merge()
    {
        var current = new Player
        {
            PlayerId = 7, FirstName = "Current", MotherClubBonus = true, Loyalty = 20, Form = 7,
            Skills = new PlayerSkills { Keeper = 5, KeeperAvailable = true }
        };
        var historical = new Player
        {
            PlayerId = 7, FirstName = "Old", MotherClubBonus = false,
            MatchStats = new PlayerMatchStats { TotalMatches = 12, Goals = 8, AverageForm = 1 }
        };

        var merged = HattrickApiService.MergeCurrentPlayersWithMatchStats(new[] { current }, new[] { historical }).Single();

        Assert.Equal("Current", merged.FirstName);
        Assert.True(merged.MotherClubBonus);
        Assert.Equal(20, merged.Loyalty);
        Assert.Equal(12, merged.MatchStats!.TotalMatches);
        Assert.Equal(8, merged.MatchStats.Goals);
        Assert.Equal(7, merged.MatchStats.AverageForm);
    }

    [Fact]
    public void Every_supported_behavior_round_trips_through_matchorders_codes()
    {
        var behaviorBySlot = new (string Slot, string Behavior, int Code)[]
        {
            ("GK", "GK", 0), ("RWB", "RWB", 0), ("RWB", "WBO", 1), ("RWB", "WBD", 2), ("RWB", "WBTM", 3),
            ("LWB", "LWB", 0), ("LWB", "WBO", 1), ("LWB", "WBD", 2), ("LWB", "WBTM", 3),
            ("RCD", "RCD", 0), ("RCD", "CDO", 1), ("RCD", "CDTW", 4),
            ("CD", "CD", 0), ("CD", "CDO", 1),
            ("LCD", "LCD", 0), ("LCD", "CDO", 1), ("LCD", "CDTW", 4),
            ("RW", "RW", 0), ("RW", "WO", 1), ("RW", "WD", 2), ("RW", "WTM", 3),
            ("LW", "LW", 0), ("LW", "WO", 1), ("LW", "WD", 2), ("LW", "WTM", 3),
            ("RIM", "RIM", 0), ("RIM", "IMO", 1), ("RIM", "IMD", 2), ("RIM", "IMTW", 4),
            ("IM", "IM", 0), ("IM", "IMO", 1), ("IM", "IMD", 2),
            ("LIM", "LIM", 0), ("LIM", "IMO", 1), ("LIM", "IMD", 2), ("LIM", "IMTW", 4),
            ("RFW", "RFW", 0), ("RFW", "DF", 2), ("RFW", "FTW", 4),
            ("FW", "FW", 0), ("FW", "DF", 2), ("FW", "FTW", 4),
            ("LFW", "LFW", 0), ("LFW", "DF", 2), ("LFW", "FTW", 4)
        };

        foreach (var (slot, behavior, expectedCode) in behaviorBySlot)
        {
            var formation = Assert.Single(FormationData.Formations.Values.Where(f => f.Positions.Contains(slot))
                .OrderBy(f => f.Positions.Length).Take(1));
            var oneCustom = formation.Positions.ToDictionary(
                p => p,
                p => new MatchOrderSlot { PlayerId = Array.IndexOf(formation.Positions, p) + 1, Behaviour = p },
                StringComparer.Ordinal);
            oneCustom[slot].Behaviour = behavior;
            var request = ValidOrderRequest(oneCustom, assistantLevel: 5);
            var json = JsonDocument.Parse(MatchOrdersService.BuildLineupJson(request));
            var order = json.RootElement.GetProperty("positions").EnumerateArray()
                .Single(p => p.GetProperty("positionCode").GetInt32() == MatchOrdersService.SlotToRoleId(slot));

            Assert.Equal(expectedCode, order.GetProperty("behaviour").GetInt32());
            Assert.Equal(behavior, CalibrationService.MapSlotBehaviour(slot, expectedCode));
            Assert.Equal(expectedCode, MatchOrdersService.BehaviourCode(slot, behavior));
            Assert.True(CalibrationService.IsValidSlotBehaviour(slot, behavior));
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing-gk")]
    [InlineData("unsupported-tactic")]
    [InlineData("unsupported-attitude")]
    [InlineData("custom-order-cap")]
    public void Invalid_matchorders_payloads_are_rejected_before_any_CHPP_call(string invalidCase)
    {
        var request = ValidOrderRequest(ValidOrderPositions(), assistantLevel: 0);
        switch (invalidCase)
        {
            case "duplicate": request.Positions["RWB"].PlayerId = request.Positions["GK"].PlayerId; break;
            case "missing-gk": request.Positions.Remove("GK"); break;
            case "unsupported-tactic": request.Tactic = "TikiTaka"; break;
            case "unsupported-attitude": request.Attitude = "Calm"; break;
            case "custom-order-cap":
                request.Positions["RWB"].Behaviour = "WBO";
                request.Positions["RCD"].Behaviour = "CDO";
                request.Positions["LCD"].Behaviour = "CDO";
                request.Positions["LWB"].Behaviour = "WBD";
                request.Positions["RW"].Behaviour = "WO";
                request.Positions["RIM"].Behaviour = "IMO";
                break;
        }

        Assert.NotNull(MatchOrdersService.ValidateRequest(request));
        Assert.Throws<ArgumentException>(() => MatchOrdersService.BuildLineupJson(request));
    }

    private static XElement PlayerXml(bool includePassing, string passing = "8", bool suspended = false)
    {
        var fields = new List<XElement>
        {
            new("PlayerID", "42"), new("FirstName", "A"), new("LastName", "B"), new("Age", "24"),
            new("TSI", "1000"), new("PlayerForm", "7"), new("StaminaSkill", "6"), new("Experience", "5"),
            new("PlayerNumber", "1"), new("InjuryLevel", "-1"), new("Cards", "0"), new("IsSuspended", suspended ? "True" : "False"),
            new("MotherClubBonus", "True"), new("KeeperSkill", "4"), new("DefenderSkill", "7"),
            new("PlaymakerSkill", "8"), new("WingerSkill", "5"), new("ScorerSkill", "6"), new("SetPiecesSkill", "3")
        };
        if (includePassing) fields.Add(new XElement("PassingSkill", passing));
        return new XElement("Player", fields);
    }

    private static MatchOrdersRequest ValidOrderRequest(Dictionary<string, MatchOrderSlot> positions, int assistantLevel) => new()
    {
        MatchId = 123, Positions = positions, Tactic = "Normal", Attitude = "Normal", AssistantManagerLevel = assistantLevel,
        CaptainId = positions["GK"].PlayerId, SetPiecesTakerId = positions["GK"].PlayerId
    };

    private static Dictionary<string, MatchOrderSlot> ValidOrderPositions() => FormationData.Formations["4-4-2"].Positions.ToDictionary(
        p => p,
        p => new MatchOrderSlot { PlayerId = Array.IndexOf(FormationData.Formations["4-4-2"].Positions, p) + 1, Behaviour = p },
        StringComparer.Ordinal);
}
