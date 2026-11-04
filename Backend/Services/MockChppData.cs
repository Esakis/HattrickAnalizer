using System.Globalization;
using System.Xml.Linq;

namespace HattrickAnalizer.Services;

/// <summary>Explicit offline CHPP fixtures. Unsupported calls fail instead of reaching a live account.</summary>
public static class MockChppData
{
    public const int TeamId = 1001;
    public const string TeamName = "FC Przykład";
    public static string Name(int id) => id == TeamId ? TeamName : $"Klub demonstracyjny {id % 1000}";

    public static XDocument Create(IReadOnlyDictionary<string, string> parameters)
    {
        string Value(string name, string fallback = "") => parameters.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? fallback;
        int Number(string name, int fallback) => int.TryParse(Value(name), out var n) ? n : fallback;
        var file = Value("file").ToLowerInvariant();
        var action = Value("actionType", "view");
        if (action is not ("view" or "")) throw new InvalidOperationException($"Demo mode does not support CHPP action '{action}'. No network request was made.");
        var teamId = Number("teamId", TeamId);
        var matchId = Number("matchId", teamId * 100 + 1);
        var homeId = matchId / 100;
        if (homeId <= 0) homeId = TeamId;
        var sequence = matchId % 100;
        var awayId = homeId == TeamId + 7 ? TeamId : homeId + 1;
        var date = sequence >= 98 ? DateTime.UtcNow.Date.AddDays(sequence == 99 ? 1 : 3).AddHours(18)
            : DateTime.UtcNow.AddDays(-7 * Math.Max(0, sequence - 1)).AddHours(-4);
        var root = new XElement("HattrickData", new XElement("FileName", file + ".xml"),
            new XElement("FetchedDate", Stamp(DateTime.UtcNow)), new XElement("MockData", true));
        switch (file)
        {
            case "teamdetails":
                root.Add(new XElement("Teams", new XElement("Team", new XElement("TeamID", teamId),
                    new XElement("TeamName", Name(teamId)), new XElement("IsPrimaryClub", true),
                    new XElement("TeamSpirit", 5), new XElement("Confidence", 5),
                    new XElement("Region", new XElement("RegionID", 1), new XElement("RegionName", "Region demonstracyjny")),
                    new XElement("LeagueLevelUnit", new XElement("LeagueLevelUnitID", 9001), new XElement("LeagueLevelUnitName", "Liga demonstracyjna")))));
                break;
            case "matches":
                root.Add(new XElement("Team", new XElement("TeamID", teamId), new XElement("MatchList",
                    Enumerable.Range(1, 5).Select(i => Match(teamId, i)), Match(teamId, 99), Match(teamId, 98))));
                break;
            case "matchdetails":
                root.Add(new XElement("Match", new XElement("MatchID", matchId), new XElement("MatchDate", Stamp(date)),
                    new XElement("FinishedDate", Stamp(date.AddMinutes(110))), new XElement("AddedMinutes", 0),
                    new XElement("MatchType", 1), new XElement("WeatherID", 2),
                    MatchSide("Home", homeId, sequence), MatchSide("Away", awayId, sequence),
                    new XElement("EventList"), new XElement("Bookings")));
                break;
            case "matchlineup":
                var players = FormationData.Formations["4-4-2"].Positions.Select((slot, index) => new XElement("Player",
                    new XElement("PlayerID", index + 1), new XElement("FirstName", $"Player{index + 1}"),
                    new XElement("LastName", $"Surname{index + 1}"), new XElement("RoleID", MatchOrdersService.SlotToRoleId(slot)),
                    new XElement("Behaviour", 0), new XElement("PlayedMinutes", 90), new XElement("RatingStars", 4.5))).ToList();
                root.Add(new XElement("MatchID", matchId), new XElement("Team", new XElement("TeamID", teamId),
                    new XElement("SetPiecesTakerID", 1), new XElement("StartingLineup", players.Select(p => new XElement(p))),
                    new XElement("Substitutions"), new XElement("Lineup", players.Select(p => new XElement(p)))));
                break;
            case "training":
                root.Add(new XElement("Team", new XElement("TeamID", teamId), new XElement("TrainingType", 8),
                    new XElement("TrainingLevel", 100), new XElement("StaminaTrainingPart", 15),
                    new XElement("Morale", 5), new XElement("SelfConfidence", 5),
                    FormationData.Formations.Keys.Select(f => new XElement("Experience" + f.Replace("-", ""), 8))));
                break;
            case "stafflist":
                root.Add(new XElement("StaffList", new XElement("Trainer", new XElement("TrainerId", 900),
                    new XElement("Name", "Trener demonstracyjny"), new XElement("TrainerSkillLevel", 4), new XElement("TrainerType", 2)),
                    new XElement("StaffMembers", Enumerable.Range(1, 2).Select(i => new XElement("Staff",
                        new XElement("StaffId", i), new XElement("StaffType", 1), new XElement("StaffLevel", 4))))));
                break;
            case "regiondetails":
                root.Add(new XElement("Region", new XElement("RegionID", 1), new XElement("WeatherID", 2), new XElement("TomorrowWeatherID", 2)));
                break;
            case "leaguedetails":
                root.Add(new XElement("LeagueLevelUnitName", "Liga demonstracyjna"), new XElement("Teams",
                    Enumerable.Range(0, 8).Select(i => new XElement("Team", new XElement("TeamID", TeamId + i),
                        new XElement("TeamName", Name(TeamId + i)), new XElement("Position", i + 1), new XElement("Matches", 10),
                        new XElement("GoalsFor", 25 - i), new XElement("GoalsAgainst", 8 + i), new XElement("Points", 25 - i * 3)))));
                break;
            case "leaguefixtures":
                root.Add(new XElement("Matches", Enumerable.Range(0, 4).SelectMany(round => Enumerable.Range(0, 4).Select(pair =>
                    new XElement("Match", new XElement("MatchID", 900000 + round * 10 + pair),
                        new XElement("MatchDate", Stamp(DateTime.UtcNow.Date.AddDays(1 + 7 * round).AddHours(18))),
                        TeamSide("Home", TeamId + pair), TeamSide("Away", TeamId + 4 + (pair + round) % 4))))));
                break;
            case "players":
                root.Add(new XElement("Team", new XElement("TeamID", teamId), new XElement("PlayerList",
                    HattrickApiService.GenerateMockPlayers().Select(p => new XElement("Player", new XElement("PlayerID", p.PlayerId),
                        new XElement("FirstName", p.FirstName), new XElement("LastName", p.LastName), new XElement("Age", p.Age),
                        new XElement("PlayerForm", p.Form), new XElement("StaminaSkill", p.Stamina), new XElement("InjuryLevel", p.InjuryLevel),
                        new XElement("IsSuspended", p.IsSuspended), new XElement("Cards", p.IsSuspended ? 3 : 0),
                        new XElement("KeeperSkill", p.Skills.Keeper), new XElement("DefenderSkill", p.Skills.Defending),
                        new XElement("PlaymakerSkill", p.Skills.Playmaking), new XElement("WingerSkill", p.Skills.Winger),
                        new XElement("PassingSkill", p.Skills.Passing), new XElement("ScorerSkill", p.Skills.Scoring),
                        new XElement("SetPiecesSkill", p.Skills.SetPieces))))));
                break;
            default: throw new InvalidOperationException($"No offline fixture exists for CHPP '{file}'. No network request was made.");
        }
        return new XDocument(root);
    }

    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    private static XElement TeamSide(string side, int id) => new(side + "Team",
        new XElement(side + "TeamID", id), new XElement(side + "TeamName", Name(id)));
    private static XElement Match(int teamId, int sequence)
    {
        var element = new XElement("Match", new XElement("MatchID", teamId * 100 + sequence),
            new XElement("MatchDate", Stamp(sequence >= 98 ? DateTime.UtcNow.Date.AddDays(sequence == 99 ? 1 : 3).AddHours(18)
                : DateTime.UtcNow.AddDays(-7 * (sequence - 1)).AddHours(-4))), new XElement("MatchType", sequence == 98 ? 4 : 1),
            new XElement("Status", sequence >= 98 ? "UPCOMING" : "FINISHED"),
            TeamSide("Home", teamId), TeamSide("Away", teamId == TeamId + 7 ? TeamId : teamId + 1));
        if (sequence < 98) element.Add(new XElement("HomeGoals", 1 + sequence % 3), new XElement("AwayGoals", sequence % 2));
        return element;
    }
    private static XElement MatchSide(string side, int id, int sequence)
    {
        var element = TeamSide(side, id);
        element.Add(new XElement("Formation", "4-4-2"), new XElement("TacticType", sequence % 3 == 0 ? 3 : 0),
            new XElement("RatingIndirectSetPiecesAtt", 7), new XElement("RatingIndirectSetPiecesDef", 8));
        var fields = new[] { "RatingMidfield", "RatingRightDef", "RatingMidDef", "RatingLeftDef", "RatingRightAtt", "RatingMidAtt", "RatingLeftAtt" };
        foreach (var (field, i) in fields.Select((f, i) => (f, i)))
            element.Add(new XElement(field, 6 + (id + sequence + i) % 5));
        return element;
    }
}
