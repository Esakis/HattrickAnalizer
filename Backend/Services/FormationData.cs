namespace HattrickAnalizer.Services;

/// <summary>
/// Definicje formacji i kontrybucji pozycji oparte na poradnikach Hattrick
/// </summary>
public static class FormationData
{
    // Dostepne formacje z pozycjami
    public static readonly Dictionary<string, FormationDefinition> Formations = new()
    {
        ["5-5-0"] = new FormationDefinition
        {
            Name = "5-5-0",
            Defenders = 5,
            Midfielders = 5,
            Forwards = 0,
            Positions = new[] { "GK", "RWB", "RCD", "CD", "LCD", "LWB", "RW", "RIM", "IM", "LIM", "LW" },
            Style = FormationStyle.UltraDefensive,
            Description = "Maksymalna obrona i pomoc, brak napastników"
        },
        ["5-4-1"] = new FormationDefinition
        {
            Name = "5-4-1",
            Defenders = 5,
            Midfielders = 4,
            Forwards = 1,
            Positions = new[] { "GK", "RWB", "RCD", "CD", "LCD", "LWB", "RW", "RIM", "LIM", "LW", "FW" },
            Style = FormationStyle.Defensive,
            Description = "Defensywna formacja, idealna do kontry"
        },
        ["5-3-2"] = new FormationDefinition
        {
            Name = "5-3-2",
            Defenders = 5,
            Midfielders = 3,
            Forwards = 2,
            Positions = new[] { "GK", "RWB", "RCD", "CD", "LCD", "LWB", "RIM", "IM", "LIM", "RFW", "LFW" },
            Style = FormationStyle.Defensive,
            Description = "Solidna obrona z dwoma napastnikami"
        },
        ["4-5-1"] = new FormationDefinition
        {
            Name = "4-5-1",
            Defenders = 4,
            Midfielders = 5,
            Forwards = 1,
            Positions = new[] { "GK", "RWB", "RCD", "LCD", "LWB", "RW", "RIM", "IM", "LIM", "LW", "FW" },
            Style = FormationStyle.MidfieldControl,
            Description = "Kontrola pomocy z jednym napastnikiem"
        },
        ["4-4-2"] = new FormationDefinition
        {
            Name = "4-4-2",
            Defenders = 4,
            Midfielders = 4,
            Forwards = 2,
            Positions = new[] { "GK", "RWB", "RCD", "LCD", "LWB", "RW", "RIM", "LIM", "LW", "RFW", "LFW" },
            Style = FormationStyle.Balanced,
            Description = "Zbalansowana formacja"
        },
        ["4-3-3"] = new FormationDefinition
        {
            Name = "4-3-3",
            Defenders = 4,
            Midfielders = 3,
            Forwards = 3,
            Positions = new[] { "GK", "RWB", "RCD", "LCD", "LWB", "RW", "IM", "LW", "RFW", "FW", "LFW" },
            Style = FormationStyle.Offensive,
            Description = "Ofensywna formacja z trzema napastnikami"
        },
        ["3-5-2"] = new FormationDefinition
        {
            Name = "3-5-2",
            Defenders = 3,
            Midfielders = 5,
            Forwards = 2,
            Positions = new[] { "GK", "RCD", "CD", "LCD", "RW", "RIM", "IM", "LIM", "LW", "RFW", "LFW" },
            Style = FormationStyle.MidfieldControl,
            Description = "Ofensywna z mocnym pomocy"
        },
        ["3-4-3"] = new FormationDefinition
        {
            Name = "3-4-3",
            Defenders = 3,
            Midfielders = 4,
            Forwards = 3,
            Positions = new[] { "GK", "RCD", "CD", "LCD", "RW", "RIM", "LIM", "LW", "RFW", "FW", "LFW" },
            Style = FormationStyle.UltraOffensive,
            Description = "Bardzo ofensywna formacja"
        },
        ["2-5-3"] = new FormationDefinition
        {
            Name = "2-5-3",
            Defenders = 2,
            Midfielders = 5,
            Forwards = 3,
            Positions = new[] { "GK", "RCD", "LCD", "RW", "RIM", "IM", "LIM", "LW", "RFW", "FW", "LFW" },
            Style = FormationStyle.UltraOffensive,
            Description = "Ekstremalnie ofensywna, saba obrona"
        },
        ["5-2-3"] = new FormationDefinition
        {
            Name = "5-2-3",
            Defenders = 5,
            Midfielders = 2,
            Forwards = 3,
            Positions = new[] { "GK", "RWB", "RCD", "CD", "LCD", "LWB", "RW", "LW", "RFW", "FW", "LFW" },
            Style = FormationStyle.Offensive,
            Description = "Silna obrona z trójk napastników"
        }
    };

