using System.Net;
using System.Text;
using System.Xml.Linq;
using HattrickAnalizer.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests;

public class TrainingStaffListTests
{
    [Fact]
    public async Task Current_stafflist_supplies_trainer_type_skill_and_assistant_total_with_provenance()
    {
        var summary = await GetSummaryAsync(StaffListXml("4", "1", "2", "2"));

        Assert.Equal(4, summary.CoachSkillRaw);
        Assert.Equal("Offensive", summary.CoachType);
        Assert.Equal(4, summary.AssistantSkillTotal);
        Assert.Equal("CHPP stafflist 1.2 TrainerSkillLevel", summary.CoachSkillSource);
        Assert.Equal("CHPP stafflist 1.2 TrainerType", summary.CoachTypeSource);
        Assert.Equal("CHPP stafflist 1.2 StaffMembers/Staff StaffType=1 StaffLevel", summary.AssistantSkillSource);
        Assert.Equal("WingAttacks", summary.TrainingTypeName);
    }

    [Fact]
    public async Task Malformed_current_staff_levels_stay_unknown_instead_of_using_invalid_values()
    {
        var summary = await GetSummaryAsync(StaffListXml("9", "-1", "1", "6"), teamId: 56);

        Assert.Null(summary.CoachSkillRaw);
        Assert.Null(summary.CoachType);
        Assert.Null(summary.AssistantSkillTotal);
        Assert.Contains(summary.Warnings, warning => warning.Contains("TrainerSkillLevel", StringComparison.Ordinal));
        Assert.Contains(summary.Warnings, warning => warning.Contains("TrainerType", StringComparison.Ordinal));
        Assert.Contains(summary.Warnings, warning => warning.Contains("assistant StaffLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_intensity_and_stamina_fields_remain_unknown_in_the_nullable_contract()
    {
        var summary = await GetSummaryAsync(StaffListXml("4", "1", "2", "2"), teamId: 57, omitTrainingSettings: true);

        Assert.Null(summary.TrainingIntensityPercent);
        Assert.Null(summary.StaminaTrainingSharePercent);
        Assert.False(summary.StaminaTrainingPartKnown);
        Assert.Contains(summary.Warnings, warning => warning.Contains("TrainingLevel or StaminaTrainingPart is unavailable", StringComparison.Ordinal));
    }

    private static async Task<TrainingSummary> GetSummaryAsync(string staffList, int teamId = 55, bool omitTrainingSettings = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UseMockData"] = "false",
            ["HattrickApi:ConsumerKey"] = "fixture-consumer",
            ["HattrickApi:ConsumerSecret"] = "fixture-secret",
            ["ConnectionStrings:HattrickDb"] = "",
            ["Training:UpdateDay"] = "Thursday",
            ["Training:UpdateHour"] = "22",
            ["Training:TimeZoneId"] = "Europe/Warsaw"
        }).Build();
        using var client = new HttpClient(new FixtureHandler(staffList, omitTrainingSettings));
        var oauth = new OAuthService(configuration, client);
        var tokenStore = new TokenStore(configuration, new EphemeralDataProtectionProvider(), NullLogger<TokenStore>.Instance);
        tokenStore.Save("staff-session", new StoredToken { AccessToken = $"fixture-token-{Guid.NewGuid():N}", AccessTokenSecret = "fixture-secret", OwnTeamId = teamId });
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = "ht_session=staff-session";
        var api = new HattrickApiService(client, configuration, oauth, tokenStore, new HttpContextAccessor { HttpContext = context },
            NullLogger<HattrickApiService>.Instance);
        return await new TrainingService(api, NullLogger<TrainingService>.Instance, configuration).GetSummaryAsync(teamId);
    }

    private static string StaffListXml(string coachSkill, string coachType, string firstAssistant, string secondAssistant) =>
        new XDocument(new XElement("HattrickData", new XElement("StaffList",
            new XElement("Trainer", new XElement("TrainerId", "88"), new XElement("Name", "Current Coach"),
                new XElement("TrainerSkillLevel", coachSkill), new XElement("TrainerType", coachType)),
            new XElement("StaffMembers",
                new XElement("Staff", new XElement("StaffType", "1"), new XElement("StaffLevel", firstAssistant)),
                new XElement("Staff", new XElement("StaffType", "1"), new XElement("StaffLevel", secondAssistant))))))
            .ToString(SaveOptions.DisableFormatting);

    private static string TrainingXml(bool omitSettings) => new XDocument(new XElement("HattrickData",
        new XElement("Team", new XElement("TrainingType", "12"),
            omitSettings ? null : new XElement("TrainingLevel", "90"),
            omitSettings ? null : new XElement("StaminaTrainingPart", "10"),
            new XElement("Morale", "5"), new XElement("SelfConfidence", "5"))))
        .ToString(SaveOptions.DisableFormatting);

    private static string PlayersXml() => new XDocument(new XElement("HattrickData", new XElement("Team",
        Enumerable.Range(1, 11).Select(i => new XElement("Player", new XElement("PlayerID", i),
            new XElement("FirstName", "Fixture"), new XElement("LastName", i), new XElement("Age", "25"),
            new XElement("TSI", "1"), new XElement("PlayerForm", "7"), new XElement("StaminaSkill", "7"),
            new XElement("Experience", "5"), new XElement("Loyalty", "1"), new XElement("Leadership", "1"),
            new XElement("InjuryLevel", "-1"), new XElement("Cards", "0"), new XElement("MotherClubBonus", "True"),
            new XElement("KeeperSkill", "5"), new XElement("DefenderSkill", "5"), new XElement("PlaymakerSkill", "5"),
            new XElement("WingerSkill", "5"), new XElement("PassingSkill", "5"), new XElement("ScorerSkill", "5"),
            new XElement("SetPiecesSkill", "5")))))).ToString(SaveOptions.DisableFormatting);

    private sealed class FixtureHandler(string staffList, bool omitTrainingSettings) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]),
                    part => part.Length > 1 ? Uri.UnescapeDataString(part[1]) : "", StringComparer.OrdinalIgnoreCase);
            var xml = query["file"] switch
            {
                "training" => TrainingXml(omitTrainingSettings),
                "stafflist" => staffList,
                "players" => PlayersXml(),
                "matches" => "<HattrickData />",
                "matchlineup" or "matchdetails" => "<HattrickData />",
                _ => throw new InvalidOperationException($"Unexpected CHPP fixture request: {query["file"]}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });
        }
    }
}
