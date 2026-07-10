using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Czysty silnik ocen sektorowych i przewidywania wyniku — bez I/O, w pelni testowalny.
/// Wejscie: 11 graczy z zachowaniami + kontekst (taktyka, postawa, trener, dom).
/// Wyjscie: 7 ocen sektorowych w skali denominacji HT + prawdopodobienstwa W/R/P.
///
/// Stale oznaczone "kalibrowalne" nalezy dopasowac harnessem kalibracyjnym
/// (/api/calibration/own-matches) na ocenach z matchdetails wlasnych meczow.
/// </summary>
public sealed class RatingEngine
{
    // ======================= Skale i stale kalibrowalne =======================

    /// <summary>
    /// Mapowanie wewnetrznej sumy wkladow na skale denominacji HT (matchdetails):
    /// Rating_HT = K * suma^Gamma + C (power-law per model HO!). Wartosci domyslne
    /// sa przyblizeniem — do dopasowania harnessem.
    /// </summary>
    public static class RatingScale
    {
        public const double K = 1.0;
        public const double Gamma = 0.95;
        public const double C = 1.0;

        public static double ToHtScale(double sum) => K * Math.Pow(Math.Max(0, sum), Gamma) + C;
    }

    /// <summary>
    /// Kalibrowane mnozniki sektorowe: srednie actual/predicted z 5 rozegranych meczow
    /// (GET /api/calibration/own-matches, 2026-07-10, pelne dopasowanie 11 graczy).
    /// Strony L/P usrednione symetrycznie — roznice miedzy nimi to szum probki.
    /// </summary>
    public static class SectorCorrection
    {
        public const double Midfield = 0.46;
        public const double CentralDefense = 0.95;
        public const double SideDefense = 1.35;
        public const double CentralAttack = 0.96;
        public const double SideAttack = 1.31;
    }

    // Wklad doswiadczenia: sektor *= 1 + k * ln(1 + srednie XP). Kalibrowalne.
    private const double XpFactorDefense = 0.035;
    private const double XpFactorMidfield = 0.025;
    private const double XpFactorAttack = 0.030;

    // Nielad formacji: wartosc oczekiwana po [bez nieladu, nielad -> spadek ocen ~15%].
    private const double DisorderRatingDrop = 0.15;

    // ======================= Efektywne umiejetnosci =======================

    /// <summary>
    /// Bonus do POZIOMU umiejetnosci z lojalnosci / klubu macierzystego:
    /// klub macierzysty = +1.5 poziomu, inaczej (lojalnosc-1)/19 (0 przy 1, +1.0 przy 20).
    /// </summary>
    public static double LoyaltySkillBonus(Player p) =>
        p.MotherClubBonus ? 1.5 : Math.Max(0, p.Loyalty - 1) / 19.0;

    /// <summary>Efektywny poziom umiejetnosci (skill + bonus lojalnosci). 0 pozostaje 0.</summary>
    public static double EffSkill(Player p, int raw) =>
        raw <= 0 ? 0 : raw + LoyaltySkillBonus(p);

    public static double FormFactor(int form) =>
        FormationData.FormPerformance.GetValueOrDefault(Math.Clamp(form, 1, 8), 0.9);

    /// <summary>
    /// Wplyw kondycji jako srednia energia w trakcie 90 minut (model energetyczny):
    /// gracz traci energie w tempie malejacym z kondycja. Tabela kalibrowalna.
    /// </summary>
    public static double StaminaEffect(int stamina)
    {
        // indeks = kondycja 1..9 (powyzej 9 — pelna energia)
        double[] avgEnergy = { 0.50, 0.61, 0.70, 0.78, 0.85, 0.91, 0.95, 0.98, 1.00 };
        if (stamina <= 0) return avgEnergy[0];
        if (stamina >= 9) return 1.00;
        return avgEnergy[stamina - 1];
    }

    /// <summary>
    /// Mnoznik wydajnosci gracza: forma i kondycja.
    /// Doswiadczenie dziala na poziomie sektora (logarytmicznie), lojalnosc addytywnie
    /// na poziom umiejetnosci — celowo NIE wchodza do tego mnoznika.
    /// </summary>
    public static double EffectiveMultiplier(Player p) =>
        FormFactor(p.Form) * StaminaEffect(p.Stamina);

