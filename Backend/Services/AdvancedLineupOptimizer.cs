using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Zaawansowany optymalizator oparty na wiedzy z poradników Hattrick
/// </summary>
public class AdvancedLineupOptimizer
{
    private readonly HattrickApiService _hattrickApi;
    
    public AdvancedLineupOptimizer(HattrickApiService hattrickApi)
    {
        _hattrickApi = hattrickApi;
    }

    public async Task<OptimizerResponse> OptimizeLineupAsync(OptimizerRequest request)
    {
        var myTeam = await _hattrickApi.GetTeamDetailsAsync(request.MyTeamId);
        var opponentRatings = await _hattrickApi.GetOpponentRatingsAsync(request.OpponentTeamId, 0);

        // Analiza si przeciwnika i wybór optymalnej formacji
        var formationAnalysis = AnalyzeOpponentAndSelectFormation(opponentRatings, request.PreferredTactic);
        
        // Wybór zawodników na podstawie kontrybucji pozycji
        var lineup = GenerateOptimalLineup(myTeam.Players, formationAnalysis, request.PreferredTactic);
        
        // Obliczenie ratingów
        var myRatings = CalculateLineupRatings(lineup);
        var opponentLineupRatings = ConvertToLineupRatings(opponentRatings);

        // Zastosowanie modyfikatorów taktycznych
        ApplyTacticModifiers(ref myRatings, request.PreferredTactic, formationAnalysis);

        var comparison = new TeamComparison
        {
            MyTeamRatings = myRatings,
            OpponentRatings = opponentLineupRatings,
            Strengths = IdentifyStrengths(myRatings, opponentLineupRatings),
            Weaknesses = IdentifyWeaknesses(myRatings, opponentLineupRatings)
        };

        var recommendations = GenerateRecommendations(comparison, lineup, formationAnalysis);

        return new OptimizerResponse
        {
            OptimalLineup = lineup,
            Recommendations = recommendations,
            Comparison = comparison
        };
    }

    private FormationAnalysis AnalyzeOpponentAndSelectFormation(TeamRatings opponentRatings, string preferredTactic)
    {
        var opponentStrength = AnalyzeOpponentStrength(opponentRatings);
        var recommendedFormation = SelectOptimalFormation(opponentStrength, preferredTactic);
        
        return new FormationAnalysis
        {
            SelectedFormation = recommendedFormation,
            OpponentStrength = opponentStrength,
            RecommendedTactic = SelectOptimalTactic(opponentStrength, preferredTactic, recommendedFormation),
            FormationStyle = recommendedFormation.Style
        };
    }

    private OpponentStrength AnalyzeOpponentStrength(TeamRatings ratings)
    {
        var totalRating = ratings.MidfieldRating + ratings.CentralDefenseRating + ratings.RightDefenseRating + 
                         ratings.LeftDefenseRating + ratings.CentralAttackRating + ratings.RightAttackRating + ratings.LeftAttackRating;
        
        var avgRating = totalRating / 7.0;

        return new OpponentStrength
        {
            Midfield = ratings.MidfieldRating,
            CentralDefense = ratings.CentralDefenseRating,
            SideDefense = (ratings.RightDefenseRating + ratings.LeftDefenseRating) / 2.0,
            CentralAttack = ratings.CentralAttackRating,
            SideAttack = (ratings.RightAttackRating + ratings.LeftAttackRating) / 2.0,
            Overall = avgRating,
            WeakSide = ratings.RightDefenseRating < ratings.LeftDefenseRating ? "right" : "left",
            StrongSide = ratings.RightDefenseRating > ratings.LeftDefenseRating ? "right" : "left"
        };
    }

    private FormationDefinition SelectOptimalFormation(OpponentStrength opponentStrength, string preferredTactic)
    {
        var formations = FormationData.Formations.Values.ToList();

        // Ocena formacji na podstawie siy przeciwnika
        var scoredFormations = formations.Select(f => new
        {
            Formation = f,
            Score = EvaluateFormationAgainstOpponent(f, opponentStrength, preferredTactic)
        })
        .OrderByDescending(x => x.Score)
        .ToList();

        return scoredFormations.First().Formation;
    }

