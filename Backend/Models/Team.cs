namespace HattrickAnalizer.Models;

public class Team
{
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<Player> Players { get; set; } = new();
    public TeamRatings? Ratings { get; set; }
    public DataProvenance Provenance { get; set; } = new();
    public int? TeamSpiritLevel { get; set; }
    public int? ConfidenceLevel { get; set; }
}

public class TeamRatings
{
    public double MidfieldRating { get; set; }
    public double RightDefenseRating { get; set; }
    public double CentralDefenseRating { get; set; }
    public double LeftDefenseRating { get; set; }
    public double RightAttackRating { get; set; }
    public double CentralAttackRating { get; set; }
    public double LeftAttackRating { get; set; }
    // Posrednie stale fragmenty gry (matchdetails: RatingIndirectSetPiecesAtt/Def).
    // 0 = brak danych (starsze wersje API).
    public double IndirectSetPiecesAttRating { get; set; }
    public double IndirectSetPiecesDefRating { get; set; }
}
