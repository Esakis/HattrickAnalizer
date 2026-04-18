using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

public class LineupOptimizerService
{
    private readonly HattrickApiService _hattrickApi;
    private readonly AdvancedLineupOptimizer _advancedOptimizer;

    public LineupOptimizerService(HattrickApiService hattrickApi)
    {
        _hattrickApi = hattrickApi;
        _advancedOptimizer = new AdvancedLineupOptimizer(hattrickApi);
    }

    public async Task<OptimizerResponse> OptimizeLineupAsync(OptimizerRequest request)
    {
        // Uyj zaawansowanego optymalizatora opartego na wiedzy z poradników
        return await _advancedOptimizer.OptimizeLineupAsync(request);
    }

    private Lineup GenerateOptimalLineup(List<Player> players, TeamRatings opponentRatings, string tactic)
    {
        var availablePlayers = players.Where(p => p.InjuryLevel <= 0).ToList();
        var lineup = new Lineup { TacticType = tactic };

        var keeper = availablePlayers.OrderByDescending(p => p.Skills.Keeper).First();
        lineup.Positions["GK"] = new LineupPosition { Position = "GK", Player = keeper };
        availablePlayers.Remove(keeper);

        var defenders = availablePlayers
            .OrderByDescending(p => p.Skills.Defending)
            .Take(4)
            .ToList();

        lineup.Positions["RWB"] = new LineupPosition { Position = "RWB", Player = defenders[0] };
        lineup.Positions["RCD"] = new LineupPosition { Position = "RCD", Player = defenders[1] };
        lineup.Positions["LCD"] = new LineupPosition { Position = "LCD", Player = defenders[2] };
        lineup.Positions["LWB"] = new LineupPosition { Position = "LWB", Player = defenders[3] };
        
        foreach (var def in defenders) availablePlayers.Remove(def);

        var midfielders = availablePlayers
            .OrderByDescending(p => p.Skills.Playmaking + p.Skills.Passing)
            .Take(3)
            .ToList();

        lineup.Positions["RW"] = new LineupPosition { Position = "RW", Player = midfielders[0] };
        lineup.Positions["CM"] = new LineupPosition { Position = "CM", Player = midfielders[1] };
        lineup.Positions["LW"] = new LineupPosition { Position = "LW", Player = midfielders[2] };
        
        foreach (var mid in midfielders) availablePlayers.Remove(mid);

        var forwards = availablePlayers
            .OrderByDescending(p => p.Skills.Scoring)
            .Take(3)
            .ToList();

        lineup.Positions["RFW"] = new LineupPosition { Position = "RFW", Player = forwards[0] };
        lineup.Positions["CFW"] = new LineupPosition { Position = "CFW", Player = forwards[1] };
        lineup.Positions["LFW"] = new LineupPosition { Position = "LFW", Player = forwards[2] };

        lineup.PredictedRatings = CalculateLineupRatings(lineup);

        return lineup;
    }

    private LineupRatings CalculateLineupRatings(Lineup lineup)
    {
        var ratings = new LineupRatings();

        if (lineup.Positions.TryGetValue("CM", out var cm) && cm.Player != null)
        {
            ratings.Midfield = cm.Player.Skills.Playmaking * 4 + cm.Player.Form;
        }

        if (lineup.Positions.TryGetValue("RW", out var rw) && rw.Player != null)
        {
            ratings.Midfield += rw.Player.Skills.Playmaking * 2;
        }

        if (lineup.Positions.TryGetValue("LW", out var lw) && lw.Player != null)
        {
            ratings.Midfield += lw.Player.Skills.Playmaking * 2;
        }

        if (lineup.Positions.TryGetValue("RCD", out var rcd) && rcd.Player != null)
        {
            ratings.CentralDefense = rcd.Player.Skills.Defending * 4;
        }

        if (lineup.Positions.TryGetValue("LCD", out var lcd) && lcd.Player != null)
        {
            ratings.CentralDefense += lcd.Player.Skills.Defending * 4;
        }

        if (lineup.Positions.TryGetValue("RWB", out var rwb) && rwb.Player != null)
        {
            ratings.RightDefense = rwb.Player.Skills.Defending * 4;
        }

        if (lineup.Positions.TryGetValue("LWB", out var lwb) && lwb.Player != null)
        {
            ratings.LeftDefense = lwb.Player.Skills.Defending * 4;
        }

        if (lineup.Positions.TryGetValue("RFW", out var rfw) && rfw.Player != null)
        {
            ratings.RightAttack = rfw.Player.Skills.Scoring * 4;
        }

        if (lineup.Positions.TryGetValue("CFW", out var cfw) && cfw.Player != null)
        {
            ratings.CentralAttack = cfw.Player.Skills.Scoring * 4;
        }

        if (lineup.Positions.TryGetValue("LFW", out var lfw) && lfw.Player != null)
        {
            ratings.LeftAttack = lfw.Player.Skills.Scoring * 4;
        }

        ratings.Overall = (ratings.Midfield + ratings.CentralDefense + ratings.RightDefense + 
                          ratings.LeftDefense + ratings.CentralAttack + ratings.RightAttack + 
                          ratings.LeftAttack) / 7;

        return ratings;
    }