    private double EvaluateFormationAgainstOpponent(FormationDefinition formation, OpponentStrength opponentStrength, string preferredTactic)
    {
        double score = 0;

        // Podstawowe punkty za styl formacji vs siy przeciwnika
        switch (formation.Style)
        {
            case FormationStyle.UltraDefensive when opponentStrength.Overall > 70:
                score += 20;
                break;
            case FormationStyle.Defensive when opponentStrength.Overall > 60:
                score += 15;
                break;
            case FormationStyle.MidfieldControl when opponentStrength.Midfield < 50:
                score += 20;
                break;
            case FormationStyle.Balanced:
                score += 10;
                break;
            case FormationStyle.Offensive when opponentStrength.Overall < 50:
                score += 15;
                break;
            case FormationStyle.UltraOffensive when opponentStrength.Overall < 40:
                score += 20;
                break;
        }

        // Premia za formacje z odpowiednimi liczbami graczy na pozycjach
        if (opponentStrength.CentralDefense > 60 && formation.Defenders >= 5)
            score += 10;
        
        if (opponentStrength.Midfield > 50 && formation.Midfielders >= 5)
            score += 10;
        
        if (opponentStrength.CentralAttack < 40 && formation.Forwards <= 2)
            score += 5;

        // Premia za preferowan taktyk
        if (preferredTactic == "Counter" && formation.Style <= FormationStyle.Defensive)
            score += 10;
        
        if (preferredTactic == "Normal" && formation.Style == FormationStyle.Balanced)
            score += 5;

        return score;
    }

    private string SelectOptimalTactic(OpponentStrength opponentStrength, string preferredTactic, FormationDefinition formation)
    {
        // Logika wyboru taktyki na podstawie siy przeciwnika i formacji
        if (opponentStrength.Overall > 70 && formation.Style <= FormationStyle.Defensive)
        {
            return "Counter"; // Kontratak przeciwko silnemu przeciwnikowi
        }
        
        if (opponentStrength.Midfield < 40 && formation.Midfielders >= 5)
        {
            return "Normal"; // Dominacja w pomocy
        }
        
        if (opponentStrength.Overall < 50 && formation.Style >= FormationStyle.Offensive)
        {
            return "Normal"; // Atak przeciwko slabemu przeciwnikowi
        }

        return preferredTactic;
    }

    private Lineup GenerateOptimalLineup(List<Player> players, FormationAnalysis analysis, string tactic)
    {
        var availablePlayers = players.Where(p => p.InjuryLevel == 0).ToList();
        var formation = analysis.SelectedFormation;
        var lineup = new Lineup 
        { 
            TacticType = tactic,
            Formation = formation.Name
        };

        // Wybór zawodników na podstawie kontrybucji pozycji
        foreach (var position in formation.Positions)
        {
            var bestPlayer = SelectBestPlayerForPosition(availablePlayers, position, analysis);
            if (bestPlayer != null)
            {
                lineup.Positions[position] = new LineupPosition 
                { 
                    Position = position, 
                    Player = bestPlayer 
                };
                availablePlayers.Remove(bestPlayer);
            }
        }

        lineup.PredictedRatings = CalculateLineupRatings(lineup);
        return lineup;
    }

    private Player SelectBestPlayerForPosition(List<Player> availablePlayers, string position, FormationAnalysis analysis)
    {
        if (!FormationData.PositionContributions.TryGetValue(position, out var contribution))
            return null;

        var scoredPlayers = availablePlayers.Select(player => new
        {
            Player = player,
            Score = CalculatePlayerScoreForPosition(player, position, contribution, analysis)
        })
        .OrderByDescending(x => x.Score)
        .ToList();

        return scoredPlayers.FirstOrDefault()?.Player;
    }

