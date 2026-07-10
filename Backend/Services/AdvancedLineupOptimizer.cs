using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Optymalizator skladu maksymalizujacy prawdopodobienstwo wygranej.
/// Iteruje po (formacja x taktyka) i dla kazdej kombinacji robi lokalna optymalizacje
/// przypisania graczy do slotow + zachowan (WBD/WBN/WBO/WBTM itp.).
/// Wynik koncowy jest oceniany modelem Poissona: P(win) + 0.5 * P(draw).
/// </summary>
public class AdvancedLineupOptimizer
{
    private readonly HattrickApiService _hattrickApi;
    private readonly OpponentScoutService _scout;

    // Wlasciwe taktyki Hattricka (MatchTacticType)
    private static readonly string[] AllTactics =
    {
        "Normal", "Counter",
        "AttackInMiddle", "AttackOnWings",
        "Pressing", "PlayCreatively", "LongShots"
    };

    private readonly RatingEngine _engine = new();

    public AdvancedLineupOptimizer(HattrickApiService hattrickApi, OpponentScoutService scout)
    {
        _hattrickApi = hattrickApi;
        _scout = scout;
    }

    public async Task<OptimizerResponse> OptimizeLineupAsync(OptimizerRequest request)
    {
        var myTeam = await _hattrickApi.GetTeamDetailsAsync(request.MyTeamId);
        // Oceny przeciwnika ze skauta (srednia wazona z ostatnich meczow) zamiast
        // pojedynczego ostatniego meczu — mniejsza wariancja pojedynczego wystawienia.
        var opponentResult = await _scout.GetWeightedRatingsAsync(request.OpponentTeamId);
        var opponentRatings = opponentResult.Ratings;
        var context = new MatchContext
        {
            IsHomeMatch = request.IsHomeMatch,
            OpponentIspAtt = opponentRatings.IndirectSetPiecesAttRating,
            OpponentIspDef = opponentRatings.IndirectSetPiecesDefRating
        };

        // Pogoda regionu gospodarza — tylko dla znanego nadchodzacego meczu.
        MatchWeather? weather = null;
        if (request.MatchId != 0)
        {
            try
            {
                var hostTeamId = request.IsHomeMatch ? request.MyTeamId : request.OpponentTeamId;
                weather = await _hattrickApi.GetMatchWeatherAsync(hostTeamId, request.MatchDate);
                context.WeatherId = weather.WeatherId;
            }
            catch (Exception)
            {
                // Pogoda jest dodatkiem — optymalizacja dziala dalej bez niej.
            }
        }

        // InjuryLevel: -1 = zdrowy, 0 = siniak (moze grac), >=1 = tygodnie kontuzji (nie moze grac)
        var available = myTeam.Players.Where(p => p.InjuryLevel <= 0).ToList();
        if (available.Count < 11)
        {
            throw new InvalidOperationException($"Zbyt malo zdrowych graczy ({available.Count}) zeby ulozyc sklad.");
        }

        var tactics = ResolveTacticCandidates(request.PreferredTactic);
        var attitude = NormaliseAttitude(request.TeamAttitude);
        var coach = NormaliseCoachType(request.CoachType);

        OptimizationCandidate? best = null;
        var allCandidates = new List<OptimizationCandidate>();
        var formationsToTry = ResolveFormationCandidates(request.PreferredFormation);
        foreach (var formation in formationsToTry)
        {
            // Pierwsze przypisanie: niezalezne od taktyki — bazuje na lacznym wkladzie w ratingi.
            var baseLineup = BuildInitialLineup(formation, available);
            if (baseLineup == null) continue;

            // Ulepsz przypisanie poprzez 2-opt na sile druzyny
            baseLineup = LocalSearchPlayerAssignment(formation, baseLineup, available);

            int formExp = request.FormationExperience.GetValueOrDefault(formation.Name, 5);
            double disorderRisk = ComputeDisorderRisk(formExp);

            OptimizationCandidate? bestForFormation = null;
            foreach (var tactic in tactics)
            {
                var candidate = EvaluateCandidate(formation, tactic, attitude, coach, baseLineup, opponentRatings, context, disorderRisk);
                candidate = OptimiseBehaviours(candidate, attitude, coach, opponentRatings, context, disorderRisk);

                if (bestForFormation == null || candidate.Score > bestForFormation.Score)
                {
                    bestForFormation = candidate;
                }

                if (best == null || candidate.Score > best.Score)
                {
                    best = candidate;
                }
            }

            if (bestForFormation != null)
            {
                allCandidates.Add(bestForFormation);
            }
        }

        if (best == null)
        {
            throw new InvalidOperationException("Nie udalo sie zbudowac zadnego skladu.");
        }

        var lineup = BuildFinalLineup(best);
        var oppLineupRatings = ConvertToLineupRatings(opponentRatings);

        var lang = request.Language ?? "pl";

        var comparison = new TeamComparison
        {
            MyTeamRatings = best.Ratings,
            OpponentRatings = oppLineupRatings,
            Strengths = IdentifyStrengths(best.Ratings, oppLineupRatings, lang),
            Weaknesses = IdentifyWeaknesses(best.Ratings, oppLineupRatings, lang)
        };

        var recommendations = GenerateRecommendations(best, comparison, lang);

        var alternatives = allCandidates
            .OrderByDescending(c => c.Score)
            .Select(c => new FormationAlternative
            {
                Formation = c.Lineup.Formation.Name,
                Tactic = c.Tactic,
                Attitude = c.Attitude,
                WinProbability = c.WinProbability,
                DrawProbability = c.DrawProbability,
                LossProbability = c.LossProbability,
                ExpectedGoalsFor = c.ExpectedGoalsFor,
                ExpectedGoalsAgainst = c.ExpectedGoalsAgainst,
                DisorderRisk = c.DisorderRisk,
                Ratings = c.Ratings
            })
            .ToList();

        return new OptimizerResponse
        {
            OptimalLineup = lineup,
            Recommendations = recommendations,
            Comparison = comparison,
            Alternatives = alternatives,
            OpponentRatingsSource = opponentResult.Source,
            OpponentRatingsMatchId = opponentResult.SourceMatchId,
            OpponentRatingsMatchDate = opponentResult.SourceMatchDate,
            Weather = weather
        };
    }