    // Kontrybucje pozycji do ratingów (na podstawie poradników)
    public static readonly Dictionary<string, PositionContribution> PositionContributions = new()
    {
        // Bramkarz
        ["GK"] = new PositionContribution
        {
            Position = "GK",
            MidfieldPM = 0,
            CentralDefenseGK = 0.87,
            CentralDefenseDef = 0.35,
            SideDefenseGK = 0.61,
            SideDefenseDef = 0.25,
            CentralAttackSc = 0,
            CentralAttackPs = 0,
            SideAttackWg = 0,
            SideAttackPs = 0,
            SideAttackSc = 0
        },
        // Pomocnik - ujednolicony
        ["IM"] = new PositionContribution
        {
            Position = "IM",
            MidfieldPM = 1.0,
            CentralDefenseDef = 0.40,
            SideDefenseDef = 0.09,
            CentralAttackSc = 0.22,
            CentralAttackPs = 0.33,
            SideAttackPs = 0.13
        },
        ["RIM"] = new PositionContribution
        {
            Position = "RIM",
            MidfieldPM = 1.0,
            CentralDefenseDef = 0.40,
            SideDefenseDef = 0.19,
            CentralAttackSc = 0.22,
            CentralAttackPs = 0.33,
            SideAttackPs = 0.26
        },
        ["LIM"] = new PositionContribution
        {
            Position = "LIM",
            MidfieldPM = 1.0,
            CentralDefenseDef = 0.40,
            SideDefenseDef = 0.19,
            CentralAttackSc = 0.22,
            CentralAttackPs = 0.33,
            SideAttackPs = 0.26
        },
        // Obrocy
        ["CD"] = new PositionContribution
        {
            Position = "CD",
            MidfieldPM = 0.25,
            CentralDefenseDef = 1.0,
            SideDefenseDef = 0.26
        },
        ["RCD"] = new PositionContribution
        {
            Position = "RCD",
            MidfieldPM = 0.25,
            CentralDefenseDef = 1.0,
            SideDefenseDef = 0.52
        },
        ["LCD"] = new PositionContribution
        {
            Position = "LCD",
            MidfieldPM = 0.25,
            CentralDefenseDef = 1.0,
            SideDefenseDef = 0.52
        },
        ["CDO"] = new PositionContribution
        {
            Position = "CDO",
            MidfieldPM = 0.40,
            CentralDefenseDef = 0.73,
            SideDefenseDef = 0.20
        },
        ["CDTW"] = new PositionContribution
        {
            Position = "CDTW",
            MidfieldPM = 0.15,
            CentralDefenseDef = 0.67,
            SideDefenseDef = 0.81,
            SideAttackWg = 0.26
        },
        // Boczni obrocy
        ["WBD"] = new PositionContribution
        {
            Position = "WBD",
            MidfieldPM = 0.10,
            CentralDefenseDef = 0.43,
            SideDefenseDef = 1.0,
            SideAttackWg = 0.45
        },
        ["WBN"] = new PositionContribution
        {
            Position = "WBN",
            MidfieldPM = 0.15,
            CentralDefenseDef = 0.38,
            SideDefenseDef = 0.92,
            SideAttackWg = 0.59
        },
        ["WBO"] = new PositionContribution
        {
            Position = "WBO",
            MidfieldPM = 0.20,
            CentralDefenseDef = 0.35,
            SideDefenseDef = 0.74,
            SideAttackWg = 0.69
        },
        ["WBTM"] = new PositionContribution
        {
            Position = "WBTM",
            MidfieldPM = 0.20,
            CentralDefenseDef = 0.70,
            SideDefenseDef = 0.75,
            SideAttackWg = 0.35
        },
        ["RWB"] = new PositionContribution
        {
            Position = "RWB",
            MidfieldPM = 0.15,
            CentralDefenseDef = 0.38,
            SideDefenseDef = 0.92,
            SideAttackWg = 0.59
        },
        ["LWB"] = new PositionContribution
        {
            Position = "LWB",
            MidfieldPM = 0.15,
            CentralDefenseDef = 0.38,
            SideDefenseDef = 0.92,
            SideAttackWg = 0.59
        },
        // Skrzydowi
        ["RW"] = new PositionContribution
        {
            Position = "RW",
            MidfieldPM = 0.45,
            CentralDefenseDef = 0.20,
            SideDefenseDef = 0.35,
            CentralAttackPs = 0.11,
            SideAttackWg = 0.86,
            SideAttackPs = 0.26
        },
        ["LW"] = new PositionContribution
        {
            Position = "LW",
            MidfieldPM = 0.45,
            CentralDefenseDef = 0.20,
            SideDefenseDef = 0.35,
            CentralAttackPs = 0.11,
            SideAttackWg = 0.86,
            SideAttackPs = 0.26
        },
        ["WO"] = new PositionContribution
        {
            Position = "WO",
            MidfieldPM = 0.30,
            CentralDefenseDef = 0.13,
            SideDefenseDef = 0.22,
            CentralAttackPs = 0.13,
            SideAttackWg = 1.0,
            SideAttackPs = 0.29
        },
        ["WD"] = new PositionContribution
        {
            Position = "WD",
            MidfieldPM = 0.30,
            CentralDefenseDef = 0.25,
            SideDefenseDef = 0.61,
            CentralAttackPs = 0.05,
            SideAttackWg = 0.69,
            SideAttackPs = 0.21
        },
        ["WTM"] = new PositionContribution
        {
            Position = "WTM",
            MidfieldPM = 0.55,
            CentralDefenseDef = 0.25,
            SideDefenseDef = 0.29,
            CentralAttackPs = 0.16,
            SideAttackWg = 0.74,
            SideAttackPs = 0.15
        },
        // Napastnicy
        ["FW"] = new PositionContribution
        {
            Position = "FW",
            MidfieldPM = 0.25,
            CentralAttackSc = 1.0,
            CentralAttackPs = 0.33,
            SideAttackWg = 0.24,
            SideAttackPs = 0.14,
            SideAttackSc = 0.27
        },
        ["RFW"] = new PositionContribution
        {
            Position = "RFW",
            MidfieldPM = 0.25,
            CentralAttackSc = 1.0,
            CentralAttackPs = 0.33,
            SideAttackWg = 0.24,
            SideAttackPs = 0.14,
            SideAttackSc = 0.27
        },
        ["LFW"] = new PositionContribution
        {
            Position = "LFW",
            MidfieldPM = 0.25,
            CentralAttackSc = 1.0,
            CentralAttackPs = 0.33,
            SideAttackWg = 0.24,
            SideAttackPs = 0.14,
            SideAttackSc = 0.27
        },
        ["CFW"] = new PositionContribution
        {
            Position = "CFW",
            MidfieldPM = 0.25,
            CentralAttackSc = 1.0,
            CentralAttackPs = 0.33,
            SideAttackWg = 0.24,
            SideAttackPs = 0.14,
            SideAttackSc = 0.27
        },
        ["FTW"] = new PositionContribution
        {
            Position = "FTW",
            MidfieldPM = 0.15,
            CentralAttackSc = 0.66,
            CentralAttackPs = 0.23,
            SideAttackWg = 0.64,
            SideAttackPs = 0.21,
            SideAttackSc = 0.51
        },
        ["DF"] = new PositionContribution
        {
            Position = "DF",
            MidfieldPM = 0.35,
            CentralAttackSc = 0.56,
            CentralAttackPs = 0.53,
            SideAttackWg = 0.13,
            SideAttackPs = 0.31,
            SideAttackSc = 0.13
        }
    };