    private double CalculatePlayerScoreForPosition(Player player, string position, PositionContribution contribution, FormationAnalysis analysis)
    {
        double score = 0;

        // Wkady z umiejetnosci
        score += player.Skills.Playmaking * contribution.MidfieldPM;
        score += player.Skills.Defending * (contribution.CentralDefenseDef + contribution.SideDefenseDef);
        score += player.Skills.Scoring * (contribution.CentralAttackSc + contribution.SideAttackSc);
        score += player.Skills.Passing * (contribution.CentralAttackPs + contribution.SideAttackPs);
        score += player.Skills.Winger * contribution.SideAttackWg;
        
        if (position == "GK")
        {
            score += player.Skills.Keeper * contribution.CentralDefenseGK;
            score += player.Skills.Keeper * contribution.SideDefenseGK;
        }

        // Wpisy formy
        if (FormationData.FormPerformance.TryGetValue(player.Form, out var formMultiplier))
        {
            score *= formMultiplier;
        }

        // Premia za specjalizacje
        score += GetSpecialtyBonus(player, position, analysis);

        // Premia za dobroczyno i dowiadczenie
        score += player.Loyalty * 0.5; // Uproszczony bonus
        score += player.Experience * 0.05; // Uproszczony bonus

        return score;
    }

    private double GetSpecialtyBonus(Player player, string position, FormationAnalysis analysis)
    {
        double bonus = 0;

        // Atletycy w pressingu
        if (player.Specialty == "Powerful" && analysis.RecommendedTactic == "Pressing")
        {
            bonus += 5;
        }

        // Szybcy obrocy przeciwko szybkim napastnikom
        if (player.Specialty == "Quick" && (position.Contains("WB") || position.Contains("CD")))
        {
            bonus += 3;
        }

        // Nieprzewidywalni w pozycjach ofensywnych
        if (player.Specialty == "Unpredictable" && 
            (position.Contains("FW") || position.Contains("W") || position.Contains("IM")))
        {
            bonus += 2;
        }

        // Techniczni w deszczu
        if (player.Specialty == "Technical" && analysis.WeatherCondition == "Rain")
        {
            bonus -= 2; // Kara w deszczu
        }

        return bonus;
    }

    private LineupRatings CalculateLineupRatings(Lineup lineup)
    {
        var ratings = new LineupRatings();

        // Obliczanie kontrybucji z uwzgludnieniem kar za aglomeracje
        var positionCounts = CountCentralPositions(lineup);
        var centralDefenderPenalty = FormationData.CentralDefenderPenalty.GetValueOrDefault(positionCounts.CentralDefenders, 1.0);
        var innerMidfielderPenalty = FormationData.InnerMidfielderPenalty.GetValueOrDefault(positionCounts.InnerMidfielders, 1.0);
        var forwardPenalty = FormationData.ForwardPenalty.GetValueOrDefault(positionCounts.Forwards, 1.0);

        foreach (var position in lineup.Positions.Values)
        {
            if (position.Player == null) continue;

            if (FormationData.PositionContributions.TryGetValue(position.Position, out var contribution))
            {
                // Wkady do ratingów z uwzgludnieniem kar
                var penalty = GetPenaltyForPosition(position.Position, centralDefenderPenalty, innerMidfielderPenalty, forwardPenalty);
                
                ratings.Midfield += position.Player.Skills.Playmaking * contribution.MidfieldPM * penalty;
                
                ratings.CentralDefense += position.Player.Skills.Defending * contribution.CentralDefenseDef * penalty;
                ratings.RightDefense += position.Player.Skills.Defending * contribution.SideDefenseDef * penalty;
                ratings.LeftDefense += position.Player.Skills.Defending * contribution.SideDefenseDef * penalty;
                
                ratings.CentralAttack += position.Player.Skills.Scoring * contribution.CentralAttackSc * penalty;
                ratings.CentralAttack += position.Player.Skills.Passing * contribution.CentralAttackPs * penalty;
                
                ratings.RightAttack += position.Player.Skills.Scoring * contribution.SideAttackSc * penalty;
                ratings.RightAttack += position.Player.Skills.Passing * contribution.SideAttackPs * penalty;
                ratings.RightAttack += position.Player.Skills.Winger * contribution.SideAttackWg * penalty;
                
                ratings.LeftAttack += position.Player.Skills.Scoring * contribution.SideAttackSc * penalty;
                ratings.LeftAttack += position.Player.Skills.Passing * contribution.SideAttackPs * penalty;
                ratings.LeftAttack += position.Player.Skills.Winger * contribution.SideAttackWg * penalty;
            }
        }

        // Dodanie wkadu bramkarza
        if (lineup.Positions.TryGetValue("GK", out var gk) && gk.Player != null)
        {
            if (FormationData.PositionContributions.TryGetValue("GK", out var gkContribution))
            {
                ratings.CentralDefense += gk.Player.Skills.Keeper * gkContribution.CentralDefenseGK;
                ratings.RightDefense += gk.Player.Skills.Keeper * gkContribution.SideDefenseGK;
                ratings.LeftDefense += gk.Player.Skills.Keeper * gkContribution.SideDefenseGK;
            }
        }

        ratings.Overall = (ratings.Midfield + ratings.CentralDefense + ratings.RightDefense + 
                          ratings.LeftDefense + ratings.CentralAttack + ratings.RightAttack + 
                          ratings.LeftAttack) / 7.0;

        return ratings;
    }

