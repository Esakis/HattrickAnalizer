namespace HattrickAnalizer.Models;

public class TeamMatchStats
{
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<MatchRecord> MatchHistory { get; set; } = new();
    public TeamStatisticsSummary Statistics { get; set; } = new();
}

public class MatchRecord
{
    public long MatchId { get; set; }
    public DateTime MatchDate { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string OpponentTeam { get; set; } = string.Empty;
    public bool IsHomeMatch { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int TeamPoints { get; set; }
    public string MatchResult { get; set; } = string.Empty;
    public string FormationUsed { get; set; } = string.Empty;
    public int MidfieldRating { get; set; }
    public int RightDefenseRating { get; set; }
    public int CentralDefenseRating { get; set; }
    public int LeftDefenseRating { get; set; }
    public int RightAttackRating { get; set; }
    public int CentralAttackRating { get; set; }
    public int LeftAttackRating { get; set; }
    public int Possession { get; set; }
    public string Attitude { get; set; } = string.Empty; // PIC, Normal, MOTS
    public string TeamSpirit { get; set; } = string.Empty;
}

public class TeamStatisticsSummary
{
    public int TotalMatches { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int GoalDifference { get; set; }
    public int TotalPoints { get; set; }
    public double AverageGoalsFor { get; set; }
    public double AverageGoalsAgainst { get; set; }
    public double PointsPerMatch { get; set; }
    public double WinRate { get; set; }
    public string MostCommonFormation { get; set; } = string.Empty;
    public Dictionary<string, int> FormationFrequency { get; set; } = new();
    public Dictionary<string, double> FormationWinRate { get; set; } = new();
    public Dictionary<string, double> FormationPointsPerMatch { get; set; } = new();
    public Dictionary<string, int> FormationGoalsFor { get; set; } = new();
    public Dictionary<string, int> FormationGoalsAgainst { get; set; } = new();
    public int BestRating { get; set; }
    public int WorstRating { get; set; }
    public double AverageMidfieldRating { get; set; }
    public double AverageDefenseRating { get; set; }
    public double AverageAttackRating { get; set; }
    public DateTime? LastMatchDate { get; set; }
    public string LastMatchResult { get; set; } = string.Empty;
    public string Form { get; set; } = string.Empty; // ostatnie 5 meczów
    public List<string> RecentResults { get; set; } = new();
    public int HomeWins { get; set; }
    public int HomeDraws { get; set; }
    public int HomeLosses { get; set; }
    public int AwayWins { get; set; }
    public int AwayDraws { get; set; }
    public int AwayLosses { get; set; }
    public Dictionary<string, int> GoalsByMatchType { get; set; } = new();
    public Dictionary<string, int> PointsByMatchType { get; set; } = new();
}

public class MatchDetails
{
    public string Formation { get; set; } = string.Empty;
    public int MidfieldRating { get; set; }
    public int RightDefenseRating { get; set; }
    public int CentralDefenseRating { get; set; }
    public int LeftDefenseRating { get; set; }
    public int RightAttackRating { get; set; }
    public int CentralAttackRating { get; set; }
    public int LeftAttackRating { get; set; }
    public int Possession { get; set; }
    public string Attitude { get; set; } = string.Empty;
    public string TeamSpirit { get; set; } = string.Empty;
}

public static class TeamStatsExtensions
{
    public static void CalculateStatistics(this TeamMatchStats teamStats)
    {
        var summary = teamStats.Statistics;
        var matches = teamStats.MatchHistory;

        summary.TotalMatches = matches.Count;
        summary.Wins = matches.Count(m => m.TeamPoints == 3);
        summary.Draws = matches.Count(m => m.TeamPoints == 1);
        summary.Losses = matches.Count(m => m.TeamPoints == 0);
        summary.GoalsFor = matches.Sum(m => m.GoalsFor);
        summary.GoalsAgainst = matches.Sum(m => m.GoalsAgainst);
        summary.GoalDifference = summary.GoalsFor - summary.GoalsAgainst;
        summary.TotalPoints = matches.Sum(m => m.TeamPoints);

        summary.AverageGoalsFor = summary.TotalMatches > 0 ? (double)summary.GoalsFor / summary.TotalMatches : 0;
        summary.AverageGoalsAgainst = summary.TotalMatches > 0 ? (double)summary.GoalsAgainst / summary.TotalMatches : 0;
        summary.PointsPerMatch = summary.TotalMatches > 0 ? (double)summary.TotalPoints / summary.TotalMatches : 0;
        summary.WinRate = summary.TotalMatches > 0 ? (double)summary.Wins / summary.TotalMatches * 100 : 0;

        // Statystyki formacji (cała historia)
        summary.FormationFrequency = matches
            .GroupBy(m => m.FormationUsed)
            .ToDictionary(g => g.Key, g => g.Count());

        // Średnia formacja z ostatnich 5 meczów
        var last5formations = matches
            .OrderByDescending(m => m.MatchDate)
            .Take(5)
            .Select(m => ParseFormation(m.FormationUsed))
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        if (last5formations.Count > 0)
        {
            var d = (int)Math.Round(last5formations.Average(p => p.d));
            var mid = (int)Math.Round(last5formations.Average(p => p.m));
            var f = (int)Math.Round(last5formations.Average(p => p.f));
            summary.MostCommonFormation = $"{d}-{mid}-{f}";
        }
        else
        {
            summary.MostCommonFormation = summary.FormationFrequency
                .OrderByDescending(kvp => kvp.Value)
                .FirstOrDefault().Key ?? "N/A";
        }

        // Statystyki w zaleznoeci od miejsca
        summary.HomeWins = matches.Count(m => m.IsHomeMatch && m.TeamPoints == 3);
        summary.HomeDraws = matches.Count(m => m.IsHomeMatch && m.TeamPoints == 1);
        summary.HomeLosses = matches.Count(m => m.IsHomeMatch && m.TeamPoints == 0);
        summary.AwayWins = matches.Count(m => !m.IsHomeMatch && m.TeamPoints == 3);
        summary.AwayDraws = matches.Count(m => !m.IsHomeMatch && m.TeamPoints == 1);
        summary.AwayLosses = matches.Count(m => !m.IsHomeMatch && m.TeamPoints == 0);

        // Ocena
        if (matches.Any())
        {
            summary.AverageMidfieldRating = matches.Average(m => m.MidfieldRating);
            summary.AverageDefenseRating = matches.Average(m => 
                (m.RightDefenseRating + m.CentralDefenseRating + m.LeftDefenseRating) / 3.0);
            summary.AverageAttackRating = matches.Average(m => 
                (m.RightAttackRating + m.CentralAttackRating + m.LeftAttackRating) / 3.0);
            summary.BestRating = matches.Max(m => Math.Max(Math.Max(
                Math.Max(m.MidfieldRating, m.CentralDefenseRating), 
                Math.Max(m.RightDefenseRating, m.LeftDefenseRating)), 
                Math.Max(Math.Max(m.CentralAttackRating, m.RightAttackRating), m.LeftAttackRating)));
            summary.WorstRating = matches.Min(m => Math.Min(Math.Min(
                Math.Min(m.MidfieldRating, m.CentralDefenseRating), 
                Math.Min(m.RightDefenseRating, m.LeftDefenseRating)), 
                Math.Min(Math.Min(m.CentralAttackRating, m.RightAttackRating), m.LeftAttackRating)));
        }

        // Ostatnie mecze
        var recentMatches = matches.OrderByDescending(m => m.MatchDate).Take(5).ToList();
        summary.RecentResults = recentMatches.Select(m => m.TeamPoints switch
        {
            3 => "W",
            1 => "D",
            0 => "L",
            _ => "-"
        }).ToList();
        summary.Form = string.Join("", summary.RecentResults);

        // Statystyki wg typu meczu
        summary.GoalsByMatchType = matches
            .GroupBy(m => m.MatchType)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.GoalsFor));

        summary.PointsByMatchType = matches
            .GroupBy(m => m.MatchType)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.TeamPoints));
    }

    private static (int d, int m, int f)? ParseFormation(string formation)
    {
        var parts = formation.Split('-');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int d) &&
            int.TryParse(parts[1], out int m) &&
            int.TryParse(parts[2], out int f))
            return (d, m, f);
        return null;
    }
}
