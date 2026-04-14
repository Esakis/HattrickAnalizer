namespace HattrickAnalizer.Models;

public class OptimizerRequest
{
    public int MyTeamId { get; set; }
    public int OpponentTeamId { get; set; }
    public string PreferredTactic { get; set; } = "Auto";
    // Postawa druzyny: "Normal" (normalne spotkanie), "PIC" (gra na luzie), "MOTS" (mecz sezonu)
    public string TeamAttitude { get; set; } = "Normal";
    public List<string> FocusAreas { get; set; } = new();

    // Typ trenera: "Offensive", "Defensive", "Neutral"
    public string CoachType { get; set; } = "Neutral";
    // Poziom asystenta ds. taktyki (0-5)
    public int AssistantManagerLevel { get; set; } = 0;
    // Doswiadczenie formacji: nazwa formacji (np. "4-4-2") -> poziom 0..7
    // 0=nedzne, 1=zaloslne, 2=zle, 3=slabe, 4=niewystarczajace, 5=przyzwoite, 6=solidne, 7=znakomite
    public Dictionary<string, int> FormationExperience { get; set; } = new();
}

public class OptimizerResponse
{
    public Lineup OptimalLineup { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public TeamComparison Comparison { get; set; } = new();
    // Alternatywne formacje (top N) do podgladu przez uzytkownika
    public List<FormationAlternative> Alternatives { get; set; } = new();
}

public class FormationAlternative
{
    public string Formation { get; set; } = "";
    public string Tactic { get; set; } = "";
    public string Attitude { get; set; } = "Normal";
    public double WinProbability { get; set; }
    public double DrawProbability { get; set; }
    public double LossProbability { get; set; }
    public double ExpectedGoalsFor { get; set; }
    public double ExpectedGoalsAgainst { get; set; }
    public double DisorderRisk { get; set; }
    public LineupRatings Ratings { get; set; } = new();
}

public class TeamComparison
{
    public LineupRatings MyTeamRatings { get; set; } = new();
    public LineupRatings OpponentRatings { get; set; } = new();
    public List<string> Strengths { get; set; } = new();
    public List<string> Weaknesses { get; set; } = new();
}