    private PositionCounts CountCentralPositions(Lineup lineup)
    {
        var counts = new PositionCounts();
        
        foreach (var position in lineup.Positions.Values)
        {
            if (position?.Position?.Contains("CD") == true)
                counts.CentralDefenders++;
            else if (position?.Position?.Contains("IM") == true)
                counts.InnerMidfielders++;
            else if (position?.Position?.Contains("FW") == true)
                counts.Forwards++;
        }

        return counts;
    }

    private double GetPenaltyForPosition(string position, double centralDefenderPenalty, double innerMidfielderPenalty, double forwardPenalty)
    {
        if (position.Contains("CD"))
            return centralDefenderPenalty;
        else if (position.Contains("IM"))
            return innerMidfielderPenalty;
        else if (position.Contains("FW"))
            return forwardPenalty;
        
        return 1.0;
    }

    private void ApplyTacticModifiers(ref LineupRatings ratings, string tactic, FormationAnalysis analysis)
    {
        switch (tactic)
        {
            case "Counter":
                ratings.Midfield *= FormationData.TacticModifiers.CounterAttackMidfieldPenalty;
                break;
            case "PIC":
                ratings.Midfield *= FormationData.TacticModifiers.PICMidfieldPenalty;
                break;
            case "MOTS":
                ratings.Midfield *= FormationData.TacticModifiers.MOTSMidfieldBonus;
                break;
        }

        // Dodatkowe modyfikatory dla AOW/AIM
        if (tactic == "AttackInMiddle" || tactic == "AttackOnWings")
        {
            // Zastosuj modyfikatory AOW/AIM do ataków
            ApplyAOWAIMModifiers(ref ratings, tactic);
        }
    }

    private void ApplyAOWAIMModifiers(ref LineupRatings ratings, string tactic)
    {
        // Uproszczona implementacja - w rzeczywisto wymaga analizy zawodników
        if (tactic == "AttackInMiddle")
        {
            ratings.CentralAttack *= 1.1; // +10% do ataku centralnego
            ratings.RightAttack *= 0.95; // -5% do ataków bocznych
            ratings.LeftAttack *= 0.95;
        }
        else if (tactic == "AttackOnWings")
        {
            ratings.CentralAttack *= 0.95; // -5% do ataku centralnego
            ratings.RightAttack *= 1.1; // +10% do ataków bocznych
            ratings.LeftAttack *= 1.1;
        }
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
            strengths.Add("Dominacja w pomocy - kontroluj tempo gry");

        if (myRatings.CentralDefense > opponentRatings.CentralAttack * 1.15)
            strengths.Add("Silna obrona centralna - przeciwnik ma problemy ze strzelaniem");

        if (myRatings.RightAttack > opponentRatings.LeftDefense * 1.15)
            strengths.Add("Przewaga na prawej flance - wykorzystaj to skrzydlo");

        if (myRatings.LeftAttack > opponentRatings.RightDefense * 1.15)
            strengths.Add("Przewaga na lewej flance - wykorzystaj to skrzydlo");

        return strengths;
    }

