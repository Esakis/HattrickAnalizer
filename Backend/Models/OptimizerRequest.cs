namespace HattrickAnalizer.Models;

public class OptimizerRequest
{
    public int MyTeamId { get; set; }
    public int OpponentTeamId { get; set; }
    public string PreferredTactic { get; set; } = "Normal";
    public List<string> FocusAreas { get; set; } = new();
}

public class OptimizerResponse
{
    public Lineup OptimalLineup { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public TeamComparison Comparison { get; set; } = new();
}

public class TeamComparison
{
    public LineupRatings MyTeamRatings { get; set; } = new();
    public LineupRatings OpponentRatings { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public List<string> Weaknesses { get; set; } = new();
}
