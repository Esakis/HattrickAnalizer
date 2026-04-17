namespace HattrickAnalizer.Models;

public class Lineup
{
    public Dictionary<string, LineupPosition> Positions { get; set; } = new();
    public string TacticType { get; set; } = "";
    public string TacticSkill { get; set; } = "";
    public LineupRatings PredictedRatings { get; set; } = new();
    public string? Formation { get; set; }
}

public class LineupPosition
{
    public string Position { get; set; } = string.Empty;
    public Player? Player { get; set; }
    public string Behavior { get; set; } = "Normal";
    public double Rating { get; set; }
}

public class LineupRatings
{
    public double Midfield { get; set; }
    public double RightDefense { get; set; }
    public double CentralDefense { get; set; }
    public double LeftDefense { get; set; }
    public double RightAttack { get; set; }
    public double CentralAttack { get; set; }
    public double LeftAttack { get; set; }
    public double Overall { get; set; }
}