    private List<string> IdentifyWeaknesses(LineupRatings myRatings, LineupRatings opponentRatings)
    {
        var weaknesses = new List<string>();

        if (myRatings.Midfield < opponentRatings.Midfield * 0.85)
            weaknesses.Add("Sabo w pomocy - rozwa defensywn taktyk");

        if (myRatings.CentralDefense < opponentRatings.CentralAttack * 0.9)
            weaknesses.Add("Saba obrona centralna - wzmocnij");

        if (myRatings.RightDefense < opponentRatings.LeftAttack * 0.9)
            weaknesses.Add("Saba prawa obrona - przeciwnik moe to wykorzysta");

        if (myRatings.LeftDefense < opponentRatings.RightAttack * 0.9)
            weaknesses.Add("Saba lewa obrona - przeciwnik moe to wykorzysta");

        return weaknesses;
    }

    private List<string> GenerateRecommendations(TeamComparison comparison, Lineup lineup, FormationAnalysis analysis)
    {
        var recommendations = new List<string>();

        // Rekomendacje na podstawie stylu formacji
        switch (analysis.FormationStyle)
        {
            case FormationStyle.UltraDefensive:
                recommendations.Add("Graj defensywnie - formacja ultra-defensywna");
                recommendations.Add("Czekaj na bledy przeciwnika i kontruj");
                break;
            case FormationStyle.Defensive:
                recommendations.Add("Graj defensywnie - solidna obrona");
                recommendations.Add("Zachowaj czyste konto");
                break;
            case FormationStyle.MidfieldControl:
                recommendations.Add("Kontroluj gr w pomocy");
                recommendations.Add("Wykorzystaj przewagi w posiadaniu");
                break;
            case FormationStyle.Balanced:
                recommendations.Add("Graj zbalansowanie - elastyczno kluczem");
                break;
            case FormationStyle.Offensive:
                recommendations.Add("Graj ofensywnie - stwórz pres");
                break;
            case FormationStyle.UltraOffensive:
                recommendations.Add("Graj bardzo ofensywnie - atakuj bez pardonu");
                break;
        }

        // Rekomendacje na podstawie taktyki
        if (analysis.RecommendedTactic == "Counter")
        {
            recommendations.Add("Ustaw kontratak - czekaj na bledy przeciwnika");
        }

        // Rekomendacje na podstawie analizy przeciwnika
        if (analysis.OpponentStrength.SideDefense < 40)
        {
            recommendations.Add("Atakuj skrzydami - obrona przeciwnika jest saba");
        }

        if (analysis.OpponentStrength.CentralDefense < 40)
        {
            recommendations.Add("Atakuj przez centrum - przebij obron");
        }

        return recommendations;
    }
}

public class FormationAnalysis
{
    public FormationDefinition SelectedFormation { get; set; } = null!;
    public OpponentStrength OpponentStrength { get; set; } = null!;
    public string RecommendedTactic { get; set; } = "";
    public FormationStyle FormationStyle { get; set; }
    public string WeatherCondition { get; set; } = "Overcast"; // Domylnie
}

public class OpponentStrength
{
    public double Midfield { get; set; }
    public double CentralDefense { get; set; }
    public double SideDefense { get; set; }
    public double CentralAttack { get; set; }
    public double SideAttack { get; set; }
    public double Overall { get; set; }
    public string WeakSide { get; set; } = "";
    public string StrongSide { get; set; } = "";
}

public class PositionCounts
{
    public int CentralDefenders { get; set; }
    public int InnerMidfielders { get; set; }
    public int Forwards { get; set; }
}
