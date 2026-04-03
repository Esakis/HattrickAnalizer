namespace HattrickAnalizer.Models;

public class Player
{
    public int PlayerId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }
    public int TSI { get; set; }
    public PlayerSkills Skills { get; set; } = new();
    public int Form { get; set; }
    public int Stamina { get; set; }
    public int Experience { get; set; }
    public int Loyalty { get; set; }
    public int Leadership { get; set; }
    public string Specialty { get; set; } = string.Empty;
    public int InjuryLevel { get; set; }
}

public class PlayerSkills
{
    public int Keeper { get; set; }
    public int Defending { get; set; }
    public int Playmaking { get; set; }
    public int Winger { get; set; }
    public int Passing { get; set; }
    public int Scoring { get; set; }
    public int SetPieces { get; set; }
}
