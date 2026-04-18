namespace HattrickAnalizer.Models;

public class PlayerSkillHistory
{
    public int Id { get; set; }
    public int PlayerId { get; set; }
    public int TeamId { get; set; }
    public DateTime RecordedDate { get; set; }
    // Umiejętności
    public int Keeper { get; set; }
    public int Defending { get; set; }
    public int Playmaking { get; set; }
    public int Winger { get; set; }
    public int Passing { get; set; }
    public int Scoring { get; set; }
    public int SetPieces { get; set; }
    // Podstawowe
    public int Form { get; set; }
    public int Stamina { get; set; }
    public int Age { get; set; }
    public int TSI { get; set; }
    public int Experience { get; set; }
    public int Loyalty { get; set; }
    public int Leadership { get; set; }
    public int InjuryLevel { get; set; }
    // Statystyki meczowe
    public int TotalMatches { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }
    public double AverageRating { get; set; }
    public double AverageForm { get; set; }
    public int MinutesPlayed { get; set; }
}