    // Mapowanie slotu w formacji -> dopuszczalne zachowania (behaviours).
    // Klucze zachowań odpowiadają pozycjom w PositionContributions.
    public static readonly Dictionary<string, string[]> SlotBehaviourOptions = new()
    {
        ["GK"] = new[] { "GK" },
        ["RWB"] = new[] { "WBD", "WBN", "WBO", "WBTM" },
        ["LWB"] = new[] { "WBD", "WBN", "WBO", "WBTM" },
        ["RCD"] = new[] { "RCD", "CDO", "CDTW" },
        ["LCD"] = new[] { "LCD", "CDO", "CDTW" },
        ["CD"]  = new[] { "CD", "CDO" },
        ["RW"]  = new[] { "RW", "WO", "WD", "WTM" },
        ["LW"]  = new[] { "LW", "WO", "WD", "WTM" },
        ["RIM"] = new[] { "RIM", "IMO", "IMD", "IMTW" },
        ["LIM"] = new[] { "LIM", "IMO", "IMD", "IMTW" },
        ["IM"]  = new[] { "IM", "IMO", "IMD" },
        ["RFW"] = new[] { "RFW", "FTW", "DF" },
        ["LFW"] = new[] { "LFW", "FTW", "DF" },
        ["CFW"] = new[] { "CFW", "FTW", "DF" },
        ["FW"]  = new[] { "FW", "FTW", "DF" }
    };

