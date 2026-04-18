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

    // Wlasciwe taktyki Hattricka (MatchTacticType)
    private static readonly string[] AllTactics =
    {
        "Normal", "Counter",
        "AttackInMiddle", "AttackOnWings",
        "Pressing", "PlayCreatively", "LongShots"
    };

    public AdvancedLineupOptimizer(HattrickApiService hattrickApi)
    {
        _hattrickApi = hattrickApi;
    }

    public async Task<OptimizerResponse> OptimizeLineupAsync(OptimizerRequest request)
    {
        var myTeam = await _hattrickApi.GetTeamDetailsAsync(request.MyTeamId);
        var opponentRatings = await _hattrickApi.GetOpponentRatingsAsync(request.OpponentTeamId, 0);

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
                var candidate = EvaluateCandidate(formation, tactic, attitude, coach, baseLineup, opponentRatings);
                candidate = OptimiseBehaviours(candidate, attitude, coach, opponentRatings);
                // Kara za nielad formacji — proporcjonalnie obniza wszystkie prawdopodobienstwa wygranej.
                candidate.DisorderRisk = disorderRisk;
                candidate.Score = (candidate.WinProbability + 0.5 * candidate.DrawProbability) * (1.0 - 0.5 * disorderRisk);

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

        var comparison = new TeamComparison
        {
            MyTeamRatings = best.Ratings,
            OpponentRatings = oppLineupRatings,
            Strengths = IdentifyStrengths(best.Ratings, oppLineupRatings),
            Weaknesses = IdentifyWeaknesses(best.Ratings, oppLineupRatings)
        };

        var recommendations = GenerateRecommendations(best, comparison);

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
            Alternatives = alternatives
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
        double eff = EffectiveMultiplier(p);

        double mid = c.MidfieldPM * p.Skills.Playmaking;
        double cd = c.CentralDefenseDef * p.Skills.Defending + c.CentralDefenseGK * p.Skills.Keeper;
        double sd = c.SideDefenseDef * p.Skills.Defending + c.SideDefenseGK * p.Skills.Keeper;
        double ca = c.CentralAttackSc * p.Skills.Scoring + c.CentralAttackPs * p.Skills.Passing;
        double sa = c.SideAttackSc * p.Skills.Scoring + c.SideAttackPs * p.Skills.Passing + c.SideAttackWg * p.Skills.Winger;

        // Rozrzuc sd/sa na odpowiednia strone wg slotu
        double rd = 0, ld = 0, ra = 0, la = 0;
        var side = FormationData.SlotSide.GetValueOrDefault(slot, "C");
        if (side == "R") { rd = sd; ra = sa; }
        else if (side == "L") { ld = sd; la = sa; }
        else
        {
            // Centralnie — rozlaczamy po rowno na obie flanki
            rd = sd * 0.5; ld = sd * 0.5;
            ra = sa * 0.5; la = sa * 0.5;
        }

        double score = eff * (
            w.Midfield * mid +
            w.CentralDefense * cd +
            w.RightDefense * rd + w.LeftDefense * ld +
            w.CentralAttack * ca +
            w.RightAttack * ra + w.LeftAttack * la);

        return score;
    }

    /// <summary>Mnoznik efektywnych umiejetnosci: forma, doswiadczenie, lojalnosc, stamina.</summary>
    private double EffectiveMultiplier(Player p)
    {
        double form = FormationData.FormPerformance.GetValueOrDefault(p.Form, 0.9);
        // XP wg poradnika: 1 + 0.0716 * sqrt(xp-1)
        double xp = 1.0 + 0.0716 * Math.Sqrt(Math.Max(0, p.Experience - 1));
        // Lojalnosc: max +1 do skilla gdy Motherclub, upraszczamy: 1 + loyalty*0.01 (0-9)
        double loy = 1.0 + Math.Min(0.1, Math.Max(0, p.Loyalty) * 0.01);
        // Kondycja: ((stamina+6.5)/14)^0.6 — dla sredniej staminy ~8 daje ~0.98
        double stamina = p.Stamina > 0 ? Math.Pow((p.Stamina + 6.5) / 14.0, 0.6) : 1.0;
        // XP ma sens tylko czesciowo — cap tak by nie rosl nadmiernie
        return form * Math.Min(1.35, xp) * loy * Math.Min(1.05, stamina);
    }

    private double EffectiveSkill(Player p, int rawSkill) => rawSkill * EffectiveMultiplier(p);

    // ======================= Ratings & Evaluation =======================

    private OptimizationCandidate EvaluateCandidate(FormationDefinition formation, string tactic, string attitude, string coach, AssignedLineup lineup, TeamRatings opponent)
    {
        var ratings = ComputeRatings(lineup);
        ApplyTacticAndContext(ref ratings, tactic, lineup);
        ApplyAttitude(ref ratings, attitude);
        ApplyCoach(ref ratings, coach);
        var opp = ConvertToLineupRatings(opponent);

        var (pWin, pDraw, pLoss, lamMe, lamOpp) = PoissonWinProbability(ratings, opp, tactic, lineup);

        return new OptimizationCandidate
        {
            Lineup = lineup,
            Tactic = tactic,
            Attitude = attitude,
            Coach = coach,
            Ratings = ratings,
            WinProbability = pWin,
            DrawProbability = pDraw,
            LossProbability = pLoss,
            ExpectedGoalsFor = lamMe,
            ExpectedGoalsAgainst = lamOpp,
            Score = pWin + 0.5 * pDraw
        };
    }

    private OptimizationCandidate OptimiseBehaviours(OptimizationCandidate cand, string attitude, string coach, TeamRatings opponent)
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
                    var test = EvaluateCandidate(cand.Lineup.Formation, cand.Tactic, attitude, coach, lineup, opponent);
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
        var final = EvaluateCandidate(cand.Lineup.Formation, cand.Tactic, attitude, coach, lineup, opponent);
        return final;
    }

    // Typ trenera: ofensywny bumpuje ataki, defensywny bumpuje obrony. Neutralny — bez zmian.
    private void ApplyCoach(ref LineupRatings r, string coach)
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
        else
        {
            return;
        }
        r.Overall = (r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense +
                     r.CentralAttack + r.RightAttack + r.LeftAttack) / 7.0;
    }

    // Postawa druzyny (PIC/Normal/MOTS) — wybor trenera, niezalezny od taktyki.
    private void ApplyAttitude(ref LineupRatings r, string attitude)
    {
        double mult = attitude switch
        {
            "PIC" => FormationData.TacticModifiers.PICMidfieldPenalty,
            "MOTS" => FormationData.TacticModifiers.MOTSMidfieldBonus,
            _ => 1.0
        };
        if (Math.Abs(mult - 1.0) > 1e-9)
        {
            r.Midfield *= mult;
            r.Overall = (r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense +
                         r.CentralAttack + r.RightAttack + r.LeftAttack) / 7.0;
        }
    }

    private LineupRatings ComputeRatings(AssignedLineup lineup)
    {
        var r = new LineupRatings();

        // Aglomeracje: policz graczy na CD / IM / FW
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

        foreach (var s in lineup.Slots.Values)
        {
            if (!FormationData.PositionContributions.TryGetValue(s.Behaviour, out var c)) continue;
            double eff = EffectiveMultiplier(s.Player);

            double pen = 1.0;
            if (IsCentralDefenderSlot(s.SlotId)) pen = penCD;
            else if (IsInnerMidfielderSlot(s.SlotId)) pen = penIM;
            else if (IsForwardSlot(s.SlotId)) pen = penFW;

            double mid = c.MidfieldPM * s.Player.Skills.Playmaking;
            double cd = c.CentralDefenseDef * s.Player.Skills.Defending + c.CentralDefenseGK * s.Player.Skills.Keeper;
            double sd = c.SideDefenseDef * s.Player.Skills.Defending + c.SideDefenseGK * s.Player.Skills.Keeper;
            double ca = c.CentralAttackSc * s.Player.Skills.Scoring + c.CentralAttackPs * s.Player.Skills.Passing;
            double sa = c.SideAttackSc * s.Player.Skills.Scoring + c.SideAttackPs * s.Player.Skills.Passing + c.SideAttackWg * s.Player.Skills.Winger;

            var side = FormationData.SlotSide.GetValueOrDefault(s.SlotId, "C");
            double effPen = eff * pen;

            r.Midfield += mid * effPen;
            r.CentralDefense += cd * effPen;
            r.CentralAttack += ca * effPen;

            if (side == "R")
            {
                r.RightDefense += sd * effPen;
                r.RightAttack += sa * effPen;
            }
            else if (side == "L")
            {
                r.LeftDefense += sd * effPen;
                r.LeftAttack += sa * effPen;
            }
            else
            {
                r.RightDefense += sd * 0.5 * effPen;
                r.LeftDefense += sd * 0.5 * effPen;
                r.RightAttack += sa * 0.5 * effPen;
                r.LeftAttack += sa * 0.5 * effPen;
            }
        }

        r.Overall = (r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense +
                     r.CentralAttack + r.RightAttack + r.LeftAttack) / 7.0;
        return r;
    }

    private void ApplyTacticAndContext(ref LineupRatings r, string tactic, AssignedLineup lineup)
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
                r.Midfield *= FormationData.TacticModifiers.PressingMidfieldPenalty;
                r.CentralAttack *= FormationData.TacticModifiers.PressingAttackPenalty;
                r.RightAttack *= FormationData.TacticModifiers.PressingAttackPenalty;
                r.LeftAttack *= FormationData.TacticModifiers.PressingAttackPenalty;
                r.CentralDefense *= FormationData.TacticModifiers.PressingDefenseBonus;
                r.RightDefense *= FormationData.TacticModifiers.PressingDefenseBonus;
                r.LeftDefense *= FormationData.TacticModifiers.PressingDefenseBonus;
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
        r.Overall = (r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense +
                     r.CentralAttack + r.RightAttack + r.LeftAttack) / 7.0;
    }

    // ======================= Win probability =======================

    private (double pWin, double pDraw, double pLoss, double lamMe, double lamOpp)
        PoissonWinProbability(LineupRatings me, LineupRatings opp, string tactic, AssignedLineup lineup)
    {
        // Unikamy dzielenia przez 0
        double midMe = Math.Max(1.0, me.Midfield);
        double midOpp = Math.Max(1.0, opp.Midfield);
        double possMe = midMe / (midMe + midOpp);
        double possOpp = 1 - possMe;

        double myAttAvg = (me.CentralAttack + me.RightAttack + me.LeftAttack) / 3.0;
        double oppDefAvg = Math.Max(1.0, (opp.CentralDefense + opp.RightDefense + opp.LeftDefense) / 3.0);
        double oppAttAvg = (opp.CentralAttack + opp.RightAttack + opp.LeftAttack) / 3.0;
        double myDefAvg = Math.Max(1.0, (me.CentralDefense + me.RightDefense + me.LeftDefense) / 3.0);

        double baseGoals = 1.45;                       // srednia liczba goli jednej druzyny
        double alpha = 1.8;                            // wykladnik przewagi ataku nad obrona
        double possFactor = 1.0 + 0.5 * (possMe - 0.5); // +- do 25% zaleznie od posiadania

        double lamMe = baseGoals * Math.Pow(myAttAvg / oppDefAvg, alpha) * possFactor;
        double lamOpp = baseGoals * Math.Pow(oppAttAvg / myDefAvg, alpha) * (2 - possFactor);

        // Kontratak daje bonus do oczekiwanych goli przy niskim posiadaniu
        if (tactic == "Counter" && possMe < 0.45)
        {
            lamMe *= 1.10;
        }

        // Long Shots: bonus jesli poziom LS co najmniej passable (srednia sc>5)
        if (tactic == "LongShots")
        {
            var ls = AverageScoring(lineup);
            if (ls > 7) lamMe *= FormationData.TacticModifiers.LongShotsGoalBonus;
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
        return (pWin, pDraw, pLoss, lamMe, lamOpp);
    }

    private static double Poisson(double lambda, int k)
    {
        if (lambda <= 0) return k == 0 ? 1.0 : 0.0;
        // log-space dla stabilnosci
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

    // ======================= Helpers =======================

    private double TeamStrength(AssignedLineup lineup)
    {
        var r = ComputeRatings(lineup);
        return 3 * r.Midfield + r.CentralDefense + r.RightDefense + r.LeftDefense
             + r.CentralAttack + r.RightAttack + r.LeftAttack;
    }

    private double AverageScoring(AssignedLineup lineup)
    {
        var outfield = lineup.Slots.Values.Where(s => s.SlotId != "GK").Select(s => s.Player.Skills.Scoring);
        if (!outfield.Any()) return 0;
        return outfield.Average();
    }

    private static bool IsCentralDefenderSlot(string slot) =>
        slot == "CD" || slot == "RCD" || slot == "LCD";

    private static bool IsInnerMidfielderSlot(string slot) =>
        slot == "IM" || slot == "RIM" || slot == "LIM";

    private static bool IsForwardSlot(string slot) =>
        slot == "FW" || slot == "RFW" || slot == "LFW" || slot == "CFW";

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
                Rating = ComputePlayerSlotRating(s.Value.Player, s.Key, s.Value.Behaviour)
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
        double eff = EffectiveMultiplier(player);
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

    private List<string> IdentifyStrengths(LineupRatings me, LineupRatings opp)
    {
        var r = new List<string>();
        if (me.Midfield > opp.Midfield * 1.15)
            r.Add("Przewaga w pomocy — wiecej szans na twoja druzyne. / Midfield advantage — more chances for your team.");
        if (me.CentralDefense > opp.CentralAttack * 1.15)
            r.Add("Mocna obrona centralna — przeciwnik bedzie mial trudno w srodku. / Strong central defense — opponent will struggle through the middle.");
        bool rightAdv = me.RightAttack > opp.LeftDefense * 1.15;
        bool leftAdv  = me.LeftAttack  > opp.RightDefense * 1.15;
        if (rightAdv && leftAdv)
            r.Add("Obie flanki silne wzgledem obrony przeciwnika — idealny moment na taktike AOW (Atak skrzydlami, wzmacnia oba skrzydla jednoczesnie). / Both flanks strong vs opponent defense — ideal for AOW (Attack on Wings, boosts both wings simultaneously).");
        else if (rightAdv)
            r.Add("Prawa flanka silna, jednak AOW wzmacnia oba skrzydla jednoczesnie — wzmocnij lewe skrzydlo, by w pelni wykorzystac te przewage. / Right flank strong, but AOW boosts both wings simultaneously — strengthen the left wing to fully exploit this advantage.");
        else if (leftAdv)
            r.Add("Lewa flanka silna, jednak AOW wzmacnia oba skrzydla jednoczesnie — wzmocnij prawe skrzydlo, by w pelni wykorzystac te przewage. / Left flank strong, but AOW boosts both wings simultaneously — strengthen the right wing to fully exploit this advantage.");
        return r;
    }

    private List<string> IdentifyWeaknesses(LineupRatings me, LineupRatings opp)
    {
        var r = new List<string>();
        if (me.Midfield < opp.Midfield * 0.9)
            r.Add("Slabsza pomoc — przeciwnik bedzie mial posiadanie. / Weak midfield — opponent will dominate possession.");
        if (me.CentralDefense < opp.CentralAttack * 0.9)
            r.Add("Zagrozenie w srodku obrony — wzmocnij. / Central defense under threat — reinforce it.");
        if (me.RightDefense < opp.LeftAttack * 0.9)
            r.Add("Prawa obrona pod presja — ryzyko straty z lewego ataku przeciwnika. / Right defense under pressure — risk from opponent's left attack.");
        if (me.LeftDefense < opp.RightAttack * 0.9)
            r.Add("Lewa obrona pod presja — ryzyko straty z prawego ataku przeciwnika. / Left defense under pressure — risk from opponent's right attack.");
        return r;
    }

    private List<string> GenerateRecommendations(OptimizationCandidate best, TeamComparison cmp)
    {
        var rec = new List<string>
        {
            $"Formacja: {best.Lineup.Formation.Name} ({best.Lineup.Formation.Description}). / Formation: {best.Lineup.Formation.Name} ({best.Lineup.Formation.Description}).",
            $"Taktyka: {TranslateTactic(best.Tactic)}.",
            $"Postawa druzyny: {TranslateAttitude(best.Attitude)}.",
            $"Trener: {TranslateCoach(best.Coach)}.",
            $"Szacowane prawdopodobienstwo: wygrana {best.WinProbability:P1}, remis {best.DrawProbability:P1}, porazka {best.LossProbability:P1}. / Estimated probability: win {best.WinProbability:P1}, draw {best.DrawProbability:P1}, loss {best.LossProbability:P1}.",
            $"Oczekiwany wynik (lambda): {best.ExpectedGoalsFor:F2}:{best.ExpectedGoalsAgainst:F2}. / Expected score (lambda): {best.ExpectedGoalsFor:F2}:{best.ExpectedGoalsAgainst:F2}."
        };
        if (best.DisorderRisk > 0.01)
        {
            rec.Add($"Ryzyko nieladu (niskie doswiadczenie formacji): {best.DisorderRisk:P0} — rozwaz czestsze granie ta formacja lub wybor formacji o wyzszym poziomie doswiadczenia. / Disorder risk (low formation experience): {best.DisorderRisk:P0} — consider playing this formation more often or choosing one with higher experience.");
        }

        var bhvSummary = best.Lineup.Slots
            .Where(kv => kv.Key != "GK")
            .Select(kv => $"{kv.Key}={kv.Value.Behaviour}")
            .ToList();
        rec.Add("Ustawienia per slot / Per-slot settings: " + string.Join(", ", bhvSummary));

        rec.AddRange(cmp.Strengths);
        rec.AddRange(cmp.Weaknesses);
        return rec;
    }

    private static string TranslateTactic(string t) => t switch
    {
        "Normal"         => "Zwykla / Normal",
        "Counter"        => "Kontratak / Counter-attack",
        "AttackInMiddle" => "Atak srodkiem (AIM) — wzmacnia centralny atak, oslabia oba skrzydla / Attack in the Middle (AIM) — boosts central attack, weakens both wings",
        "AttackOnWings"  => "Atak skrzydlami (AOW) — wzmacnia oba skrzydla jednoczesnie, oslabia centralny atak / Attack on Wings (AOW) — boosts both wings simultaneously, weakens central attack",
        "Pressing"       => "Pressing / Pressing",
        "PlayCreatively" => "Kreatywna gra / Play Creatively",
        "LongShots"      => "Strzaly z dystansu / Long Shots",
        _ => t
    };

    private static string TranslateAttitude(string a) => a switch
    {
        "PIC"  => "Gra na luzie (PIC) / Play It Cool (PIC)",
        "MOTS" => "Mecz sezonu (MOTS) / Match of the Season (MOTS)",
        _ => "Normalne spotkanie / Normal match"
    };

    private static string TranslateCoach(string c) => c switch
    {
        "Offensive" => "Ofensywny / Offensive",
        "Defensive" => "Defensywny / Defensive",
        _ => "Neutralny / Neutral"
    };
}

// ======================= Internal structures =======================

internal class AssignedSlot
{
    public string SlotId { get; set; } = "";
    public Player Player { get; set; } = null!;
    public string Behaviour { get; set; } = "";
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