    // Kody pogody CHPP (WeatherID): 0=deszcz, 1=pochmurno, 2=czesciowe zachmurzenie, 3=slonce.
    public const int WeatherRain = 0;
    public const int WeatherSunny = 3;
    public const int WeatherUnknown = -1;

    /// <summary>
    /// Wplyw pogody na specjalnosc (model HT): techniczny slonce +5% / deszcz -5%,
    /// silny deszcz +5% / slonce -5%, szybki deszcz i slonce -5%. Inne bez wplywu.
    /// Specialty z CHPP przychodzi jako kod liczbowy (1=Technical, 2=Quick, 3=Powerful).
    /// </summary>
    public static double WeatherMultiplier(Player p, int weatherId)
    {
        if (weatherId != WeatherRain && weatherId != WeatherSunny) return 1.0;
        var spec = p.Specialty;
        bool technical = spec is "1" or "Technical";
        bool quick = spec is "2" or "Quick";
        bool powerful = spec is "3" or "Powerful";

        if (technical) return weatherId == WeatherSunny ? 1.05 : 0.95;
        if (powerful) return weatherId == WeatherRain ? 1.05 : 0.95;
        if (quick) return 0.95; // cierpi i w deszczu, i w sloncu
        return 1.0;
    }

    // ======================= Wklad gracza w sektory =======================

    internal sealed record SectorContribution(
        double Mid, double Cd, double Rd, double Ld, double Ca, double Ra, double La)
    {
        public double Total => Mid + Cd + Rd + Ld + Ca + Ra + La;
    }

    /// <summary>
    /// Wklad gracza na slocie z danym zachowaniem do 7 sektorow (przed karami za stloczenie).
    /// Strona slotu decyduje, dokad ida wklady boczne; sloty czysto centralne (CD/IM/FW)
    /// wnosza wklad boczny W PELNI do obu flank (model HO!), nie po polowie.
    /// </summary>
    internal static SectorContribution ContributionFor(Player p, string slot, PositionContribution c, int weatherId = WeatherUnknown)
    {
        double eff = EffectiveMultiplier(p) * WeatherMultiplier(p, weatherId);

        double mid = c.MidfieldPM * EffSkill(p, p.Skills.Playmaking);
        double cd = c.CentralDefenseDef * EffSkill(p, p.Skills.Defending)
                  + c.CentralDefenseGK * EffSkill(p, p.Skills.Keeper);
        double sd = c.SideDefenseDef * EffSkill(p, p.Skills.Defending)
                  + c.SideDefenseGK * EffSkill(p, p.Skills.Keeper);
        double ca = c.CentralAttackSc * EffSkill(p, p.Skills.Scoring)
                  + c.CentralAttackPs * EffSkill(p, p.Skills.Passing);
        double sa = c.SideAttackSc * EffSkill(p, p.Skills.Scoring)
                  + c.SideAttackPs * EffSkill(p, p.Skills.Passing)
                  + c.SideAttackWg * EffSkill(p, p.Skills.Winger);

        double rd = 0, ld = 0, ra = 0, la = 0;
        var side = FormationData.SlotSide.GetValueOrDefault(slot, "C");
        if (side == "R") { rd = sd; ra = sa; }
        else if (side == "L") { ld = sd; la = sa; }
        else
        {
            rd = sd; ld = sd;
            ra = sa; la = sa;
        }

        return new SectorContribution(
            mid * eff, cd * eff, rd * eff, ld * eff, ca * eff, ra * eff, la * eff);
    }

    // ======================= Oceny druzyny =======================