    // Informacja, czy slot jest po prawej / lewej / w centrum.
    // Stosowane, by winger/WB kontrybuowali tylko do swojej flanki.
    public static readonly Dictionary<string, string> SlotSide = new()
    {
        ["GK"] = "C",
        ["RWB"] = "R", ["LWB"] = "L",
        ["RCD"] = "C", ["LCD"] = "C", ["CD"] = "C",
        ["RW"] = "R", ["LW"] = "L",
        ["RIM"] = "C", ["LIM"] = "C", ["IM"] = "C",
        ["RFW"] = "R", ["LFW"] = "L", ["CFW"] = "C", ["FW"] = "C"
    };

    // Kary za aglomeracje (wielu graczy na tej samej pozycji centralnej)
    public static readonly Dictionary<int, double> CentralDefenderPenalty = new()
    {
        { 2, 0.964 }, // -3.6%
        { 3, 0.90 }   // -10%
    };

    public static readonly Dictionary<int, double> InnerMidfielderPenalty = new()
    {
        { 2, 0.935 }, // -6.5%
        { 3, 0.825 }  // -17.5%
    };

    public static readonly Dictionary<int, double> ForwardPenalty = new()
    {
        { 2, 0.945 }, // -5.5%
        { 3, 0.865 }  // -13.5%
    };

    // Wpisy formy na wydajno
    public static readonly Dictionary<int, double> FormPerformance = new()
    {
        { 8, 1.00 },   // Excellent
        { 7, 0.967 },  // Solid
        { 6, 0.897 },  // Passable
        { 5, 0.82 },   // Inadequate
        { 4, 0.732 },  // Weak
        { 3, 0.629 },  // Poor
        { 2, 0.50 },   // Wretched
        { 1, 0.305 }   // Disastrous
    };

    // Wspczynniki dla taktyk
    public static class TacticModifiers
    {
        // Counter Attack - 93% midfield, bonus do szans gdy druyna ma mniejszosc w posiadaniu
        public const double CounterAttackMidfieldPenalty = 0.93;
        public const double CounterAttackAttackBonus = 1.08; // boost atakow gdy kontratak zadziala (uproszczenie)

