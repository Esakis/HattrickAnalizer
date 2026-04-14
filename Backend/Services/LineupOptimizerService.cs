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
        var availablePlayers = players.Where(p => p.InjuryLevel == 0).ToList();
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
            strengths.Add("Dominacja w środku pola - kontroluj tempo gry");

        if (myRatings.CentralDefense > opponentRatings.CentralAttack * 1.15)
            strengths.Add("Silna obrona centralna - przeciwnik będzie miał trudności ze strzelaniem");

        if (myRatings.RightAttack > opponentRatings.LeftDefense * 1.15)
            strengths.Add("Przewaga na prawej flance w ataku - wykorzystaj to skrzydło");

        if (myRatings.LeftAttack > opponentRatings.RightDefense * 1.15)
            strengths.Add("Przewaga na lewej flance w ataku - wykorzystaj to skrzydło");

        return strengths;
    }

    private List<string> IdentifyWeaknesses(LineupRatings myRatings, LineupRatings opponentRatings)
    {
        var weaknesses = new List<string>();

        if (myRatings.Midfield < opponentRatings.Midfield * 0.85)
            weaknesses.Add("Słabość w środku pola - rozważ defensywną taktykę");

        if (myRatings.CentralDefense < opponentRatings.CentralAttack * 0.9)
            weaknesses.Add("Obrona centralna może mieć problemy - wzmocnij środek");

        if (myRatings.RightDefense < opponentRatings.LeftAttack * 0.9)
            weaknesses.Add("Słaba prawa obrona - przeciwnik może to wykorzystać");

        if (myRatings.LeftDefense < opponentRatings.RightAttack * 0.9)
            weaknesses.Add("Słaba lewa obrona - przeciwnik może to wykorzystać");

        return weaknesses;
    }

    private List<string> GenerateRecommendations(TeamComparison comparison, Lineup lineup)
    {
        var recommendations = new List<string>();

        if (comparison.Strengths.Count > comparison.Weaknesses.Count)
        {
            recommendations.Add("Graj ofensywnie - masz przewagę nad przeciwnikiem");
            recommendations.Add("Rozważ taktykę 'Atak środkiem' lub 'Atak skrzydłami'");
        }
        else if (comparison.Weaknesses.Count > comparison.Strengths.Count)
        {
            recommendations.Add("Graj defensywnie - przeciwnik jest silniejszy");
            recommendations.Add("Rozważ taktykę 'Kontratak' lub 'Gra defensywna'");
        }
        else
        {
            recommendations.Add("Graj normalnie - siły są wyrównane");
        }

        if (comparison.MyTeamRatings.Midfield > comparison.OpponentRatings.Midfield)
        {
            recommendations.Add("Postaw na rozgrywanie - masz przewagę w pomocy");
        }

        var bestAttack = Math.Max(Math.Max(comparison.MyTeamRatings.LeftAttack, 
                                           comparison.MyTeamRatings.RightAttack), 
                                           comparison.MyTeamRatings.CentralAttack);

        if (bestAttack == comparison.MyTeamRatings.LeftAttack)
            recommendations.Add("Atakuj lewą stroną - to twoja najmocniejsza flanka");
        else if (bestAttack == comparison.MyTeamRatings.RightAttack)
            recommendations.Add("Atakuj prawą stroną - to twoja najmocniejsza flanka");
        else
            recommendations.Add("Atakuj środkiem - to twoja najmocniejsza strefa");

        return recommendations;
    }
}