    internal LineupRatings ComputeRatings(AssignedLineup lineup, int weatherId = WeatherUnknown)
    {
        var r = new LineupRatings();

        int cdCount = 0, imCount = 0, fwCount = 0;
        foreach (var s in lineup.Slots.Values)
        {
            if (IsCentralDefenderSlot(s.SlotId)) cdCount++;
            else if (IsInnerMidfielderSlot(s.SlotId)) imCount++;
            else if (IsForwardSlot(s.SlotId)) fwCount++;
        }
        double penCD = FormationData.CentralDefenderPenalty.GetValueOrDefault(cdCount, 1.0);
        double penIM = FormationData.InnerMidfielderPenalty.GetValueOrDefault(imCount, 1.0);
        double penFW = FormationData.ForwardPenalty.GetValueOrDefault(fwCount, 1.0);

        double xpSum = 0;
        int xpCount = 0;

        foreach (var s in lineup.Slots.Values)
        {
            if (!FormationData.PositionContributions.TryGetValue(s.Behaviour, out var c)) continue;

            double pen = 1.0;
            if (IsCentralDefenderSlot(s.SlotId)) pen = penCD;
            else if (IsInnerMidfielderSlot(s.SlotId)) pen = penIM;
            else if (IsForwardSlot(s.SlotId)) pen = penFW;

            var contrib = ContributionFor(s.Player, s.SlotId, c, weatherId);
            r.Midfield += contrib.Mid * pen;
            r.CentralDefense += contrib.Cd * pen;
            r.RightDefense += contrib.Rd * pen;
            r.LeftDefense += contrib.Ld * pen;
            r.CentralAttack += contrib.Ca * pen;
            r.RightAttack += contrib.Ra * pen;
            r.LeftAttack += contrib.La * pen;

            xpSum += s.Player.Experience;
            xpCount++;
        }

        // Doswiadczenie druzyny podnosi sektory logarytmicznie.
        double avgXp = xpCount > 0 ? xpSum / xpCount : 0;
        double lnXp = Math.Log(1 + Math.Max(0, avgXp));
        r.Midfield *= 1 + XpFactorMidfield * lnXp;
        r.CentralDefense *= 1 + XpFactorDefense * lnXp;
        r.RightDefense *= 1 + XpFactorDefense * lnXp;
        r.LeftDefense *= 1 + XpFactorDefense * lnXp;
        r.CentralAttack *= 1 + XpFactorAttack * lnXp;
        r.RightAttack *= 1 + XpFactorAttack * lnXp;
        r.LeftAttack *= 1 + XpFactorAttack * lnXp;

        // Mapowanie na skale denominacji HT — od tego momentu oceny sa porownywalne
        // z ocenami przeciwnika z matchdetails.
        r.Midfield = RatingScale.ToHtScale(r.Midfield) * SectorCorrection.Midfield;
        r.CentralDefense = RatingScale.ToHtScale(r.CentralDefense) * SectorCorrection.CentralDefense;
        r.RightDefense = RatingScale.ToHtScale(r.RightDefense) * SectorCorrection.SideDefense;
        r.LeftDefense = RatingScale.ToHtScale(r.LeftDefense) * SectorCorrection.SideDefense;
        r.CentralAttack = RatingScale.ToHtScale(r.CentralAttack) * SectorCorrection.CentralAttack;
        r.RightAttack = RatingScale.ToHtScale(r.RightAttack) * SectorCorrection.SideAttack;
        r.LeftAttack = RatingScale.ToHtScale(r.LeftAttack) * SectorCorrection.SideAttack;

        RecomputeOverall(r);
        return r;
    }

    // ======================= Modyfikatory kontekstu =======================

    public void ApplyTactic(LineupRatings r, string tactic)
    {
        switch (tactic)
        {
            case "Counter":
                r.Midfield *= FormationData.TacticModifiers.CounterAttackMidfieldPenalty;
                break;
            case "AttackInMiddle":
                r.CentralAttack *= FormationData.TacticModifiers.AIMCentralAttackBonus;
                r.RightAttack *= FormationData.TacticModifiers.AIMSideAttackPenalty;
                r.LeftAttack *= FormationData.TacticModifiers.AIMSideAttackPenalty;
                break;
            case "AttackOnWings":
                r.CentralAttack *= FormationData.TacticModifiers.AOWCentralAttackPenalty;
                r.RightAttack *= FormationData.TacticModifiers.AOWSideAttackBonus;
                r.LeftAttack *= FormationData.TacticModifiers.AOWSideAttackBonus;
                break;
            case "Pressing":
                // Pressing NIE zmienia ocen — tlumi szanse obu druzyn (model meczu).
                break;
            case "PlayCreatively":
                r.CentralAttack *= FormationData.TacticModifiers.CreativelyAttackBonus;
                r.RightAttack *= FormationData.TacticModifiers.CreativelyAttackBonus;
                r.LeftAttack *= FormationData.TacticModifiers.CreativelyAttackBonus;
                r.CentralDefense *= FormationData.TacticModifiers.CreativelyDefensePenalty;
                r.RightDefense *= FormationData.TacticModifiers.CreativelyDefensePenalty;
                r.LeftDefense *= FormationData.TacticModifiers.CreativelyDefensePenalty;
                break;
            case "LongShots":
                r.Midfield *= FormationData.TacticModifiers.LongShotsMidfieldPenalty;
                r.CentralAttack *= FormationData.TacticModifiers.LongShotsAttackPenalty;
                r.RightAttack *= FormationData.TacticModifiers.LongShotsAttackPenalty;
                r.LeftAttack *= FormationData.TacticModifiers.LongShotsAttackPenalty;
                break;
        }
        RecomputeOverall(r);
    }