    // ======================= Tactics =======================

    private static IEnumerable<FormationDefinition> ResolveFormationCandidates(string preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred) || preferred == "Auto")
        {
            return FormationData.Formations.Values;
        }
        if (FormationData.Formations.TryGetValue(preferred, out var specific))
        {
            return new[] { specific };
        }
        return FormationData.Formations.Values;
    }

    private static IEnumerable<string> ResolveTacticCandidates(string preferred)
    {
        // "Auto" / pusto -> probuj wszystkie. Konkretna taktyka -> tylko ona.
        if (string.IsNullOrWhiteSpace(preferred) || preferred == "Auto")
        {
            return AllTactics;
        }
        return AllTactics.Contains(preferred) ? new[] { preferred } : AllTactics;
    }

    // Postawa druzyny wybierana przez trenera — nie jest dobierana automatycznie.
    private static string NormaliseAttitude(string attitude)
    {
        if (string.IsNullOrWhiteSpace(attitude)) return "Normal";
        return attitude switch
        {
            "PIC" => "PIC",
            "MOTS" => "MOTS",
            _ => "Normal"
        };
    }

    private static string NormaliseCoachType(string coach)
    {
        if (string.IsNullOrWhiteSpace(coach)) return "Neutral";
        return coach switch
        {
            "Offensive" => "Offensive",
            "Defensive" => "Defensive",
            _ => "Neutral"
        };
    }

    // Prawdopodobienstwo nieladu formacji w funkcji poziomu doswiadczenia (3..10).
    // 10 (olsniewajace) = 0% ryzyka. Nizsze poziomy = rosnace ryzyko.
    private static double ComputeDisorderRisk(int formationExperience)
    {
        int e = Math.Clamp(formationExperience, 3, 10);
        return e switch
        {
            10 => 0.00,
            9 => 0.02,
            8 => 0.05,
            7 => 0.10,
            6 => 0.18,
            5 => 0.28,
            4 => 0.40,
            _ => 0.55  // 3 = kiepskie
        };
    }

    // ======================= Assignment =======================

    private AssignedLineup? BuildInitialLineup(FormationDefinition formation, List<Player> available)
    {
        var slots = formation.Positions.ToList();
        var players = available.ToList();

        // GK: najlepszy wg keeper-score
        var gk = players.OrderByDescending(p => EffectiveSkill(p, p.Skills.Keeper)).FirstOrDefault();
        if (gk == null) return null;

        var result = new AssignedLineup
        {
            Formation = formation,
            Slots = new Dictionary<string, AssignedSlot>()
        };

        result.Slots["GK"] = new AssignedSlot { SlotId = "GK", Player = gk, Behaviour = "GK" };
        players.Remove(gk);

        var outfieldSlots = slots.Where(s => s != "GK").ToList();

        // Pierwsze pokrycie: zachlannie, sloty od "najmocniejszej preferencji" (najwiekszy spread scoringu).
        var remaining = new List<Player>(players);
        var slotScores = new Dictionary<string, Dictionary<int, double>>(); // slot -> playerId -> score
        foreach (var slot in outfieldSlots)
        {
            var d = new Dictionary<int, double>();
            foreach (var p in remaining)
            {
                d[p.PlayerId] = BestBehaviourScore(slot, p, null, out _);
            }
            slotScores[slot] = d;
        }

        // Uporzadkuj sloty wg wariancji scoringu (wieksza wariancja = wazniejszy wybor).
        var slotOrder = outfieldSlots
            .OrderByDescending(s => Variance(slotScores[s].Values))
            .ToList();

        foreach (var slot in slotOrder)
        {
            var best = remaining
                .OrderByDescending(p => slotScores[slot][p.PlayerId])
                .FirstOrDefault();
            if (best == null) break;
            _ = BestBehaviourScore(slot, best, null, out var bhv);
            result.Slots[slot] = new AssignedSlot { SlotId = slot, Player = best, Behaviour = bhv };
            remaining.Remove(best);
        }

        if (result.Slots.Count < slots.Count) return null;
        return result;
    }

    private AssignedLineup LocalSearchPlayerAssignment(FormationDefinition formation, AssignedLineup lineup, List<Player> available)
    {
        var assignedIds = lineup.Slots.Values.Select(s => s.Player.PlayerId).ToHashSet();
        var bench = available.Where(p => !assignedIds.Contains(p.PlayerId)).ToList();

        // 2-opt: wymieniaj graczy miedzy slotami oraz z lawka, jesli poprawia sile druzyny.
        // Sila druzyny = suma wazonych ratingow (hatstats-like).
        bool improved = true;
        int iterations = 0;
        while (improved && iterations++ < 12)
        {
            improved = false;
            var slots = lineup.Slots.Keys.Where(k => k != "GK").ToList();

            // Swap miedzy slotami
            for (int i = 0; i < slots.Count; i++)
            {
                for (int j = i + 1; j < slots.Count; j++)
                {
                    var sa = slots[i]; var sb = slots[j];
                    var pa = lineup.Slots[sa]; var pb = lineup.Slots[sb];
                    var currentScore = TeamStrength(lineup);

                    var na = new AssignedSlot { SlotId = sa, Player = pb.Player, Behaviour = BestBehaviourFor(sa, pb.Player) };
                    var nb = new AssignedSlot { SlotId = sb, Player = pa.Player, Behaviour = BestBehaviourFor(sb, pa.Player) };
                    lineup.Slots[sa] = na; lineup.Slots[sb] = nb;
                    var newScore = TeamStrength(lineup);

                    if (newScore > currentScore + 0.01)
                    {
                        improved = true;
                    }
                    else
                    {
                        lineup.Slots[sa] = pa; lineup.Slots[sb] = pb;
                    }
                }
            }

            // Podmiana z lawka
            foreach (var slot in slots.ToList())
            {
                var current = lineup.Slots[slot];
                var bestBench = bench
                    .Select(b => new { P = b, Score = BestBehaviourScore(slot, b, null, out _) })
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();
                if (bestBench == null) continue;

                var currentScore = TeamStrength(lineup);
                var newAssigned = new AssignedSlot
                {
                    SlotId = slot,
                    Player = bestBench.P,
                    Behaviour = BestBehaviourFor(slot, bestBench.P)
                };
                lineup.Slots[slot] = newAssigned;
                var newScore = TeamStrength(lineup);
                if (newScore > currentScore + 0.01)
                {
                    bench.Remove(bestBench.P);
                    bench.Add(current.Player);
                    improved = true;
                }
                else
                {
                    lineup.Slots[slot] = current;
                }
            }
        }

        return lineup;
    }

    private string BestBehaviourFor(string slot, Player p)
    {
        _ = BestBehaviourScore(slot, p, null, out var bhv);
        return bhv;
    }

    /// <summary>
    /// Najlepszy wklad gracza na slocie po wszystkich wariantach zachowan.
    /// Opcjonalne `weights` moga modyfikowac preferencje per typ ratingu.
    /// </summary>
    private double BestBehaviourScore(string slot, Player player, RatingWeights? weights, out string bestBehaviour)
    {
        bestBehaviour = slot;
        if (!FormationData.SlotBehaviourOptions.TryGetValue(slot, out var options))
        {
            options = new[] { slot };
        }

        double best = double.NegativeInfinity;
        foreach (var bhv in options)
        {
            if (!FormationData.PositionContributions.TryGetValue(bhv, out var contrib)) continue;
            var score = PlayerContributionScore(player, slot, contrib, weights);
            if (score > best)
            {
                best = score;
                bestBehaviour = bhv;
            }
        }
        return best < double.MinValue / 2 ? 0 : best;
    }

    private double PlayerContributionScore(Player p, string slot, PositionContribution c, RatingWeights? w)
    {
        w ??= RatingWeights.Uniform;
        var contrib = RatingEngine.ContributionFor(p, slot, c);
        return w.Midfield * contrib.Mid +
               w.CentralDefense * contrib.Cd +
               w.RightDefense * contrib.Rd + w.LeftDefense * contrib.Ld +
               w.CentralAttack * contrib.Ca +
               w.RightAttack * contrib.Ra + w.LeftAttack * contrib.La;
    }

    private static double EffectiveSkill(Player p, int rawSkill) =>
        RatingEngine.EffSkill(p, rawSkill) * RatingEngine.EffectiveMultiplier(p);

    // ======================= Ratings & Evaluation =======================

    private OptimizationCandidate EvaluateCandidate(FormationDefinition formation, string tactic, string attitude, string coach, AssignedLineup lineup, TeamRatings opponent, MatchContext context, double disorderRisk = 0.0)
    {
        var ratings = _engine.ComputeRatings(lineup, context.WeatherId);
        _engine.ApplyTactic(ratings, tactic);
        _engine.ApplyAttitude(ratings, attitude);
        _engine.ApplyCoach(ratings, coach);
        _engine.ApplyHomeAdvantage(ratings, context.IsHomeMatch);
        _engine.ApplyDisorder(ratings, disorderRisk);

        var opp = ConvertToLineupRatings(opponent);

        var (myIspAtt, myIspDef) = _engine.ComputeIndirectSetPieces(lineup);
        var prediction = _engine.PredictOutcome(
            ratings, opp, tactic, lineup,
            myIspAtt, myIspDef, context.OpponentIspAtt, context.OpponentIspDef);

        return new OptimizationCandidate
        {
            Lineup = lineup,
            Tactic = tactic,
            Attitude = attitude,
            Coach = coach,
            Ratings = ratings,
            WinProbability = prediction.WinProbability,
            DrawProbability = prediction.DrawProbability,
            LossProbability = prediction.LossProbability,
            ExpectedGoalsFor = prediction.ExpectedGoalsFor,
            ExpectedGoalsAgainst = prediction.ExpectedGoalsAgainst,
            DisorderRisk = disorderRisk,
            Score = prediction.WinProbability + 0.5 * prediction.DrawProbability
        };
    }

    private OptimizationCandidate OptimiseBehaviours(OptimizationCandidate cand, string attitude, string coach, TeamRatings opponent, MatchContext context, double disorderRisk = 0.0)
    {
        // Lekka iteracja: dla kazdego slotu sprobuj kazdego zachowania i zachowaj te,
        // ktore maksymalizuje P(win) + 0.5*P(draw).
        var lineup = cand.Lineup;
        double currentScore = cand.Score;
        bool improved = true;
        int it = 0;
        while (improved && it++ < 4)
        {
            improved = false;
            foreach (var kv in lineup.Slots.ToList())
            {
                var slot = kv.Key;
                var cur = kv.Value;
                if (!FormationData.SlotBehaviourOptions.TryGetValue(slot, out var options)) continue;
                string bestBhv = cur.Behaviour;
                double bestScore = currentScore;
                foreach (var bhv in options)
                {
                    if (bhv == cur.Behaviour) continue;
                    lineup.Slots[slot] = new AssignedSlot { SlotId = slot, Player = cur.Player, Behaviour = bhv };
                    var test = EvaluateCandidate(cand.Lineup.Formation, cand.Tactic, attitude, coach, lineup, opponent, context, disorderRisk);
                    if (test.Score > bestScore + 1e-6)
                    {
                        bestScore = test.Score;
                        bestBhv = bhv;
                    }
                }
                lineup.Slots[slot] = new AssignedSlot { SlotId = slot, Player = cur.Player, Behaviour = bestBhv };
                if (Math.Abs(bestScore - currentScore) > 1e-6)
                {
                    currentScore = bestScore;
                    improved = true;
                }
            }
        }
        // Przelicz na koncu dla pewnosci
        var final = EvaluateCandidate(cand.Lineup.Formation, cand.Tactic, attitude, coach, lineup, opponent, context, disorderRisk);
        return final;
    }

    // ======================= Helpers =======================

    private double TeamStrength(AssignedLineup lineup)
    {
        var r = _engine.ComputeRatings(lineup);
        return 3 * r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense
             + r.CentralAttack + r.RightAttack + r.LeftAttack;
    }

    private static double Variance(IEnumerable<double> values)
    {
        var arr = values.ToArray();
        if (arr.Length == 0) return 0;
        double mean = arr.Average();
        return arr.Sum(v => (v - mean) * (v - mean)) / arr.Length;
    }

    private LineupRatings ConvertToLineupRatings(TeamRatings t) => new()
    {
        Midfield = t.MidfieldRating,
        RightDefense = t.RightDefenseRating,
        CentralDefense = t.CentralDefenseRating,
        LeftDefense = t.LeftDefenseRating,
        RightAttack = t.RightAttackRating,
        CentralAttack = t.CentralAttackRating,
        LeftAttack = t.LeftAttackRating,
        Overall = (t.MidfieldRating + t.CentralDefenseRating + t.RightDefenseRating + t.LeftDefenseRating
                  + t.CentralAttackRating + t.RightAttackRating + t.LeftAttackRating) / 7.0
    };

    private Lineup BuildFinalLineup(OptimizationCandidate best)
    {
        var lineup = new Lineup
        {
            TacticType = best.Tactic,
            Formation = best.Lineup.Formation.Name,
            PredictedRatings = best.Ratings
        };
        foreach (var s in best.Lineup.Slots)
        {
            lineup.Positions[s.Key] = new LineupPosition
            {
                Position = s.Key,
                Player = s.Value.Player,
                Behavior = s.Value.Behaviour,
                Rating = ComputePlayerSlotRating(s.Value.Player, s.Key, s.Value.Behaviour),
                IsBruised = s.Value.Player.InjuryLevel == 0
            };
        }
        return lineup;
    }

    /// <summary>
    /// Szacowana ocena meczowa gracza na pozycji (skala Hattrick 0-20).
    /// Dla bramkarza "magicznego" (keeper=19) daje ~9 na start z wysoka kondycja.
    /// Bazuje na glownej umiejetnosci wymaganej przez rolke + modyfikatory (forma, kondycja, XP, lojalnosc).
    /// </summary>
    private double ComputePlayerSlotRating(Player player, string slot, string behaviour)
    {
        if (player == null) return 0;
        double eff = RatingEngine.EffectiveMultiplier(player);
        var skills = player.Skills;
        double main = slot switch
        {
            "GK" => skills.Keeper,
            "RWB" or "LWB" => 0.7 * skills.Defending + 0.3 * skills.Winger,
            "RCD" or "LCD" or "CD" => skills.Defending,
            "RW" or "LW" => 0.6 * skills.Winger + 0.4 * skills.Playmaking,
            "RIM" or "LIM" or "IM" => skills.Playmaking,
            "RFW" or "LFW" or "FW" or "CFW" => skills.Scoring,
            _ => skills.Playmaking
        };
        // Wspolczynnik 0.4: skill 19 * 0.4 * eff(~1.15) ~ 8.7 → pokrywa sie z ocenami meczowymi w Hattrick.
        double rating = main * 0.4 * eff;
        return Math.Max(0, Math.Min(20, rating));
    }

    private static string T(string pl, string en, string lang) => lang == "en" ? en : pl;

    private List<string> IdentifyStrengths(LineupRatings me, LineupRatings opp, string lang)
    {
        var r = new List<string>();
        if (me.Midfield > opp.Midfield * 1.15)
            r.Add(T("Przewaga w pomocy — więcej szans na Twoją drużynę.", "Midfield advantage — more chances for your team.", lang));
        if (me.CentralDefense > opp.CentralAttack * 1.15)
            r.Add(T("Mocna obrona centralna — przeciwnik będzie miał trudno w środku.", "Strong central defense — opponent will struggle through the middle.", lang));
        bool rightAdv = me.RightAttack > opp.LeftDefense * 1.15;
        bool leftAdv  = me.LeftAttack  > opp.RightDefense * 1.15;
        if (rightAdv && leftAdv)
            r.Add(T("Obie flanki silne względem obrony przeciwnika — idealny moment na taktykę AOW (Atak skrzydłami, wzmacnia oba skrzydła jednocześnie).", "Both flanks strong vs opponent defense — ideal for AOW (Attack on Wings, boosts both wings simultaneously).", lang));
        else if (rightAdv)
            r.Add(T("Prawa flanka silna, jednak AOW wzmacnia oba skrzydła jednocześnie — wzmocnij lewe skrzydło, by w pełni wykorzystać tę przewagę.", "Right flank strong, but AOW boosts both wings simultaneously — strengthen the left wing to fully exploit this advantage.", lang));
        else if (leftAdv)
            r.Add(T("Lewa flanka silna, jednak AOW wzmacnia oba skrzydła jednocześnie — wzmocnij prawe skrzydło, by w pełni wykorzystać tę przewagę.", "Left flank strong, but AOW boosts both wings simultaneously — strengthen the right wing to fully exploit this advantage.", lang));
        return r;
    }

    private List<string> IdentifyWeaknesses(LineupRatings me, LineupRatings opp, string lang)
    {
        var r = new List<string>();
        if (me.Midfield < opp.Midfield * 0.9)
            r.Add(T("Słabsza pomoc — przeciwnik będzie miał posiadanie.", "Weak midfield — opponent will dominate possession.", lang));
        if (me.CentralDefense < opp.CentralAttack * 0.9)
            r.Add(T("Zagrożenie w środku obrony — wzmocnij.", "Central defense under threat — reinforce it.", lang));
        if (me.RightDefense < opp.LeftAttack * 0.9)
            r.Add(T("Prawa obrona pod presją — ryzyko straty z lewego ataku przeciwnika.", "Right defense under pressure — risk from opponent's left attack.", lang));
        if (me.LeftDefense < opp.RightAttack * 0.9)
            r.Add(T("Lewa obrona pod presją — ryzyko straty z prawego ataku przeciwnika.", "Left defense under pressure — risk from opponent's right attack.", lang));
        return r;
    }

    private List<string> GenerateRecommendations(OptimizationCandidate best, TeamComparison cmp, string lang)
    {
        var formationDesc = lang == "en" ? best.Lineup.Formation.DescriptionEn : best.Lineup.Formation.Description;
        var rec = new List<string>
        {
            T($"Formacja: {best.Lineup.Formation.Name} ({formationDesc}).",
              $"Formation: {best.Lineup.Formation.Name} ({formationDesc}).", lang),
            T($"Taktyka: {TranslateTactic(best.Tactic, lang)}.",
              $"Tactic: {TranslateTactic(best.Tactic, lang)}.", lang),
            T($"Postawa drużyny: {TranslateAttitude(best.Attitude, lang)}.",
              $"Team attitude: {TranslateAttitude(best.Attitude, lang)}.", lang),
            T($"Trener: {TranslateCoach(best.Coach, lang)}.",
              $"Coach: {TranslateCoach(best.Coach, lang)}.", lang),
            T($"Szacowane prawdopodobieństwo: wygrana {best.WinProbability:P1}, remis {best.DrawProbability:P1}, porażka {best.LossProbability:P1}.",
              $"Estimated probability: win {best.WinProbability:P1}, draw {best.DrawProbability:P1}, loss {best.LossProbability:P1}.", lang),
            T($"Oczekiwany wynik (lambda): {best.ExpectedGoalsFor:F2}:{best.ExpectedGoalsAgainst:F2}.",
              $"Expected score (lambda): {best.ExpectedGoalsFor:F2}:{best.ExpectedGoalsAgainst:F2}.", lang)
        };
        if (best.DisorderRisk > 0.01)
        {
            rec.Add(T($"Ryzyko nieładu (niskie doświadczenie formacji): {best.DisorderRisk:P0} — rozważ częstsze granie tą formacją lub wybór formacji o wyższym poziomie doświadczenia.",
                      $"Disorder risk (low formation experience): {best.DisorderRisk:P0} — consider playing this formation more often or choosing one with higher experience.", lang));
        }

        var bhvSummary = best.Lineup.Slots
            .Where(kv => kv.Key != "GK")
            .Select(kv => $"{kv.Key}={kv.Value.Behaviour}")
            .ToList();
        rec.Add(T("Ustawienia per slot: ", "Per-slot settings: ", lang) + string.Join(", ", bhvSummary));

        rec.AddRange(cmp.Strengths);
        rec.AddRange(cmp.Weaknesses);
        return rec;
    }

    private static string TranslateTactic(string t, string lang) => t switch
    {
        "Normal"         => T("Zwykła", "Normal", lang),
        "Counter"        => T("Kontratak", "Counter-attack", lang),
        "AttackInMiddle" => T("Atak środkiem (AIM) — wzmacnia centralny atak, osłabia oba skrzydła", "Attack in the Middle (AIM) — boosts central attack, weakens both wings", lang),
        "AttackOnWings"  => T("Atak skrzydłami (AOW) — wzmacnia oba skrzydła jednocześnie, osłabia centralny atak", "Attack on Wings (AOW) — boosts both wings simultaneously, weakens central attack", lang),
        "Pressing"       => "Pressing",
        "PlayCreatively" => T("Kreatywna gra", "Play Creatively", lang),
        "LongShots"      => T("Strzały z dystansu", "Long Shots", lang),
        _ => t
    };

    private static string TranslateAttitude(string a, string lang) => a switch
    {
        "PIC"  => T("Gra na luzie (PIC)", "Play It Cool (PIC)", lang),
        "MOTS" => T("Mecz sezonu (MOTS)", "Match of the Season (MOTS)", lang),
        _ => T("Normalne spotkanie", "Normal match", lang)
    };

    private static string TranslateCoach(string c, string lang) => c switch
    {
        "Offensive" => T("Ofensywny", "Offensive", lang),
        "Defensive" => T("Defensywny", "Defensive", lang),
        _ => T("Neutralny", "Neutral", lang)
    };
}

