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
    public bool StaminaAvailable { get; set; }
    public int Experience { get; set; }
    public int Loyalty { get; set; }
    // Bonus klubu macierzystego: gracz wychowany w klubie dostaje +1.5 poziomu
    // do kazdej umiejetnosci (zamiast bonusu z lojalnosci).
    public bool MotherClubBonus { get; set; }
    public int Leadership { get; set; }
    public string Specialty { get; set; } = string.Empty;
    public int InjuryLevel { get; set; }
    public bool InjuryStatusKnown { get; set; }
    public int ShirtNumber { get; set; }
    public PlayerMatchStats? MatchStats { get; set; }
    public DataProvenance Provenance { get; set; } = new();
    public bool IsSuspended { get; set; }
    public bool SuspensionStatusKnown { get; set; }
    public bool SkillsAvailable => Skills.HasAllSkills && StaminaAvailable;
    public bool CanOptimize => SkillsAvailable && InjuryStatusKnown && InjuryLevel <= 0
        && SuspensionStatusKnown && !IsSuspended;
    public bool CanOptimizeForMatch(bool? isSuspendedForMatch) =>
        CanOptimize && isSuspendedForMatch.HasValue && !isSuspendedForMatch.Value;
}

public class PlayerMatchStats
{
    public int TotalMatches { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }
    public double AverageRating { get; set; }
    public double AverageForm { get; set; }
    public double GoalsPerMatch { get; set; }
    public double MatchesPerGoal { get; set; }
    public int MinutesPlayed { get; set; }
    public Dictionary<string, double> PositionRatings { get; set; } = new();
}

public class PlayerSkills
{
    public int Keeper { get; set; }
    public bool KeeperAvailable { get; set; }
    public int Defending { get; set; }
    public bool DefendingAvailable { get; set; }
    public int Playmaking { get; set; }
    public bool PlaymakingAvailable { get; set; }
    public int Winger { get; set; }
    public bool WingerAvailable { get; set; }
    public int Passing { get; set; }
    public bool PassingAvailable { get; set; }
    public int Scoring { get; set; }
    public bool ScoringAvailable { get; set; }
    public int SetPieces { get; set; }
    public bool SetPiecesAvailable { get; set; }

    public bool HasAllSkills => KeeperAvailable && DefendingAvailable && PlaymakingAvailable
        && WingerAvailable && PassingAvailable && ScoringAvailable && SetPiecesAvailable;
}

public class DataProvenance
{
    public string Source { get; set; } = "unknown";
    public DateTimeOffset? RetrievedAt { get; set; }
    public List<string> Warnings { get; set; } = new();
    public int SampleCount { get; set; }
}