    public void ApplyAttitude(LineupRatings r, string attitude)
    {
        double mult = attitude switch
        {
            "PIC" => FormationData.TacticModifiers.PICMidfieldPenalty,
            "MOTS" => FormationData.TacticModifiers.MOTSMidfieldBonus,
            _ => 1.0
        };
        r.Midfield *= mult;
        RecomputeOverall(r);
    }

    public void ApplyCoach(LineupRatings r, string coach)
    {
        if (coach == "Offensive")
        {
            r.CentralAttack *= FormationData.CoachModifiers.OffensiveAttackBonus;
            r.RightAttack *= FormationData.CoachModifiers.OffensiveAttackBonus;
            r.LeftAttack *= FormationData.CoachModifiers.OffensiveAttackBonus;
            r.CentralDefense *= FormationData.CoachModifiers.OffensiveDefensePenalty;
            r.RightDefense *= FormationData.CoachModifiers.OffensiveDefensePenalty;
            r.LeftDefense *= FormationData.CoachModifiers.OffensiveDefensePenalty;
        }
        else if (coach == "Defensive")
        {
            r.CentralDefense *= FormationData.CoachModifiers.DefensiveDefenseBonus;
            r.RightDefense *= FormationData.CoachModifiers.DefensiveDefenseBonus;
            r.LeftDefense *= FormationData.CoachModifiers.DefensiveDefenseBonus;
            r.CentralAttack *= FormationData.CoachModifiers.DefensiveAttackPenalty;
            r.RightAttack *= FormationData.CoachModifiers.DefensiveAttackPenalty;
            r.LeftAttack *= FormationData.CoachModifiers.DefensiveAttackPenalty;
        }
        RecomputeOverall(r);
    }

    /// <summary>Atut wlasnego boiska: srodek pola x1.19892 (derby na wyjezdzie pomijamy — brak danych).</summary>
    public void ApplyHomeAdvantage(LineupRatings r, bool isHomeMatch)
    {
        if (!isHomeMatch) return;
        r.Midfield *= FormationData.TacticModifiers.HomeAdvantage;
        RecomputeOverall(r);
    }

    /// <summary>
    /// Nielad formacji jako wartosc oczekiwana: E = (1-risk)*pelne + risk*(pelne*(1-spadek)).
    /// </summary>
    public void ApplyDisorder(LineupRatings r, double disorderRisk)
    {
        if (disorderRisk <= 0) return;
        double ev = 1.0 - DisorderRatingDrop * Math.Clamp(disorderRisk, 0, 1);
        r.Midfield *= ev;
        r.CentralDefense *= ev;
        r.RightDefense *= ev;
        r.LeftDefense *= ev;
        r.CentralAttack *= ev;
        r.RightAttack *= ev;
        r.LeftAttack *= ev;
        RecomputeOverall(r);
    }

    private static void RecomputeOverall(LineupRatings r) =>
        r.Overall = (r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense +
                     r.CentralAttack + r.RightAttack + r.LeftAttack) / 7.0;

    // ======================= Stale fragmenty gry (posrednie) =======================

