using HattrickAnalizer.Models;

namespace HattrickAnalizer.Services;

/// <summary>
/// Optymalizator skladu maksymalizujacy wybrany cel (Win domyslnie).
/// Iteruje po (formacja x taktyka) i dla kazdej kombinacji robi lokalna optymalizacje
/// przypisania graczy do slotow + zachowan (WBD/WBN/WBO/WBTM itp.).
/// Wynik jest oceniany wspolnym, znormalizowanym modelem Poissona.
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
        ValidateRequest(request);
        var myTeam = await _hattrickApi.GetTeamDetailsAsync(request.MyTeamId);
        // An explicit scenario is fully caller-supplied; otherwise use weighted scout ratings.
        var opponentResult = request.OpponentScenario?.Ratings is { } scenarioRatings
            ? new OpponentRatingsResult { Ratings = scenarioRatings, Source = "explicit scenario ratings" }
            : await _scout.GetWeightedRatingsAsync(request.OpponentTeamId);
        // Pogoda regionu gospodarza — tylko dla znanego nadchodzacego meczu.
        MatchWeather? weather = null;
        if (request.MatchId != 0)
        {
            try
            {
                var hostTeamId = request.IsHomeMatch ? request.MyTeamId : request.OpponentTeamId;
                weather = await _hattrickApi.GetMatchWeatherAsync(hostTeamId, request.MatchDate);
            }
            catch (Exception)
            {
                // Pogoda jest dodatkiem — optymalizacja dziala dalej bez niej.
            }
        }

        return OptimizeRoster(myTeam, opponentResult, request, weather);
    }

    /// <summary>Pure roster-to-lineup optimization entry point for deterministic replay and benchmarks.</summary>
    public OptimizerResponse OptimizeRoster(Team myTeam, OpponentRatingsResult opponentResult,
        OptimizerRequest request, MatchWeather? weather = null)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(myTeam);
        ArgumentNullException.ThrowIfNull(opponentResult);
        var opponentRatings = request.OpponentScenario?.Ratings ?? opponentResult.Ratings;
        var context = new MatchContext
        {
            IsHomeMatch = request.IsHomeMatch,
            WeatherId = weather?.WeatherId ?? RatingEngine.WeatherUnknown,
            OpponentIspAtt = opponentRatings.IndirectSetPiecesAttRating,
            OpponentIspDef = opponentRatings.IndirectSetPiecesDefRating
        };
        var available = myTeam.Players.Where(p => p.CanOptimize).ToList();
        if (available.Count < 11)
        {
            throw new InvalidOperationException($"Zbyt malo zdrowych graczy ({available.Count}) zeby ulozyc sklad.");
        }

        var tactics = ResolveTacticCandidates(request.PreferredTactic);
        var attitude = NormaliseAttitude(request.TeamAttitude);
        var coach = NormaliseCoachType(request.CoachType);
        var objective = request.Objective;
        context.TeamSpiritLevel = request.TeamSpiritLevel ?? myTeam.TeamSpiritLevel;
        context.ConfidenceLevel = request.ConfidenceLevel ?? myTeam.ConfidenceLevel;
        context.Objective = objective;
        context.OpponentTactic = request.OpponentScenario?.Tactic ?? "Normal";
        context.OpponentRatingsAreObserved = request.OpponentScenario is null && (opponentResult.Source is "scout" or "lastMatch");
        context.OpponentHomeBonusAlreadyPresent = request.OpponentScenario is null && opponentResult.Source == "lastMatch";

        OptimizationCandidate? best = null;
        var allCandidates = new List<OptimizationCandidate>();
        var formationsToTry = ResolveFormationCandidates(request.PreferredFormation);
        foreach (var formation in formationsToTry)
        {
            int formExp = request.FormationExperience.GetValueOrDefault(formation.Name, 5);
            double disorderRisk = ComputeDisorderRisk(formExp);
            OptimizationCandidate? bestForFormation = null;
            var keeperStarts = available.OrderByDescending(p => EffectiveSkill(p, p.Skills.Keeper))
                .ThenBy(p => p.PlayerId).Take(2).Select(p => p.PlayerId).ToList();
            foreach (var keeperId in keeperStarts)
            {
                var baseLineup = BuildInitialLineup(formation, available.OrderBy(p => p.PlayerId).ToList(), keeperId);
                if (baseLineup == null) continue;
                EnforceAssistantBehaviorLimit(baseLineup, request.AssistantManagerLevel);
                foreach (var tactic in tactics)
                {
                    var candidate = EvaluateCandidate(formation, tactic, attitude, coach, CloneLineup(baseLineup), opponentRatings, context, disorderRisk);
                    candidate = OptimiseBehaviours(candidate, attitude, coach, opponentRatings, context, disorderRisk, available, request.AssistantManagerLevel);
                    if (bestForFormation == null || candidate.Score > bestForFormation.Score) bestForFormation = candidate;
                    if (best == null || candidate.Score > best.Score) best = candidate;
                }
            }

            // Keep the complete existing two-keeper, three-pass result as a candidate. The
            // deterministic opponent-aware starts below can improve it, but never discard it.
            if (bestForFormation != null)
            {
                var incumbent = bestForFormation;
                var distinctStarts = BuildOpponentAwareSeeds(formation, available, opponentRatings,
                        request.AssistantManagerLevel)
                    .SelectMany(seed => tactics.Select(tactic => EvaluateCandidate(formation, tactic, attitude, coach,
                        seed, opponentRatings, context, disorderRisk)))
                    .GroupBy(c => AssignmentKey(c.Lineup), StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(c => c.Score)
                        .ThenBy(c => c.Tactic, StringComparer.Ordinal).First())
                    .OrderByDescending(c => c.Score).ThenBy(c => AssignmentKey(c.Lineup), StringComparer.Ordinal)
                    .Where(candidate => AssignmentKey(candidate.Lineup) != AssignmentKey(incumbent.Lineup))
                    .Take(2).ToList();
                distinctStarts.Insert(0, incumbent);
                foreach (var start in distinctStarts)
                {
                    var climbed = OptimiseBehaviours(start, attitude, coach, opponentRatings, context,
                        disorderRisk, available, request.AssistantManagerLevel);
                    var searched = SearchCandidateWithEscapes(climbed, opponentRatings, context, disorderRisk,
                        available, request.AssistantManagerLevel, maxEvaluations: 480);
                    if (searched.Score > bestForFormation.Score + 1e-9) bestForFormation = searched;
                    if (best == null || searched.Score > best.Score + 1e-9) best = searched;
                }
                bestForFormation = RefineJointBehaviours(bestForFormation, opponentRatings, context,
                    disorderRisk, request.AssistantManagerLevel, maxPasses: 8);
                if (best == null || bestForFormation.Score > best.Score + 1e-9) best = bestForFormation;
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
        var returnedEleven = AssignedFromReturnedLineup(lineup, requireEligiblePlayers: true);
        if (!WithinAssistantBehaviorLimit(returnedEleven, request.AssistantManagerLevel))
            throw new InvalidOperationException("The optimizer returned an XI above the assistant custom-order limit.");
        var returnedFormation = FormationData.Formations[lineup.Formation!];
        best = EvaluateCandidate(returnedFormation, best.Tactic, best.Attitude, best.Coach,
            returnedEleven, opponentRatings, context, best.DisorderRisk);
        lineup = BuildFinalLineup(best);
        var oppLineupRatings = best.OpponentRatings;

        var lang = request.Language ?? "pl";

        var comparison = new TeamComparison
        {
            MyTeamRatings = best.Ratings,
            OpponentRatings = oppLineupRatings,
            Strengths = IdentifyStrengths(best.Ratings, oppLineupRatings, lang),
            Weaknesses = IdentifyWeaknesses(best.Ratings, oppLineupRatings, lang)
        };

        var recommendations = GenerateRecommendations(best, comparison, lang);
        var inputSnapshot = CloneRequest(request);
        inputSnapshot.TeamSpiritLevel = context.TeamSpiritLevel;
        inputSnapshot.ConfidenceLevel = context.ConfidenceLevel;

        var alternatives = allCandidates
            .OrderByDescending(c => c.Score).ThenBy(c => c.Lineup.Formation.Name, StringComparer.Ordinal).ThenBy(c => c.Tactic, StringComparer.Ordinal)
            .Select(c => new FormationAlternative
            {
                Formation = c.Lineup.Formation.Name,
                Tactic = c.Tactic,
                Attitude = c.Attitude,
                Lineup = BuildFinalLineup(c),
                WinProbability = c.WinProbability,
                DrawProbability = c.DrawProbability,
                LossProbability = c.LossProbability,
                ExpectedGoalsFor = c.ExpectedGoalsFor,
                ExpectedGoalsAgainst = c.ExpectedGoalsAgainst,
                ExpectedPoints = 3 * c.WinProbability + c.DrawProbability,
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
            Weather = weather,
            InputSnapshot = inputSnapshot,
            GeneratedAt = DateTimeOffset.UtcNow,
            OwnTeamProvenance = ResolveOwnTeamProvenance(myTeam),
            OpponentProvenance = new DataProvenance
            {
                Source = request.OpponentScenario?.Ratings is not null ? "explicit scenario ratings" : opponentResult.Source,
                RetrievedAt = opponentResult.SourceMatchDate is DateTime sourceDate ? new DateTimeOffset(sourceDate) : null,
                Warnings = opponentResult.Warnings.Concat(
                    request.OpponentScenario?.Ratings is null && opponentResult.Source == "default"
                        ? new[] { "Opponent sector ratings are an estimate; hidden player skills are unavailable." }
                        : Array.Empty<string>()).Concat(
                    request.OpponentScenario?.Ratings is null && opponentResult.Source == "lastMatch"
                        ? new[] { "Historical home status is unavailable for this fallback rating source; its midfield may already include home advantage." }
                        : Array.Empty<string>()).ToList()
            },
            ModelVersion = MatchPredictionModel.Version,
            ModelConfidence = "unvalidated-low",
            ModelWarnings = BuildModelWarnings(request, opponentResult.Source, context.TeamSpiritLevel,
                context.ConfidenceLevel, best.Lineup.Formation.Name)
        };
    }

    /// <summary>Returns the first legal greedy seed used by the same production search path.</summary>
    public Lineup CreateLegalInitialSeed(Team team, TeamRatings opponentRatings, OptimizerRequest request,
        int weatherId = RatingEngine.WeatherUnknown)
    {
        ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(opponentRatings);
        var available = team.Players.Where(p => p.CanOptimize).OrderBy(p => p.PlayerId).ToList();
        if (available.Count < 11) throw new InvalidOperationException("At least eleven eligible players are required.");
        var formation = ResolveFormationCandidates(request.PreferredFormation).First();
        var keeperId = available.OrderByDescending(p => EffectiveSkill(p, p.Skills.Keeper))
            .ThenBy(p => p.PlayerId).Take(2).Select(p => p.PlayerId).First();
        var assigned = BuildInitialLineup(formation, available, keeperId)
            ?? throw new InvalidOperationException("Could not build the legal greedy seed.");
        EnforceAssistantBehaviorLimit(assigned, request.AssistantManagerLevel);
        var tactic = ResolveTacticCandidates(request.PreferredTactic).First();
        var context = new MatchContext
        {
            IsHomeMatch = request.IsHomeMatch, WeatherId = weatherId,
            TeamSpiritLevel = request.TeamSpiritLevel ?? team.TeamSpiritLevel,
            ConfidenceLevel = request.ConfidenceLevel ?? team.ConfidenceLevel,
            OpponentIspAtt = opponentRatings.IndirectSetPiecesAttRating,
            OpponentIspDef = opponentRatings.IndirectSetPiecesDefRating,
            OpponentTactic = request.OpponentScenario?.Tactic ?? "Normal"
        };
        var candidate = EvaluateCandidate(formation, tactic, NormaliseAttitude(request.TeamAttitude),
            NormaliseCoachType(request.CoachType), assigned, opponentRatings, context,
            ComputeDisorderRisk(request.FormationExperience.GetValueOrDefault(formation.Name, 5)));
        return BuildFinalLineup(candidate);
    }

    private static void ValidateRequest(OptimizerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MyTeamId <= 0 || request.OpponentTeamId <= 0) throw new ArgumentException("Team IDs must be positive.");
        if (request.AssistantManagerLevel is < 0 or > 5) throw new ArgumentException("AssistantManagerLevel must be 0..5.");
        if (request.TeamSpiritLevel is < 0 or > 10 || request.ConfidenceLevel is < 0 or > 10) throw new ArgumentException("TeamSpiritLevel and ConfidenceLevel must be 0..10.");
        if (request.Objective is not ("ExpectedPoints" or "Win" or "Draw")) throw new ArgumentException("Objective must be ExpectedPoints, Win, or Draw.");
        if (request.TeamAttitude is not ("Normal" or "PIC" or "MOTS")) throw new ArgumentException("TeamAttitude must be Normal, PIC, or MOTS.");
        if (request.CoachType is not ("Neutral" or "Offensive" or "Defensive")) throw new ArgumentException("CoachType must be Neutral, Offensive, or Defensive.");
        if (!string.IsNullOrWhiteSpace(request.PreferredTactic) && request.PreferredTactic != "Auto" && !AllTactics.Contains(request.PreferredTactic)) throw new ArgumentException("PreferredTactic is invalid.");
        if (!string.IsNullOrWhiteSpace(request.PreferredFormation) && request.PreferredFormation != "Auto" && !FormationData.Formations.ContainsKey(request.PreferredFormation)) throw new ArgumentException("PreferredFormation is invalid.");
        if (request.FormationExperience is null || request.FormationExperience.Values.Any(level => level is < 3 or > 10)) throw new ArgumentException("FormationExperience levels must be 3..10.");
        if (request.FocusAreas is null) throw new ArgumentException("FocusAreas must not be null.");
        if (request.OpponentScenario is { Ratings: null }) throw new ArgumentException("OpponentScenario requires explicit Ratings.");
        if (request.OpponentScenario?.Ratings is { } ratings)
        {
            var sectors = new[] { ratings.MidfieldRating, ratings.RightDefenseRating, ratings.CentralDefenseRating,
                ratings.LeftDefenseRating, ratings.RightAttackRating, ratings.CentralAttackRating, ratings.LeftAttackRating };
            var setPieces = new[] { ratings.IndirectSetPiecesAttRating, ratings.IndirectSetPiecesDefRating };
            if (sectors.Any(x => !double.IsFinite(x) || x <= 0 || x > 1_000_000) ||
                setPieces.Any(x => !double.IsFinite(x) || x < 0 || x > 1_000_000))
                throw new ArgumentException("Opponent scenario ratings must be finite and at most 1,000,000, with positive sectors and non-negative set pieces.");
        }
        if (request.OpponentScenario?.Tactic is { } tactic && !AllTactics.Contains(tactic)) throw new ArgumentException("OpponentScenario.Tactic is invalid.");
    }

    private static OptimizerRequest CloneRequest(OptimizerRequest request) => new()
    {
        MyTeamId = request.MyTeamId, OpponentTeamId = request.OpponentTeamId,
        PreferredTactic = request.PreferredTactic, TeamAttitude = request.TeamAttitude,
        FocusAreas = request.FocusAreas.ToList(), CoachType = request.CoachType,
        AssistantManagerLevel = request.AssistantManagerLevel, TeamSpiritLevel = request.TeamSpiritLevel,
        ConfidenceLevel = request.ConfidenceLevel, Objective = request.Objective,
        OpponentScenario = request.OpponentScenario is null ? null : new OpponentScenario
        {
            Tactic = request.OpponentScenario.Tactic,
            Ratings = request.OpponentScenario.Ratings is null ? null : new TeamRatings
            {
                MidfieldRating = request.OpponentScenario.Ratings.MidfieldRating,
                RightDefenseRating = request.OpponentScenario.Ratings.RightDefenseRating,
                CentralDefenseRating = request.OpponentScenario.Ratings.CentralDefenseRating,
                LeftDefenseRating = request.OpponentScenario.Ratings.LeftDefenseRating,
                RightAttackRating = request.OpponentScenario.Ratings.RightAttackRating,
                CentralAttackRating = request.OpponentScenario.Ratings.CentralAttackRating,
                LeftAttackRating = request.OpponentScenario.Ratings.LeftAttackRating,
                IndirectSetPiecesAttRating = request.OpponentScenario.Ratings.IndirectSetPiecesAttRating,
                IndirectSetPiecesDefRating = request.OpponentScenario.Ratings.IndirectSetPiecesDefRating
            }
        },
        FormationExperience = new Dictionary<string, int>(request.FormationExperience),
        PreferredFormation = request.PreferredFormation, Language = request.Language,
        MatchId = request.MatchId, IsHomeMatch = request.IsHomeMatch, MatchDate = request.MatchDate
    };

    private static DataProvenance ResolveOwnTeamProvenance(Team team)
    {
        if (team.Provenance.Source != "unknown") return team.Provenance;
        var players = team.Players.Select(p => p.Provenance).ToList();
        var sources = players.Select(p => p.Source).Where(s => !string.IsNullOrWhiteSpace(s) && s != "unknown").Distinct(StringComparer.Ordinal).ToList();
        return new DataProvenance
        {
            Source = sources.Count == 0 ? "unknown" : string.Join(", ", sources),
            RetrievedAt = players.Where(p => p.RetrievedAt.HasValue).Select(p => p.RetrievedAt).Max(),
            Warnings = players.SelectMany(p => p.Warnings).Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static List<string> BuildModelWarnings(OptimizerRequest request, string source, int? spirit,
        int? confidence, string selectedFormation)
    {
        var warnings = new List<string>
        {
            "Approximation details: AIM/AOW route chances use passing and official route ranges; pressing uses defending, stamina, and Powerful's doubled defending contribution; counter quality omits opponent Quick-specialty suppression; long shots use own scoring/set pieces but lack opponent keeper and set-piece skills/shooter weighting; Play Creatively's special-event generation is not modeled.",
            "Spirit adjusts midfield by 1.5% per level from midpoint 5; confidence adjusts attacks by 1% per level from midpoint 5. Assistant level only changes the legal custom-behavior limit (5 + level); it does not multiply ratings, and coach style has no continuous input.",
            "Formation experience below level 8 uses an approximate expected disorder penalty; level 8 and above uses zero risk.",
            "The model is unvalidated as a real-match forecast."
        };
        if (spirit is null) warnings.Add("Team spirit was not available; a neutral midpoint approximation was used.");
        if (confidence is null) warnings.Add("Confidence was not available; a neutral midpoint approximation was used.");
        if (request.OpponentScenario?.Ratings is null && source == "default") warnings.Add("Opponent ratings came from a default estimate; opponent skills are unknown.");
        if (request.OpponentScenario?.Tactic is { } opponentTactic && opponentTactic != "Normal") warnings.Add("Opponent tactic skill and specialist effects use conservative defaults because opponent player skills were not supplied; available route and sector effects are applied.");
        if (source == "scout") warnings.Add("Opponent scout ratings aggregate an unknown historical tactic mix; no single historic sector penalty is inverted or reapplied to avoid inventing a precise correction.");
        if (request.OpponentScenario?.Tactic == "LongShots") warnings.Add("Opponent Long Shots quality is unknown because opponent shooter and goalkeeper skills were not supplied.");
        if (source == "lastMatch") warnings.Add("Fallback ratings are one observed match with unknown home and tactic context; future home and historical tactical penalties cannot be separated reliably.");
        if (!request.FormationExperience.ContainsKey(selectedFormation))
            warnings.Add($"Formation experience for {selectedFormation} was unavailable; the optimizer used its level-5 disorder estimate.");
        if (request.PreferredTactic is "LongShots" or "Auto") warnings.Add("Long Shots uses own shooting skills without opponent goalkeeper/set-piece ratings; converted-shot quality is a low-confidence proxy.");
        if (request.PreferredTactic == "Auto") warnings.Add("Play Creatively is excluded from Auto ranking because specialist-generated special events are not modeled.");
        if (request.PreferredTactic == "PlayCreatively") warnings.Add("Play Creatively is manually evaluated with its defense cost, but specialist-generated special events are not modeled; its ranking is incomplete.");
        return warnings;
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
            return AllTactics.Where(t => t != "PlayCreatively");
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

    // Co najmniej excellent (8) experience is treated as zero risk; lower levels are a local approximation.
    public static double ComputeDisorderRisk(int formationExperience)
    {
        int e = Math.Clamp(formationExperience, 3, 10);
        return e switch
        {
            >= 8 => 0.00,
            7 => 0.10,
            6 => 0.18,
            5 => 0.28,
            4 => 0.40,
            _ => 0.55  // 3 = kiepskie
        };
    }

    // ======================= Assignment =======================

    private AssignedLineup? BuildInitialLineup(FormationDefinition formation, List<Player> available,
        int? keeperId = null, RatingWeights? weights = null, int slotOrderVariant = 0)
    {
        var slots = formation.Positions.ToList();
        var players = available.ToList();

        // GK: najlepszy wg keeper-score
        var gk = keeperId.HasValue ? players.FirstOrDefault(p => p.PlayerId == keeperId.Value) : players.OrderByDescending(p => EffectiveSkill(p, p.Skills.Keeper)).FirstOrDefault();
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
                d[p.PlayerId] = BestBehaviourScore(slot, p, weights, out _);
            }
            slotScores[slot] = d;
        }

        // Uporzadkuj sloty wg wariancji scoringu (wieksza wariancja = wazniejszy wybor).
        var slotOrder = slotOrderVariant == 0
            ? outfieldSlots.OrderByDescending(s => Variance(slotScores[s].Values)).ToList()
            : outfieldSlots.OrderByDescending(s => SeedSlotPriority(s, slotOrderVariant))
                .ThenByDescending(s => Variance(slotScores[s].Values)).ThenBy(s => s, StringComparer.Ordinal).ToList();

        foreach (var slot in slotOrder)
        {
            var best = remaining
                .OrderByDescending(p => slotScores[slot][p.PlayerId])
                .FirstOrDefault();
            if (best == null) break;
            _ = BestBehaviourScore(slot, best, weights, out var bhv);
            result.Slots[slot] = new AssignedSlot { SlotId = slot, Player = best, Behaviour = bhv };
            remaining.Remove(best);
        }

        if (result.Slots.Count < slots.Count) return null;
        return result;
    }

    private List<AssignedLineup> BuildOpponentAwareSeeds(FormationDefinition formation,
        List<Player> available, TeamRatings opponent, int assistantManagerLevel)
    {
        var attackAverage = (opponent.RightAttackRating + opponent.CentralAttackRating + opponent.LeftAttackRating) / 3.0;
        var defenseAverage = (opponent.RightDefenseRating + opponent.CentralDefenseRating + opponent.LeftDefenseRating) / 3.0;
        double Relative(double value, double average) => average <= 0 ? 1 : Math.Clamp(value / average, 0.65, 1.35);
        double Weakness(double value, double average) => average <= 0 ? 1 : Math.Clamp(average / value, 0.65, 1.35);
        var opponentAware = new RatingWeights
        {
            Midfield = 1.0,
            CentralDefense = Relative(opponent.CentralAttackRating, attackAverage),
            RightDefense = Relative(opponent.LeftAttackRating, attackAverage),
            LeftDefense = Relative(opponent.RightAttackRating, attackAverage),
            CentralAttack = Weakness(opponent.CentralDefenseRating, defenseAverage),
            RightAttack = Weakness(opponent.LeftDefenseRating, defenseAverage),
            LeftAttack = Weakness(opponent.RightDefenseRating, defenseAverage)
        };
        var profiles = new[]
        {
            opponentAware,
            new RatingWeights
            {
                Midfield = opponentAware.Midfield, CentralDefense = opponentAware.CentralDefense,
                RightDefense = opponentAware.RightDefense, LeftDefense = opponentAware.LeftDefense,
                CentralAttack = 0.75, RightAttack = 1.25, LeftAttack = 1.25
            },
            new RatingWeights
            {
                Midfield = opponentAware.Midfield, CentralDefense = 1.25 * opponentAware.CentralDefense,
                RightDefense = 1.25 * opponentAware.RightDefense, LeftDefense = 1.25 * opponentAware.LeftDefense,
                CentralAttack = 1.45, RightAttack = 0.78, LeftAttack = 0.78
            },
            new RatingWeights
            {
                Midfield = 1.5,
                CentralDefense = opponentAware.CentralDefense, RightDefense = opponentAware.RightDefense,
                LeftDefense = opponentAware.LeftDefense, CentralAttack = opponentAware.CentralAttack,
                RightAttack = opponentAware.RightAttack, LeftAttack = opponentAware.LeftAttack
            },
            new RatingWeights
            {
                Midfield = opponentAware.Midfield, CentralDefense = opponentAware.CentralDefense,
                RightDefense = opponentAware.RightDefense, LeftDefense = opponentAware.LeftDefense,
                CentralAttack = 0.82 * opponentAware.CentralAttack,
                RightAttack = 1.45 * opponentAware.RightAttack, LeftAttack = 0.82 * opponentAware.LeftAttack
            },
            new RatingWeights
            {
                Midfield = opponentAware.Midfield, CentralDefense = opponentAware.CentralDefense,
                RightDefense = opponentAware.RightDefense, LeftDefense = opponentAware.LeftDefense,
                CentralAttack = 0.82 * opponentAware.CentralAttack,
                RightAttack = 0.82 * opponentAware.RightAttack, LeftAttack = 1.45 * opponentAware.LeftAttack
            }
        };
        var players = available.OrderBy(p => p.PlayerId).ToList();
        var seeds = new List<AssignedLineup>();
        for (var profileIndex = 0; profileIndex < profiles.Length; profileIndex++)
        {
            var profile = profiles[profileIndex];
            var keeperWeights = new RatingWeights
            {
                CentralDefense = profile.CentralDefense,
                RightDefense = profile.RightDefense,
                LeftDefense = profile.LeftDefense
            };
            var keeperIds = players.OrderByDescending(p => PlayerContributionScore(p, "GK",
                    FormationData.PositionContributions["GK"], keeperWeights))
                .ThenBy(p => p.PlayerId).Take(2).Select(p => p.PlayerId);
            foreach (var keeperId in keeperIds)
            {
                var seed = BuildInitialLineup(formation, players, keeperId, profile, profileIndex % 4);
                if (seed == null) continue;
                EnforceAssistantBehaviorLimit(seed, assistantManagerLevel);
                if (WithinAssistantBehaviorLimit(seed, assistantManagerLevel)
                    && !seeds.Any(existing => AssignmentKey(existing) == AssignmentKey(seed))) seeds.Add(seed);

                foreach (var beamSeed in BuildBeamSeeds(formation, players, profile,
                    keeperId, assistantManagerLevel, profileIndex % 4, width: 8))
                {
                    if (!seeds.Any(existing => AssignmentKey(existing) == AssignmentKey(beamSeed))) seeds.Add(beamSeed);
                }
            }
        }
        return seeds;
    }

    private List<AssignedLineup> BuildBeamSeeds(FormationDefinition formation, List<Player> players,
        RatingWeights weights, int keeperId, int assistantManagerLevel,
        int priorityVariant, int width)
    {
        var keeper = players.FirstOrDefault(p => p.PlayerId == keeperId);
        if (keeper is null) return new List<AssignedLineup>();
        var cap = 5 + assistantManagerLevel;
        var initial = new SeedBeamState
        {
            Slots = new Dictionary<string, AssignedSlot>(StringComparer.Ordinal)
            {
                ["GK"] = new AssignedSlot { SlotId = "GK", Player = keeper, Behaviour = "GK" }
            },
            UsedPlayerIds = new HashSet<int> { keeperId },
            CustomBehaviors = 0,
            Score = PlayerContributionScore(keeper, "GK", FormationData.PositionContributions["GK"], weights)
        };
        var outfieldSlots = formation.Positions.Where(s => s != "GK")
            .OrderByDescending(s => SeedSlotPriority(s, priorityVariant))
            .ThenBy(s => Variance(players.Select(p => BestBehaviourScore(s, p, weights, out _))))
            .ThenBy(s => s, StringComparer.Ordinal).ToArray();
        var beam = new List<SeedBeamState> { initial };
        foreach (var slot in outfieldSlots)
        {
            var expanded = new List<SeedBeamState>();
            foreach (var state in beam)
            foreach (var player in players)
            {
                if (state.UsedPlayerIds.Contains(player.PlayerId)) continue;
                foreach (var behavior in BehavioursFor(slot).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var customCount = state.CustomBehaviors + (behavior == slot ? 0 : 1);
                    if (customCount > cap || !FormationData.PositionContributions.TryGetValue(behavior, out var contribution)) continue;
                    var slotScore = PlayerContributionScore(player, slot, contribution, weights);
                    var slots = new Dictionary<string, AssignedSlot>(state.Slots, StringComparer.Ordinal)
                    {
                        [slot] = new AssignedSlot { SlotId = slot, Player = player, Behaviour = behavior }
                    };
                    var used = new HashSet<int>(state.UsedPlayerIds) { player.PlayerId };
                    expanded.Add(new SeedBeamState
                    {
                        Slots = slots, UsedPlayerIds = used, CustomBehaviors = customCount,
                        Score = state.Score + slotScore
                    });
                }
            }
            beam = expanded.OrderByDescending(s => s.Score).ThenBy(SeedBeamKey, StringComparer.Ordinal)
                .Take(width).ToList();
            if (beam.Count == 0) break;
        }
        return beam.Where(s => s.Slots.Count == 11)
            .OrderByDescending(s => s.Score).ThenBy(SeedBeamKey, StringComparer.Ordinal)
            .Select(s => new AssignedLineup { Formation = formation, Slots = s.Slots })
            .ToList();
    }

    private static string SeedBeamKey(SeedBeamState state) => string.Join("|", state.Slots
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key}:{kv.Value.Player.PlayerId}:{kv.Value.Behaviour}"));

    private static int SeedSlotPriority(string slot, int variant)
    {
        var isAttack = slot is "FW" or "CFW" or "RFW" or "LFW" or "RW" or "LW";
        var isMidfield = slot is "IM" or "RIM" or "LIM";
        return variant switch
        {
            1 => isAttack ? 3 : isMidfield ? 2 : 1,
            2 => RatingEngine.IsDefenderSlot(slot) ? 3 : isMidfield ? 2 : 1,
            _ => isMidfield ? 3 : isAttack ? 2 : 1
        };
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
        var ratings = _engine.ComputeContextualRatings(ToLineup(lineup, includeIndividualRatings: false), context.WeatherId, attitude, coach,
            disorderRisk, context.TeamSpiritLevel, context.ConfidenceLevel);
        var opp = ConvertToLineupRatings(opponent);

        var (myIspAtt, myIspDef) = _engine.ComputeIndirectSetPieces(lineup);
        var ownSide = new MatchTacticalSide
        {
            Ratings = ratings, Tactic = tactic, IsHome = context.IsHomeMatch,
            IndirectSetPieceAttack = myIspAtt, IndirectSetPieceDefense = myIspDef,
            PassingSkillTotal = lineup.Slots.Values.Where(s => s.SlotId != "GK").Sum(s => RatingEngine.EffSkill(s.Player, s.Player.Skills.Passing)),
            CounterConversion = CalculateCounterConversion(lineup),
            PressingSuppression = CalculatePressingSuppression(lineup),
            LongShotGoalProbability = CalculateLongShotQuality(lineup)
        };
        var opponentSide = new MatchTacticalSide
        {
            Ratings = opp, Tactic = context.OpponentTactic, IsHome = !context.IsHomeMatch,
            HomeAdvantageAlreadyApplied = context.OpponentHomeBonusAlreadyPresent,
            RatingsIncludeHistoricalTacticEffects = context.OpponentRatingsAreObserved,
            ApplyCurrentTacticRatingEffects = !context.OpponentRatingsAreObserved,
            HistoricalTactic = context.OpponentHistoricalTactic,
            IndirectSetPieceAttack = context.OpponentIspAtt, IndirectSetPieceDefense = context.OpponentIspDef
        };
        // PredictOutcome accepts home/away ordering; ownSide is not necessarily the home side.
        var prediction = context.IsHomeMatch
            ? _engine.PredictOutcome(ownSide, opponentSide, meIsHome: true)
            : _engine.PredictOutcome(opponentSide, ownSide, meIsHome: false);
        ratings = prediction.MyRatings;

        return new OptimizationCandidate
        {
            Lineup = CloneLineup(lineup),
            Tactic = tactic,
            Attitude = attitude,
            Coach = coach,
            Ratings = ratings,
            WinProbability = prediction.WinProbability,
            DrawProbability = prediction.DrawProbability,
            LossProbability = prediction.LossProbability,
            ExpectedGoalsFor = prediction.ExpectedGoalsFor,
            ExpectedGoalsAgainst = prediction.ExpectedGoalsAgainst,
            OpponentRatings = prediction.OpponentRatings,
            DisorderRisk = disorderRisk,
            ExpectedPoints = 3 * prediction.WinProbability + prediction.DrawProbability,
            Score = ObjectiveScore(prediction.WinProbability, prediction.DrawProbability, context.Objective)
        };
    }

    private static double? CalculateCounterConversion(AssignedLineup lineup)
    {
        var defenders = lineup.Slots.Values.Where(s => RatingEngine.IsDefenderSlot(s.SlotId)).Select(s => s.Player).ToList();
        return defenders.Count == 0 ? null : Math.Clamp(defenders.Average(p =>
            (2 * RatingEngine.EffSkill(p, p.Skills.Passing) + RatingEngine.EffSkill(p, p.Skills.Defending)) / 3.0 / 35.0), 0.05, 0.35);
    }

    private static double? CalculatePressingSuppression(AssignedLineup lineup)
    {
        var outfield = lineup.Slots.Values.Where(s => s.SlotId != "GK").Select(s => s.Player).ToList();
        return outfield.Count == 0 ? null : Math.Clamp(0.08 + outfield.Average(p =>
            RatingEngine.EffSkill(p, p.Skills.Defending) * (IsPowerful(p) ? 2 : 1) * RatingEngine.StaminaEffect(p.Stamina)) * 0.012, 0.10, 0.30);
    }

    private static double? CalculateLongShotQuality(AssignedLineup lineup)
    {
        var outfield = lineup.Slots.Values.Where(s => s.SlotId != "GK").Select(s => s.Player).ToList();
        if (outfield.Count == 0) return null;
        var level = FormationData.TacticModifiers.CalculateLongShotsLevel(
            outfield.Average(p => RatingEngine.EffSkill(p, p.Skills.Scoring)),
            outfield.Average(p => RatingEngine.EffSkill(p, p.Skills.SetPieces)));
        return Math.Clamp(level / 25.0, 0, 0.30);
    }

    private static bool IsPowerful(Player player) => player.Specialty is "3" or "Powerful";

    private OptimizationCandidate OptimiseBehaviours(OptimizationCandidate seed, string attitude, string coach, TeamRatings opponent, MatchContext context, double disorderRisk, List<Player> available, int assistantManagerLevel)
    {
        var current = EvaluateCandidate(seed.Lineup.Formation, seed.Tactic, attitude, coach, seed.Lineup, opponent, context, disorderRisk);
        var playersById = available.OrderBy(p => p.PlayerId).ToList();
        for (int pass = 0; pass < 3; pass++)
        {
            bool improved = false;
            foreach (var slot in current.Lineup.Slots.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList())
            {
                var incumbent = current;
                foreach (var behaviour in BehavioursFor(slot))
                {
                    if (behaviour == incumbent.Lineup.Slots[slot].Behaviour) continue;
                    var trialLineup = CloneLineup(incumbent.Lineup);
                    trialLineup.Slots[slot].Behaviour = behaviour;
                    if (!WithinAssistantBehaviorLimit(trialLineup, assistantManagerLevel)) continue;
                    var trial = EvaluateCandidate(incumbent.Lineup.Formation, incumbent.Tactic, attitude, coach, trialLineup, opponent, context, disorderRisk);
                    if (trial.Score > current.Score + 1e-9) { current = trial; improved = true; }
                }
            }

            // Pairwise role swaps include GK, and reselect both affected roles before scoring.
            var slots = current.Lineup.Slots.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            for (int i = 0; i < slots.Count; i++)
            for (int j = i + 1; j < slots.Count; j++)
            {
                var slotA = slots[i]; var slotB = slots[j];
                var incumbent = current;
                var playerA = incumbent.Lineup.Slots[slotA].Player;
                var playerB = incumbent.Lineup.Slots[slotB].Player;
                foreach (var behaviourA in BehavioursFor(slotA))
                foreach (var behaviourB in BehavioursFor(slotB))
                {
                    var trialLineup = CloneLineup(incumbent.Lineup);
                    trialLineup.Slots[slotA] = new AssignedSlot { SlotId = slotA, Player = playerB, Behaviour = behaviourA };
                    trialLineup.Slots[slotB] = new AssignedSlot { SlotId = slotB, Player = playerA, Behaviour = behaviourB };
                    if (!WithinAssistantBehaviorLimit(trialLineup, assistantManagerLevel)) continue;
                    var trial = EvaluateCandidate(incumbent.Lineup.Formation, incumbent.Tactic, attitude, coach, trialLineup, opponent, context, disorderRisk);
                    if (trial.Score > current.Score + 1e-9) { current = trial; improved = true; }
                }
            }

            // Bench replacements are evaluated with every legal behaviour for the receiving slot.
            foreach (var slot in current.Lineup.Slots.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList())
            foreach (var player in playersById)
            {
                var incumbent = current;
                if (incumbent.Lineup.Slots.Values.Any(s => s.Player.PlayerId == player.PlayerId)) continue;
                foreach (var behaviour in BehavioursFor(slot))
                {
                    var trialLineup = CloneLineup(incumbent.Lineup);
                    trialLineup.Slots[slot] = new AssignedSlot { SlotId = slot, Player = player, Behaviour = behaviour };
                    if (!WithinAssistantBehaviorLimit(trialLineup, assistantManagerLevel)) continue;
                    var trial = EvaluateCandidate(incumbent.Lineup.Formation, incumbent.Tactic, attitude, coach, trialLineup, opponent, context, disorderRisk);
                    if (trial.Score > current.Score + 1e-9) { current = trial; improved = true; }
                }
            }
            if (!improved) break;
        }
        // Final reevaluation keeps returned ratings and probabilities tied to the exact candidate XI.
        return EvaluateCandidate(current.Lineup.Formation, current.Tactic, attitude, coach, current.Lineup, opponent, context, disorderRisk);
    }

    private OptimizationCandidate SearchCandidateWithEscapes(OptimizationCandidate seed, TeamRatings opponent,
        MatchContext context, double disorderRisk, List<Player> available, int assistantManagerLevel,
        int maxEvaluations)
    {
        var budget = new SearchBudget(maxEvaluations);
        var current = seed;
        var bestSoFar = seed;
        for (var round = 0; round < 2 && budget.Remaining > 0; round++)
        {
            var incumbent = current;
            var slots = current.Lineup.Slots.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();

            // Single and paired same-player behavior edits. Pair edits let a custom order
            // release one scarce slot and spend it on a stronger behavior elsewhere.
            Explore(candidate => current = candidate, current, budget, 64, emit =>
            {
                foreach (var slot in slots)
                foreach (var behavior in BehavioursFor(slot).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (behavior == current.Lineup.Slots[slot].Behaviour) continue;
                    var trial = CopyLineup(current.Lineup);
                    trial.Slots[slot].Behaviour = behavior;
                    if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                }
                for (var i = 0; i < slots.Length; i++)
                for (var j = i + 1; j < slots.Length; j++)
                foreach (var behaviorA in BehavioursFor(slots[i]).OrderBy(x => x, StringComparer.Ordinal))
                foreach (var behaviorB in BehavioursFor(slots[j]).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (behaviorA == current.Lineup.Slots[slots[i]].Behaviour
                        && behaviorB == current.Lineup.Slots[slots[j]].Behaviour) continue;
                    var trial = CopyLineup(current.Lineup);
                    trial.Slots[slots[i]].Behaviour = behaviorA;
                    trial.Slots[slots[j]].Behaviour = behaviorB;
                    if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                }
            }, opponent, context, disorderRisk, assistantManagerLevel);

            if (budget.Remaining > 0)
            {
            Explore(candidate => current = candidate, current, budget, 144, emit =>
                {
                    for (var i = 0; i < slots.Length; i++)
                    for (var j = i + 1; j < slots.Length; j++)
                    {
                        var trial = CopyLineup(current.Lineup);
                        (trial.Slots[slots[i]].Player, trial.Slots[slots[j]].Player) =
                            (current.Lineup.Slots[slots[j]].Player, current.Lineup.Slots[slots[i]].Player);
                        if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                    }
                    var selectedIds = current.Lineup.Slots.Values.Select(s => s.Player.PlayerId).ToHashSet();
                    foreach (var slot in slots)
                    foreach (var player in available.OrderBy(p => p.PlayerId))
                    {
                        if (selectedIds.Contains(player.PlayerId)) continue;
                        foreach (var behavior in BehavioursFor(slot).OrderBy(x => x, StringComparer.Ordinal))
                        {
                            var trial = CopyLineup(current.Lineup);
                            trial.Slots[slot].Player = player;
                            trial.Slots[slot].Behaviour = behavior;
                            if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                        }
                    }
                }, opponent, context, disorderRisk, assistantManagerLevel);
            }

            if (current.Score > bestSoFar.Score + 1e-9) bestSoFar = current;
            if (current.Score <= incumbent.Score + 1e-9) break;
        }

        // Deterministic perturb-and-repair escapes cross shallow local maxima. Each kick
        // makes a legal coupled role/behavior change, then spends the remaining fixed budget
        // climbing from that state; only a better repaired result replaces the incumbent.
        for (var kick = 0; kick < 3 && budget.Remaining > 0; kick++)
        {
            var shaken = CreateEscapeCandidate(bestSoFar, available, assistantManagerLevel, kick);
            if (shaken == null) continue;
            var kicked = EvaluateCandidate(shaken.Formation, bestSoFar.Tactic, bestSoFar.Attitude, bestSoFar.Coach,
                shaken, opponent, context, disorderRisk);
            budget.Use();
            var repaired = SearchCandidateWithBudget(kicked, opponent, context, disorderRisk,
                available, assistantManagerLevel, budget, perRound: 48);
            if (repaired.Score > bestSoFar.Score + 1e-9) bestSoFar = repaired;
        }
        return EvaluateCandidate(bestSoFar.Lineup.Formation, bestSoFar.Tactic,
            bestSoFar.Attitude, bestSoFar.Coach, bestSoFar.Lineup, opponent, context, disorderRisk);
    }

    private OptimizationCandidate SearchCandidateWithBudget(OptimizationCandidate start, TeamRatings opponent,
        MatchContext context, double disorderRisk, List<Player> available, int assistantManagerLevel,
        SearchBudget budget, int perRound)
    {
        var current = start;
        var slots = current.Lineup.Slots.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        for (var round = 0; round < 2 && budget.Remaining > 0; round++)
        {
            var incumbent = current;
            Explore(candidate => current = candidate, current, budget, perRound, emit =>
            {
                foreach (var slot in slots)
                foreach (var behavior in BehavioursFor(slot).OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (behavior == current.Lineup.Slots[slot].Behaviour) continue;
                    var trial = CopyLineup(current.Lineup);
                    trial.Slots[slot].Behaviour = behavior;
                    if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                }
                for (var i = 0; i < slots.Length; i++)
                for (var j = i + 1; j < slots.Length; j++)
                {
                    var trial = CopyLineup(current.Lineup);
                    (trial.Slots[slots[i]].Player, trial.Slots[slots[j]].Player) =
                        (current.Lineup.Slots[slots[j]].Player, current.Lineup.Slots[slots[i]].Player);
                    if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                }
                var selectedIds = current.Lineup.Slots.Values.Select(s => s.Player.PlayerId).ToHashSet();
                foreach (var slot in slots)
                foreach (var player in available.OrderBy(p => p.PlayerId))
                {
                    if (selectedIds.Contains(player.PlayerId)) continue;
                    var trial = CopyLineup(current.Lineup);
                    trial.Slots[slot].Player = player;
                    if (WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) emit(trial);
                }
            }, opponent, context, disorderRisk, assistantManagerLevel);
            if (current.Score <= incumbent.Score + 1e-9) break;
        }
        return current;
    }

    private OptimizationCandidate RefineJointBehaviours(OptimizationCandidate start, TeamRatings opponent,
        MatchContext context, double disorderRisk, int assistantManagerLevel, int maxPasses)
    {
        var current = start;
        var slots = current.Lineup.Slots.Keys.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        for (var pass = 0; pass < maxPasses; pass++)
        {
            var best = current;
            for (var i = 0; i < slots.Length; i++)
            for (var j = i + 1; j < slots.Length; j++)
            foreach (var behaviorA in BehavioursFor(slots[i]).OrderBy(x => x, StringComparer.Ordinal))
            foreach (var behaviorB in BehavioursFor(slots[j]).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (behaviorA == current.Lineup.Slots[slots[i]].Behaviour
                    && behaviorB == current.Lineup.Slots[slots[j]].Behaviour) continue;
                var trialLineup = CopyLineup(current.Lineup);
                trialLineup.Slots[slots[i]].Behaviour = behaviorA;
                trialLineup.Slots[slots[j]].Behaviour = behaviorB;
                if (!WithinAssistantBehaviorLimit(trialLineup, assistantManagerLevel)) continue;
                var trial = EvaluateCandidate(current.Lineup.Formation, current.Tactic, current.Attitude,
                    current.Coach, trialLineup, opponent, context, disorderRisk);
                if (trial.Score > best.Score + 1e-9) best = trial;
            }
            if (best.Score <= current.Score + 1e-9) break;
            current = best;
        }
        return EvaluateCandidate(current.Lineup.Formation, current.Tactic, current.Attitude,
            current.Coach, current.Lineup, opponent, context, disorderRisk);
    }

    private void Explore(Action<OptimizationCandidate> accept, OptimizationCandidate start,
        SearchBudget budget, int limit, Action<Action<AssignedLineup>> proposals,
        TeamRatings opponent, MatchContext context, double disorderRisk, int assistantManagerLevel)
    {
        var best = start;
        var candidates = new List<AssignedLineup>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { AssignmentKey(start.Lineup) };
        proposals(lineup =>
        {
            if (WithinAssistantBehaviorLimit(lineup, assistantManagerLevel)
                && seen.Add(AssignmentKey(lineup))) candidates.Add(lineup);
        });
        var sampleCount = Math.Min(Math.Min(limit, budget.Remaining), candidates.Count);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            // Spread a fixed evaluation budget across the complete deterministic neighborhood
            // so late slots and bench/player moves are not starved by alphabetical prefixes.
            var index = sampleCount == candidates.Count
                ? sample
                : (int)(((long)(2 * sample + 1) * candidates.Count) / (2 * sampleCount));
            var trial = EvaluateCandidate(start.Lineup.Formation, start.Tactic, start.Attitude, start.Coach,
                candidates[index], opponent, context, disorderRisk);
            budget.Use();
            if (IsBetter(trial, best)) best = trial;
        }
        accept(best);
    }

    private static bool IsBetter(OptimizationCandidate candidate, OptimizationCandidate incumbent) =>
        candidate.Score > incumbent.Score + 1e-9 ||
        (Math.Abs(candidate.Score - incumbent.Score) <= 1e-9
            && string.CompareOrdinal(AssignmentKey(candidate.Lineup), AssignmentKey(incumbent.Lineup)) < 0);

    private static AssignedLineup? CreateEscapeCandidate(OptimizationCandidate current,
        List<Player> available, int assistantManagerLevel, int kick)
    {
        var slots = current.Lineup.Slots.Keys.Where(s => s != "GK").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        if (slots.Length < 2) return null;
        var options = slots.SelectMany((slot, i) => slots.Skip(i + 1).Select(other => (slot, other))).ToArray();
        for (var offset = 0; offset < options.Length; offset++)
        {
            var pair = options[(kick * 7 + offset) % options.Length];
            var trial = CopyLineup(current.Lineup);
            (trial.Slots[pair.slot].Player, trial.Slots[pair.other].Player) =
                (current.Lineup.Slots[pair.other].Player, current.Lineup.Slots[pair.slot].Player);
            var first = BehavioursFor(pair.slot).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var second = BehavioursFor(pair.other).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            trial.Slots[pair.slot].Behaviour = first[(kick + 1) % first.Length];
            trial.Slots[pair.other].Behaviour = second[(kick + 1) % second.Length];
            if (!WithinAssistantBehaviorLimit(trial, assistantManagerLevel)) continue;
            return trial;
        }
        var selectedIds = current.Lineup.Slots.Values.Select(s => s.Player.PlayerId).ToHashSet();
        var bench = available.Where(p => !selectedIds.Contains(p.PlayerId)).OrderBy(p => p.PlayerId).ToArray();
        if (bench.Length == 0) return null;
        var slot = slots[kick % slots.Length];
        var replacement = bench[kick % bench.Length];
        var substitute = CopyLineup(current.Lineup);
        substitute.Slots[slot].Player = replacement;
        return substitute;
    }

    private static string AssignmentKey(AssignedLineup lineup) => string.Join("|", lineup.Slots
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Key}:{kv.Value.Player.PlayerId}:{kv.Value.Behaviour}"));

    private static AssignedLineup CopyLineup(AssignedLineup source) => new()
    {
        Formation = source.Formation,
        Slots = source.Slots.ToDictionary(kv => kv.Key, kv => new AssignedSlot
        {
            SlotId = kv.Value.SlotId, Player = kv.Value.Player, Behaviour = kv.Value.Behaviour
        }, StringComparer.Ordinal)
    };

    private sealed class SearchBudget(int maximum)
    {
        private int _used;
        public int Remaining => Math.Max(0, maximum - _used);
        public void Use() => _used++;
    }

    private sealed class SeedBeamState
    {
        public Dictionary<string, AssignedSlot> Slots { get; init; } = new(StringComparer.Ordinal);
        public HashSet<int> UsedPlayerIds { get; init; } = new();
        public int CustomBehaviors { get; init; }
        public double Score { get; init; }
    }

    private static IEnumerable<string> BehavioursFor(string slot) =>
        FormationData.SlotBehaviourOptions.TryGetValue(slot, out var options) ? options : new[] { slot };

    private void EnforceAssistantBehaviorLimit(AssignedLineup lineup, int assistantManagerLevel)
    {
        var allowed = 5 + assistantManagerLevel;
        while (lineup.Slots.Values.Count(s => s.Behaviour != s.SlotId) > allowed)
        {
            var leastCostly = lineup.Slots.Values
                .Where(s => s.Behaviour != s.SlotId)
                .Select(s => new
                {
                    Slot = s,
                    Cost = PlayerContributionScore(s.Player, s.SlotId,
                        FormationData.PositionContributions[s.Behaviour], null) -
                        PlayerContributionScore(s.Player, s.SlotId,
                        FormationData.PositionContributions[s.SlotId], null)
                })
                .OrderBy(x => x.Cost).ThenBy(x => x.Slot.SlotId, StringComparer.Ordinal).First();
            leastCostly.Slot.Behaviour = leastCostly.Slot.SlotId;
        }
    }

    private static bool WithinAssistantBehaviorLimit(AssignedLineup lineup, int assistantManagerLevel) =>
        lineup.Slots.Values.Count(s => s.Behaviour != s.SlotId) <= 5 + assistantManagerLevel;
    private static double ObjectiveScore(double win, double draw, string objective) => objective switch
    {
        "Win" => win,
        "Draw" => draw,
        _ => 3 * win + draw
    };

    /// <summary>Shared deterministic evaluation for calibration/replay of the supplied exact XI.</summary>
    public OptimizerEvaluation EvaluateAssignedLineup(Lineup lineup, TeamRatings opponentRatings,
        OptimizerRequest context, int weatherId = RatingEngine.WeatherUnknown,
        bool opponentRatingsAreObserved = false, bool opponentHomeBonusAlreadyPresent = false,
        string? opponentHistoricalTactic = null)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        ArgumentNullException.ThrowIfNull(opponentRatings);
        ArgumentNullException.ThrowIfNull(context);
        if (context.AssistantManagerLevel is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(context.AssistantManagerLevel));
        var assigned = AssignedFromReturnedLineup(lineup);
        if (!WithinAssistantBehaviorLimit(assigned, context.AssistantManagerLevel))
            throw new ArgumentException("The supplied XI exceeds the assistant's individual-order limit.", nameof(lineup));
        var disorderRisk = ComputeDisorderRisk(context.FormationExperience.GetValueOrDefault(assigned.Formation.Name, 5));
        var candidate = EvaluateCandidate(assigned.Formation,
            string.IsNullOrWhiteSpace(lineup.TacticType) ? "Normal" : lineup.TacticType,
            NormaliseAttitude(context.TeamAttitude), NormaliseCoachType(context.CoachType), assigned,
            opponentRatings, new MatchContext
            {
                IsHomeMatch = context.IsHomeMatch, WeatherId = weatherId,
                TeamSpiritLevel = context.TeamSpiritLevel, ConfidenceLevel = context.ConfidenceLevel,
                OpponentIspAtt = opponentRatings.IndirectSetPiecesAttRating,
                OpponentIspDef = opponentRatings.IndirectSetPiecesDefRating,
                OpponentTactic = context.OpponentScenario?.Tactic ?? "Normal",
                OpponentRatingsAreObserved = opponentRatingsAreObserved,
                OpponentHomeBonusAlreadyPresent = opponentHomeBonusAlreadyPresent,
                OpponentHistoricalTactic = opponentHistoricalTactic
            }, disorderRisk);
        return new OptimizerEvaluation(candidate.Ratings, candidate.ExpectedGoalsFor, candidate.ExpectedGoalsAgainst,
            candidate.WinProbability, candidate.DrawProbability, candidate.LossProbability,
            candidate.ExpectedPoints, candidate.DisorderRisk);
    }

    private static AssignedLineup AssignedFromReturnedLineup(Lineup lineup, bool requireEligiblePlayers = false)
    {
        if (string.IsNullOrWhiteSpace(lineup.Formation) || !FormationData.Formations.TryGetValue(lineup.Formation, out var formation)
            || lineup.Positions.Count != 11 || lineup.Positions.Values.Any(p => p.Player is null)
            || lineup.Positions.Values.Select(p => p.Player!.PlayerId).Distinct().Count() != 11
            || !lineup.Positions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(formation.Positions)
            || lineup.Positions.Any(kv => !BehavioursFor(kv.Key).Contains(kv.Value.Behavior, StringComparer.Ordinal)
                || !kv.Value.Player!.SkillsAvailable
                || (requireEligiblePlayers && !kv.Value.Player.CanOptimize)))
            throw new InvalidOperationException("The final response lineup must contain eleven distinct eligible players.");
        return new AssignedLineup
        {
            Formation = formation,
            Slots = lineup.Positions.ToDictionary(kv => kv.Key, kv => new AssignedSlot
            {
                SlotId = kv.Key,
                Player = kv.Value.Player!,
                Behaviour = kv.Value.Behavior
            }, StringComparer.Ordinal)
        };
    }

    private static AssignedLineup CloneLineup(AssignedLineup source) => new()
    {
        Formation = source.Formation,
        Slots = source.Slots.ToDictionary(kv => kv.Key, kv => new AssignedSlot
        {
            SlotId = kv.Value.SlotId,
            Player = ClonePlayer(kv.Value.Player),
            Behaviour = kv.Value.Behaviour
        }, StringComparer.Ordinal)
    };

    private static Player ClonePlayer(Player player) => new()
    {
        PlayerId = player.PlayerId, FirstName = player.FirstName, LastName = player.LastName,
        Age = player.Age, TSI = player.TSI,
        Skills = new PlayerSkills
        {
            Keeper = player.Skills.Keeper, KeeperAvailable = player.Skills.KeeperAvailable,
            Defending = player.Skills.Defending, DefendingAvailable = player.Skills.DefendingAvailable,
            Playmaking = player.Skills.Playmaking, PlaymakingAvailable = player.Skills.PlaymakingAvailable,
            Winger = player.Skills.Winger, WingerAvailable = player.Skills.WingerAvailable,
            Passing = player.Skills.Passing, PassingAvailable = player.Skills.PassingAvailable,
            Scoring = player.Skills.Scoring, ScoringAvailable = player.Skills.ScoringAvailable,
            SetPieces = player.Skills.SetPieces, SetPiecesAvailable = player.Skills.SetPiecesAvailable
        },
        Form = player.Form, Stamina = player.Stamina, StaminaAvailable = player.StaminaAvailable,
        Experience = player.Experience,
        Loyalty = player.Loyalty, MotherClubBonus = player.MotherClubBonus,
        Leadership = player.Leadership, Specialty = player.Specialty,
        InjuryLevel = player.InjuryLevel, InjuryStatusKnown = player.InjuryStatusKnown,
        ShirtNumber = player.ShirtNumber, IsSuspended = player.IsSuspended,
        SuspensionStatusKnown = player.SuspensionStatusKnown,
        MatchStats = player.MatchStats is null ? null : new PlayerMatchStats
        {
            TotalMatches = player.MatchStats.TotalMatches, Goals = player.MatchStats.Goals,
            Assists = player.MatchStats.Assists, YellowCards = player.MatchStats.YellowCards,
            RedCards = player.MatchStats.RedCards, AverageRating = player.MatchStats.AverageRating,
            AverageForm = player.MatchStats.AverageForm, GoalsPerMatch = player.MatchStats.GoalsPerMatch,
            MatchesPerGoal = player.MatchStats.MatchesPerGoal, MinutesPlayed = player.MatchStats.MinutesPlayed,
            PositionRatings = new Dictionary<string, double>(player.MatchStats.PositionRatings, StringComparer.Ordinal)
        },
        Provenance = new DataProvenance
        {
            Source = player.Provenance.Source, RetrievedAt = player.Provenance.RetrievedAt,
            Warnings = player.Provenance.Warnings.ToList(), SampleCount = player.Provenance.SampleCount
        }
    };

    // ======================= Helpers =======================

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
        var assignedLineup = ToLineup(CloneLineup(best.Lineup));
        assignedLineup.TacticType = best.Tactic;
        assignedLineup.PredictedRatings = best.Ratings;
        return assignedLineup;
    }

    private static Lineup ToLineup(AssignedLineup assigned, bool includeIndividualRatings = true)
    {
        var lineup = new Lineup { Formation = assigned.Formation.Name };
        foreach (var s in assigned.Slots)
        {
            lineup.Positions[s.Key] = new LineupPosition
            {
                Position = s.Key,
                Player = s.Value.Player,
                Behavior = s.Value.Behaviour,
                Rating = includeIndividualRatings ? ComputePlayerSlotRating(s.Value.Player, s.Key, s.Value.Behaviour) : 0,
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
    private static double ComputePlayerSlotRating(Player player, string slot, string behaviour)
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
            r.Add(T("Obie flanki są silne względem obrony przeciwnika — AOW przekierowuje część szans ze środka na oba skrzydła, kosztem obrony centralnej.", "Both flanks are strong against the opponent's defense — AOW routes some central chances to both wings at the cost of central defense.", lang));
        else if (rightAdv)
            r.Add(T("Prawa flanka jest silna; AOW rozdziela przekierowane szanse na oba skrzydła, więc część z nich trafi na słabszą lewą flankę.", "The right flank is strong; AOW routes chances to both wings, so some will go to the weaker left flank.", lang));
        else if (leftAdv)
            r.Add(T("Lewa flanka jest silna; AOW rozdziela przekierowane szanse na oba skrzydła, więc część z nich trafi na słabszą prawą flankę.", "The left flank is strong; AOW routes chances to both wings, so some will go to the weaker right flank.", lang));
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
        "AttackInMiddle" => T("Atak środkiem (AIM) — przekierowuje część ataków ze skrzydeł do środka i osłabia boczną obronę", "Attack in the Middle (AIM) — routes some wing attacks centrally and weakens wing defense", lang),
        "AttackOnWings"  => T("Atak skrzydłami (AOW) — przekierowuje część ataków ze środka na skrzydła i osłabia obronę centralną", "Attack on Wings (AOW) — routes some central attacks to the wings and weakens central defense", lang),
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
    public int? TeamSpiritLevel { get; set; }
    public int? ConfidenceLevel { get; set; }
    public string Objective { get; set; } = "ExpectedPoints";
    public string OpponentTactic { get; set; } = "Normal";
    public bool OpponentRatingsAreObserved { get; set; }
    public bool OpponentHomeBonusAlreadyPresent { get; set; }
    public string? OpponentHistoricalTactic { get; set; }
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
    public double ExpectedPoints { get; set; }
    public double ExpectedGoalsFor { get; set; }
    public double ExpectedGoalsAgainst { get; set; }
    public LineupRatings OpponentRatings { get; set; } = new();
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