    private LineupRatings ConvertToLineupRatings(TeamRatings teamRatings)
    {
        return new LineupRatings
        {
            Midfield = teamRatings.MidfieldRating,
            RightDefense = teamRatings.RightDefenseRating,
            CentralDefense = teamRatings.CentralDefenseRating,
            LeftDefense = teamRatings.LeftDefenseRating,
            RightAttack = teamRatings.RightAttackRating,
            CentralAttack = teamRatings.CentralAttackRating,
            LeftAttack = teamRatings.LeftAttackRating,
            Overall = (teamRatings.MidfieldRating + teamRatings.CentralDefenseRating + 
                      teamRatings.RightDefenseRating + teamRatings.LeftDefenseRating + 
                      teamRatings.CentralAttackRating + teamRatings.RightAttackRating + 
                      teamRatings.LeftAttackRating) / 7.0
        };
    }

    private List<string> IdentifyStrengths(LineupRatings myRatings, LineupRatings opponentRatings)
    {
        var strengths = new List<string>();

        if (myRatings.Midfield > opponentRatings.Midfield * 1.2)
            strengths.Add("Dominacja w środku pola - kontroluj tempo gry / Midfield dominance - control the pace of the game");

        if (myRatings.CentralDefense > opponentRatings.CentralAttack * 1.15)
            strengths.Add("Silna obrona centralna - przeciwnik będzie miał trudności ze strzelaniem / Strong central defense - opponent will struggle to score through the middle");

        bool rightAdv = myRatings.RightAttack > opponentRatings.LeftDefense * 1.15;
        bool leftAdv  = myRatings.LeftAttack  > opponentRatings.RightDefense * 1.15;
        if (rightAdv && leftAdv)
            strengths.Add("Przewaga na obu skrzydłach — rozważ taktykę Atak skrzydłami (AOW), która wzmacnia oba skrzydła jednocześnie / Both wings advantage — consider Attack on Wings (AOW), which boosts both wings simultaneously");
        else if (rightAdv)
            strengths.Add("Przewaga na prawej flance — AOW wzmacnia oba skrzydła jednocześnie, wzmocnij lewe skrzydło by w pełni wykorzystać tę przewagę / Right flank advantage — AOW boosts both wings simultaneously, strengthen the left wing to fully exploit this edge");
        else if (leftAdv)
            strengths.Add("Przewaga na lewej flance — AOW wzmacnia oba skrzydła jednocześnie, wzmocnij prawe skrzydło by w pełni wykorzystać tę przewagę / Left flank advantage — AOW boosts both wings simultaneously, strengthen the right wing to fully exploit this edge");

        return strengths;
    }

    private List<string> IdentifyWeaknesses(LineupRatings myRatings, LineupRatings opponentRatings)
    {
        var weaknesses = new List<string>();

        if (myRatings.Midfield < opponentRatings.Midfield * 0.85)
            weaknesses.Add("Słabość w środku pola - rozważ defensywną taktykę / Weak midfield - consider a defensive tactic");

        if (myRatings.CentralDefense < opponentRatings.CentralAttack * 0.9)
            weaknesses.Add("Obrona centralna może mieć problemy - wzmocnij środek / Central defense may struggle - reinforce the middle");

        if (myRatings.RightDefense < opponentRatings.LeftAttack * 0.9)
            weaknesses.Add("Słaba prawa obrona - przeciwnik może to wykorzystać / Weak right defense - opponent may exploit it");

        if (myRatings.LeftDefense < opponentRatings.RightAttack * 0.9)
            weaknesses.Add("Słaba lewa obrona - przeciwnik może to wykorzystać / Weak left defense - opponent may exploit it");

        return weaknesses;
    }

    private List<string> GenerateRecommendations(TeamComparison comparison, Lineup lineup)
    {
        var recommendations = new List<string>();

        if (comparison.Strengths.Count > comparison.Weaknesses.Count)
        {
            recommendations.Add("Graj ofensywnie - masz przewagę nad przeciwnikiem / Play offensively - you have the advantage");
            recommendations.Add("Rozważ taktykę 'Atak środkiem' (AIM) lub 'Atak skrzydłami' (AOW — wzmacnia oba skrzydła jednocześnie) / Consider 'Attack in the Middle' (AIM) or 'Attack on Wings' (AOW — boosts both wings simultaneously)");
        }
        else if (comparison.Weaknesses.Count > comparison.Strengths.Count)
        {
            recommendations.Add("Graj defensywnie - przeciwnik jest silniejszy / Play defensively - opponent is stronger");
            recommendations.Add("Rozważ taktykę 'Kontratak' lub 'Pressing' / Consider 'Counter-attack' or 'Pressing' tactic");
        }
        else
        {
            recommendations.Add("Graj normalnie - siły są wyrównane / Play normally - forces are balanced");
        }

        if (comparison.MyTeamRatings.Midfield > comparison.OpponentRatings.Midfield)
        {
            recommendations.Add("Postaw na rozgrywanie - masz przewagę w pomocy / Focus on possession - you have a midfield advantage");
        }

        var bestAttack = Math.Max(Math.Max(comparison.MyTeamRatings.LeftAttack,
                                           comparison.MyTeamRatings.RightAttack),
                                           comparison.MyTeamRatings.CentralAttack);

        if (bestAttack == comparison.MyTeamRatings.CentralAttack)
            recommendations.Add("Twój najsilniejszy atak to środek — rozważ taktykę Atak środkiem (AIM) / Your strongest attack is through the center — consider Attack in the Middle (AIM)");
        else
            recommendations.Add("Twój atak skrzydłami jest mocny — rozważ taktykę Atak skrzydłami (AOW), która wzmacnia oba skrzydła jednocześnie / Your wing attack is strong — consider Attack on Wings (AOW), which boosts both wings simultaneously");

        return recommendations;
    }
}