    /// <summary>
    /// Oceny posrednich stalych fragmentow (ISP) w skali HT.
    /// Atak: wykonawca (najlepsze SP) + SP i skutecznosc reszty.
    /// Obrona: bramkarz + obrona i SP druzyny. Wspolczynniki kalibrowalne.
    /// </summary>
    internal (double Att, double Def) ComputeIndirectSetPieces(AssignedLineup lineup)
    {
        var players = lineup.Slots.Values.Select(s => s.Player).ToList();
        if (players.Count == 0) return (0, 0);

        double takerSp = players.Max(p => EffSkill(p, p.Skills.SetPieces));
        double avgSp = players.Average(p => EffSkill(p, p.Skills.SetPieces));
        double avgSc = players.Average(p => EffSkill(p, p.Skills.Scoring));
        double avgDef = players.Average(p => EffSkill(p, p.Skills.Defending));

        var gk = lineup.Slots.TryGetValue("GK", out var gkSlot) ? gkSlot.Player : null;
        double gkSkill = gk != null ? EffSkill(gk, gk.Skills.Keeper) : 0;

        double att = 0.5 * takerSp + 0.3 * avgSp + 0.2 * avgSc;
        double def = 0.4 * gkSkill + 0.35 * avgDef + 0.25 * avgSp;

        // Specjalnosc "glowkujacy" wzmacnia posrednie SFG w ataku.
        int headers = players.Count(p => string.Equals(p.Specialty, "Head", StringComparison.OrdinalIgnoreCase)
                                          || p.Specialty == "4");
        att *= 1 + 0.03 * headers;

        // Skala wkladow jest per-gracz (nie suma 11) — przeskaluj do denominacji HT.
        return (RatingScale.ToHtScale(att * 3.0), RatingScale.ToHtScale(def * 3.0));
    }

    // ======================= Model meczu (Poisson) =======================

    internal sealed record MatchPrediction(
        double WinProbability, double DrawProbability, double LossProbability,
        double ExpectedGoalsFor, double ExpectedGoalsAgainst);

    /// <summary>
    /// Przewidywanie wyniku. Oceny `me` i `opp` musza byc w tej samej skali (HT).
    /// Taktyki dzialaja na model szans: pressing tlumi obie druzyny, kontra generuje
    /// dodatkowe szanse przy mniejszosci posiadania, strzaly z dystansu konwertuja
    /// czesc normalnych szans, SFG (15% akcji) liczone z ocen ISP.
    /// </summary>
    internal MatchPrediction PredictOutcome(
        LineupRatings me, LineupRatings opp, string tactic, AssignedLineup lineup,
        double myIspAtt, double myIspDef, double oppIspAtt, double oppIspDef)
    {
        double midMe = Math.Max(0.01, me.Midfield);
        double midOpp = Math.Max(0.01, opp.Midfield);
        const double midExp = 2.75;
        double midMePow = Math.Pow(midMe, midExp);
        double midOppPow = Math.Pow(midOpp, midExp);
        double myActionShare = midMePow / (midMePow + midOppPow);
        double actionsMe = 10.0 * myActionShare;
        double actionsOpp = 10.0 * (1.0 - myActionShare);

        const double finExp = 3.5;
        static double FinProb(double att, double def)
        {
            double a2 = Math.Pow(Math.Max(att, 0.01), finExp);
            double d2 = Math.Pow(Math.Max(def, 0.01), finExp);
            return a2 / (a2 + d2);
        }

        // Brak danych ISP przeciwnika -> przybliz z jego obrony/ataku centralnego.
        if (oppIspDef <= 0) oppIspDef = 0.9 * opp.CentralDefense;
        if (oppIspAtt <= 0) oppIspAtt = 0.8 * opp.CentralAttack;
        if (myIspDef <= 0) myIspDef = 0.9 * me.CentralDefense;
        if (myIspAtt <= 0) myIspAtt = 0.8 * me.CentralAttack;

        // Rozklad akcji: 35% srodek, 25% prawa, 25% lewa, 15% SFG.
        double pCentralMe = FinProb(me.CentralAttack, opp.CentralDefense);
        double pRightMe = FinProb(me.RightAttack, opp.LeftDefense);
        double pLeftMe = FinProb(me.LeftAttack, opp.RightDefense);
        double pSfgMe = FinProb(myIspAtt, oppIspDef);
        double pGoalMe = 0.35 * pCentralMe + 0.25 * pRightMe + 0.25 * pLeftMe + 0.15 * pSfgMe;

        double pCentralOpp = FinProb(opp.CentralAttack, me.CentralDefense);
        double pRightOpp = FinProb(opp.RightAttack, me.LeftDefense);
        double pLeftOpp = FinProb(opp.LeftAttack, me.RightDefense);
        double pSfgOpp = FinProb(oppIspAtt, myIspDef);
        double pGoalOpp = 0.35 * pCentralOpp + 0.25 * pRightOpp + 0.25 * pLeftOpp + 0.15 * pSfgOpp;

        // Strzaly z dystansu: czesc normalnych szans (30%) zamienia sie w proby LS,
        // ktorych skutecznosc zalezy od poziomu LS druzyny.
        if (tactic == "LongShots")
        {
            var outfield = lineup.Slots.Values.Where(s => s.SlotId != "GK").Select(s => s.Player).ToList();
            double avgSc = outfield.Count > 0 ? outfield.Average(p => EffSkill(p, p.Skills.Scoring)) : 0;
            double avgSp = outfield.Count > 0 ? outfield.Average(p => EffSkill(p, p.Skills.SetPieces)) : 0;
            double lsLevel = FormationData.TacticModifiers.CalculateLongShotsLevel(avgSc, avgSp);
            double pLs = Math.Clamp(lsLevel / 25.0, 0.05, 0.45);
            pGoalMe = 0.7 * pGoalMe + 0.3 * pLs;
        }

        double lamMe = actionsMe * pGoalMe;
        double lamOpp = actionsOpp * pGoalOpp;

        // Kontra: dodatkowe szanse z przechwytow, tylko przy mniejszosci posiadania.
        // Poziom kontry = (2*podania + obrona) obroncow / 3.
        if (tactic == "Counter" && myActionShare < 0.5)
        {
            var defenders = lineup.Slots.Values
                .Where(s => IsDefenderSlot(s.SlotId))
                .Select(s => s.Player)
                .ToList();
            if (defenders.Count > 0)
            {
                double counterLevel = defenders.Average(p =>
                    (2 * EffSkill(p, p.Skills.Passing) + EffSkill(p, p.Skills.Defending)) / 3.0);
                double conversion = Math.Clamp(counterLevel / 35.0, 0.05, 0.35);
                lamMe += actionsOpp * conversion * pGoalMe;
            }
        }

        // Pressing: tlumi szanse OBU druzyn; sila zalezy od obrony i kondycji.
        if (tactic == "Pressing")
        {
            var outfield = lineup.Slots.Values.Where(s => s.SlotId != "GK").Select(s => s.Player).ToList();
            double pressLevel = outfield.Count > 0
                ? outfield.Average(p => EffSkill(p, p.Skills.Defending) * StaminaEffect(p.Stamina))
                : 0;
            double suppression = Math.Clamp(0.08 + pressLevel * 0.012, 0.10, 0.30);
            lamMe *= 1 - suppression;
            lamOpp *= 1 - suppression;
        }

        lamMe = Math.Clamp(lamMe, 0.05, 8.0);
        lamOpp = Math.Clamp(lamOpp, 0.05, 8.0);

        const int GoalCap = 10;
        double pWin = 0, pDraw = 0, pLoss = 0;
        for (int i = 0; i <= GoalCap; i++)
        {
            double pi = Poisson(lamMe, i);
            for (int j = 0; j <= GoalCap; j++)
            {
                double pj = Poisson(lamOpp, j);
                double p = pi * pj;
                if (i > j) pWin += p;
                else if (i == j) pDraw += p;
                else pLoss += p;
            }
        }
        return new MatchPrediction(pWin, pDraw, pLoss, lamMe, lamOpp);
    }