        // PIC (Play It Cool) - 83.945% midfield, ale TS x1.33
        public const double PICMidfieldPenalty = 0.83945;
        public const double PICTeamSpiritBonus = 1.33;

        // MOTS (Match Of The Season) - 111.49% midfield, TS spada 50%
        public const double MOTSMidfieldBonus = 1.1149;
        public const double MOTSTeamSpiritPenalty = 0.50;

        // Home advantage - 119.892% midfield
        public const double HomeAdvantage = 1.19892;

        // Derby (away team) - 111.493% midfield
        public const double DerbyAwayBonus = 1.11493;

        // AIM - Atak rodkiem: +~10% CA, -~5% boczne
        public const double AIMCentralAttackBonus = 1.10;
        public const double AIMSideAttackPenalty = 0.95;

        // AOW - Atak skrzydlami: -~5% CA, +~10% boczne
        public const double AOWCentralAttackPenalty = 0.95;
        public const double AOWSideAttackBonus = 1.10;

        // Pressing: -8% atak (oba), +6% obrona, wymaga kondycji
        public const double PressingAttackPenalty = 0.92;
        public const double PressingDefenseBonus = 1.06;
        public const double PressingMidfieldPenalty = 0.97;

        // Play Creatively: +6% atak, -4% obrona (uproszczenie - takt. SE)
        public const double CreativelyAttackBonus = 1.06;
        public const double CreativelyDefensePenalty = 0.96;

        // Long Shots: -5% midfield, -2.7% attack; szansa na strzaly dystansowe
        public const double LongShotsMidfieldPenalty = 0.95;
        public const double LongShotsAttackPenalty = 0.973;
        public const double LongShotsGoalBonus = 1.05; // bonus do oczekiwanych goli gdy LS level wysoki

        // AOW/AIM - poziom taktyki = (suma poda / 5) - 2
        public static int CalculateAOWAIMLevel(int totalPassing) => (totalPassing / 5) - 2;

        // Long Shots - poziom = 1.66*SC + 0.55*SP - 7.6
        public static double CalculateLongShotsLevel(double avgScoring, double avgSetPieces)
            => 1.66 * avgScoring + 0.55 * avgSetPieces - 7.6;

        // Pressing wymaga wysokiej kondycji (stamina)
        public const int MinStaminaForPressing = 7;
    }

    // Typ trenera
    public static class CoachModifiers
    {
        // Offensive coach: +7.4% attack, -12% defense
        public const double OffensiveAttackBonus = 1.074;
        public const double OffensiveDefensePenalty = 0.88;

        // Defensive coach: +12.8% defense, -11.8% attack
        public const double DefensiveDefenseBonus = 1.128;
        public const double DefensiveAttackPenalty = 0.882;
    }
}

public class FormationDefinition
{
    public string Name { get; set; } = "";
    public int Defenders { get; set; }
    public int Midfielders { get; set; }
    public int Forwards { get; set; }
    public string[] Positions { get; set; } = Array.Empty<string>();
    public FormationStyle Style { get; set; }
    public string Description { get; set; } = "";
}

public enum FormationStyle
{
    UltraDefensive,
    Defensive,
    MidfieldControl,
    Balanced,
    Offensive,
    UltraOffensive
}

public class PositionContribution
{
    public string Position { get; set; } = "";
    
    // Wkad do pomocy (Playmaking)
    public double MidfieldPM { get; set; }
    
    // Wkad do obrony centralnej
    public double CentralDefenseGK { get; set; }
    public double CentralDefenseDef { get; set; }
    
    // Wkad do obrony bocznej
    public double SideDefenseGK { get; set; }
    public double SideDefenseDef { get; set; }
    
    // Wkad do ataku centralnego
    public double CentralAttackSc { get; set; }
    public double CentralAttackPs { get; set; }
    
    // Wkad do ataku bocznego
    public double SideAttackWg { get; set; }
    public double SideAttackPs { get; set; }
    public double SideAttackSc { get; set; }
}
