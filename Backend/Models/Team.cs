namespace HattrickAnalizer.Models;

public class Team
{
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<Player> Players { get; set; } = new();
    public TeamRatings? Ratings { get; set; }
}

public class TeamRatings
{
    public int MidfieldRating { get; set; }
    public int RightDefenseRating { get; set; }
    public int CentralDefenseRating { get; set; }
    public int LeftDefenseRating { get; set; }
    public int RightAttackRating { get; set; }
    public int CentralAttackRating { get; set; }
    public int LeftAttackRating { get; set; }
    // Posrednie stale fragmenty gry (matchdetails: RatingIndirectSetPiecesAtt/Def).
    // 0 = brak danych (starsze wersje API).
    public int IndirectSetPiecesAttRating { get; set; }
    public int IndirectSetPiecesDefRating { get; set; }
}