    // ======================= Pomocnicze =======================

    internal static bool IsCentralDefenderSlot(string slot) =>
        slot == "CD" || slot == "RCD" || slot == "LCD";

    internal static bool IsInnerMidfielderSlot(string slot) =>
        slot == "IM" || slot == "RIM" || slot == "LIM";

    internal static bool IsForwardSlot(string slot) =>
        slot == "FW" || slot == "RFW" || slot == "LFW" || slot == "CFW";

    internal static bool IsDefenderSlot(string slot) =>
        slot is "CD" or "RCD" or "LCD" or "RWB" or "LWB";

    private static double Poisson(double lambda, int k)
    {
        if (lambda <= 0) return k == 0 ? 1.0 : 0.0;
        double log = -lambda + k * Math.Log(lambda) - LogFactorial(k);
        return Math.Exp(log);
    }

    private static readonly double[] LogFactCache = BuildLogFactCache(21);
    private static double[] BuildLogFactCache(int n)
    {
        var a = new double[n];
        a[0] = 0;
        for (int i = 1; i < n; i++) a[i] = a[i - 1] + Math.Log(i);
        return a;
    }
    private static double LogFactorial(int n) =>
        n < LogFactCache.Length ? LogFactCache[n] : LogFactCache[^1] + Math.Log(LogFactCache.Length);
}