// ======================= Internal structures =======================

internal class AssignedSlot
{
    public string SlotId { get; set; } = "";
    public Player Player { get; set; } = null!;
    public string Behaviour { get; set; } = "";
}

// Kontekst meczu przekazywany do oceny kandydatow (dom/wyjazd, ISP przeciwnika).
internal class MatchContext
{
    public bool IsHomeMatch { get; set; }
    public double OpponentIspAtt { get; set; }
    public double OpponentIspDef { get; set; }
    // Kod pogody CHPP (0=deszcz..3=slonce); -1 = nieznana (bez wplywu).
    public int WeatherId { get; set; } = RatingEngine.WeatherUnknown;
}

internal class AssignedLineup
{
    public FormationDefinition Formation { get; set; } = null!;
    public Dictionary<string, AssignedSlot> Slots { get; set; } = new();
}

internal class OptimizationCandidate
{
    public AssignedLineup Lineup { get; set; } = null!;
    public string Tactic { get; set; } = "";
    public string Attitude { get; set; } = "Normal";
    public string Coach { get; set; } = "Neutral";
    public LineupRatings Ratings { get; set; } = new();
    public double WinProbability { get; set; }
    public double DrawProbability { get; set; }
    public double LossProbability { get; set; }
    public double ExpectedGoalsFor { get; set; }
    public double ExpectedGoalsAgainst { get; set; }
    public double DisorderRisk { get; set; }
    public double Score { get; set; }
}

internal class RatingWeights
{
    public double Midfield { get; set; } = 1.0;
    public double CentralDefense { get; set; } = 1.0;
    public double RightDefense { get; set; } = 1.0;
    public double LeftDefense { get; set; } = 1.0;
    public double CentralAttack { get; set; } = 1.0;
    public double RightAttack { get; set; } = 1.0;
    public double LeftAttack { get; set; } = 1.0;

    public static RatingWeights Uniform => new();
}
