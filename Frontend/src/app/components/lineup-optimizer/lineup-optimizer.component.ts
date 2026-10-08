import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, Subscription } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { OptimizerRequest, OptimizerResponse, LineupPosition, Lineup, LineupRatings } from '../../models/lineup.model';
import { TranslateService } from '@ngx-translate/core';
import { DataCacheService } from '../../services/data-cache.service';
import { Player } from '../../models/player.model';
import { OpponentScoutReport, ScoutLikelyStarter } from '../../models/opponent-scout.model';

@Component({
  selector: 'app-lineup-optimizer',
  templateUrl: './lineup-optimizer.component.html',
  styleUrls: ['./lineup-optimizer.component.scss']
})
export class LineupOptimizerComponent implements OnInit, OnDestroy {
  private destroy$ = new Subject<void>();
  private optimizeSubscription?: Subscription;
  private optimizeSequence = 0;
  private formationExperienceRequestSequence = 0;
  private readonly manuallySetFormationExperience = new Set<string>();
  private lastNextOpponentContext = '';
  myTeamId: number = 0;
  opponentTeamId: number = 0;
  preferredTactic: string = 'Auto';
  teamAttitude: string = 'Normal';
  coachType: string = 'Neutral';
  assistantManagerLevel: number = 0;
  availableFormations: string[] = ['5-5-0','5-4-1','5-3-2','5-2-3','4-5-1','4-4-2','4-3-3','3-5-2','3-4-3','2-5-3'];
  formationExperience: { [k: string]: number } = {};
  selectedAlternative: number = 0;
  objective: OptimizerRequest['objective'] = 'Win';
  teamSpiritLevel: number | null = null;
  confidenceLevel: number | null = null;
  resultStale = false;
  resultStaleReason: string | null = null;
  resultGeneratedAt: string | null = null;
  private resultTeamContext = '';

  result: OptimizerResponse | null = null;
  loading: boolean = false;
  error: string | null = null;

  myTeamPlayers: Player[] = [];
  opponentTeamPlayers: Player[] = [];
  loadingMyTeam: boolean = false;
  loadingOpponentTeam: boolean = false;
  lastLoadedMyTeamId: number | null = null;
  lastLoadedOpponentId: number | null = null;

  // Statystyki druyny
  myTeamStats: any = null;
  opponentTeamStats: any = null;
  loadingMyTeamStats: boolean = false;
  loadingOpponentTeamStats: boolean = false;

  // Raport skauta przeciwnika
  opponentScout: OpponentScoutReport | null = null;
  loadingOpponentScout: boolean = false;
  
  myTeamName: string = '';
  opponentTeamName: string = '';

  // Wybór formacji i taktyki dla mojej drużyny
  selectedMyFormation: string = 'Auto';
  selectedMyTactic: string = 'Auto';
  
  // Wybór formacji i taktyki dla przeciwnika
  selectedOpponentFormation: string = 'Auto';
  selectedOpponentTactic: string = 'Auto';
  
  // Optimal Lineup dla przeciwnika
  opponentOptimalLineup: Lineup | null = null;
  
  // Sortowanie tabeli graczy
  playerSortColumn: string = 'form';
  playerSortDirection: 'asc' | 'desc' = 'desc';
  
  // Trener i sztab
  trainerLevel: number = 0;
  assistantCoachLevel: number = 0;
  formCoachLevel: number = 0;
  
  // Kolumny do sortowania
  sortableColumns = [
    { key: 'name', label: 'Imię' },
    { key: 'age', label: 'Wiek' },
    { key: 'form', label: 'Forma' },
    { key: 'stamina', label: 'Kondycja' },
    { key: 'keeper', label: 'Bramkarz' },
    { key: 'defending', label: 'Obrona' },
    { key: 'playmaking', label: 'Rozgrywanie' },
    { key: 'winger', label: 'Skrzydło' },
    { key: 'passing', label: 'Podania' },
    { key: 'scoring', label: 'Skuteczność' },
    { key: 'setPieces', label: 'Stałe fragmenty' },
    { key: 'goals', label: 'Bramki' },
    { key: 'assists', label: 'Asysty' },
    { key: 'avgForm', label: 'Śr. Forma' },
    { key: 'goalsPerMatch', label: 'Bramki/Mecz' },
    { key: 'matchesPerGoal', label: 'Mecze/Bramka' }
  ];

  tactics: { value: string; label: string }[] = [];
  attitudes: { value: string; label: string }[] = [];
  coachTypes: { value: string; label: string }[] = [];
  experienceLevels: { value: number; label: string }[] = [];

  constructor(
    private hattrickApi: HattrickApiService,
    private translate: TranslateService,
    private cache: DataCacheService
  ) {}

  ngOnInit(): void {
    this.initializeTranslations();
    this.translate.onLangChange.pipe(takeUntil(this.destroy$)).subscribe(() => {
      this.initializeTranslations();
      this.onOptimizerInputChange();
    });
    this.translate.onTranslationChange.pipe(takeUntil(this.destroy$)).subscribe(() => this.initializeTranslations());

    this.restoreFromCache();
    this.cache.optimizerResult$.next(null);

    this.cache.auth$.pipe(takeUntil(this.destroy$)).subscribe(auth => {
      if (auth.authorized && auth.ownTeamId) {
        if (this.myTeamId !== auth.ownTeamId) {
          this.invalidateTeamContext();
          this.myTeamId = auth.ownTeamId;
          this.myTeamPlayers = [];
          this.clearFormationExperience();
          this.lastLoadedMyTeamId = null;
          this.myTeamStats = null;
          this.cache.myTeamStats$.next(null);
          this.cache.formationExperience$.next(null);
        }
        this.loadMyTeam();
        this.loadMyTeamStats();
      }
    });
    this.cache.nextOpponent$.pipe(takeUntil(this.destroy$)).subscribe(opp => {
      const opponentContext = JSON.stringify(opp ?? null);
      if (this.lastNextOpponentContext && opponentContext !== this.lastNextOpponentContext) this.invalidateTeamContext();
      this.lastNextOpponentContext = opponentContext;
      const cachedOpponentId = this.cache.opponentTeam$.value?.teamId;
      if (opp?.opponentTeamId && this.opponentTeamId !== opp.opponentTeamId
        && (!this.opponentTeamId || this.opponentTeamId === this.lastLoadedOpponentId || this.opponentTeamId === cachedOpponentId)) {
        this.opponentTeamId = opp.opponentTeamId;
        this.lastLoadedOpponentId = null;
        this.opponentTeamPlayers = [];
        this.opponentScout = null;
        this.opponentTeamStats = null;
        this.cache.opponentPlayers$.next(null);
        this.cache.opponentScout$.next(null);
        this.cache.opponentTeamStats$.next(null);
        this.cache.opponentOptimalLineup$.next(null);
      }
      if (this.opponentTeamId && opp?.opponentTeamId !== this.opponentTeamId) this.invalidateTeamContext();
      if (opp?.opponentTeamId) {
        if (!this.opponentTeamId) this.opponentTeamId = opp.opponentTeamId;
        this.loadOpponentTeam();
        this.loadOpponentTeamStats();
        this.loadOpponentScout();
      }
    });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  private restoreFromCache(): void {
    const ui = this.cache.optimizerUi$.value;
    this.selectedMyFormation = ui.selectedMyFormation;
    this.selectedMyTactic = ui.selectedMyTactic;
    this.selectedOpponentFormation = ui.selectedOpponentFormation;
    this.selectedOpponentTactic = ui.selectedOpponentTactic;
    this.coachType = ui.coachType;
    this.assistantManagerLevel = ui.assistantManagerLevel;
    this.teamAttitude = ui.teamAttitude;
    this.objective = ui.objective ?? 'Win';
    this.preferredTactic = this.selectedMyTactic;
    this.selectedAlternative = ui.selectedAlternative;
    this.playerSortColumn = ui.playerSortColumn;
    this.playerSortDirection = ui.playerSortDirection;

    const cachedOwn = this.cache.ownTeam$.value;
    if (cachedOwn?.players?.length) {
      this.myTeamId = cachedOwn.teamId;
      this.myTeamName = cachedOwn.teamName;
      this.myTeamPlayers = cachedOwn.players;
      this.lastLoadedMyTeamId = cachedOwn.teamId;
    }

    const cachedOpponentTeam = this.cache.opponentTeam$.value;
    if (cachedOpponentTeam) {
      this.opponentTeamId = cachedOpponentTeam.teamId;
      this.opponentTeamName = cachedOpponentTeam.teamName;
    } else {
      const nextOpp = this.cache.nextOpponent$.value;
      if (nextOpp?.opponentTeamId) {
        this.opponentTeamId = nextOpp.opponentTeamId;
        this.opponentTeamName = nextOpp.opponentTeamName;
      }
    }

    const cachedOpponentPlayers = this.cache.opponentPlayers$.value;
    if (cachedOpponentPlayers?.length) {
      this.opponentTeamPlayers = cachedOpponentPlayers;
      this.lastLoadedOpponentId = this.opponentTeamId;
    }

    const cachedMyStats = this.cache.myTeamStats$.value;
    if (cachedMyStats) this.myTeamStats = cachedMyStats;

    const cachedOppStats = this.cache.opponentTeamStats$.value;
    if (cachedOppStats) this.opponentTeamStats = cachedOppStats;

    const cachedScout = this.cache.opponentScout$.value;
    if (cachedScout) this.opponentScout = cachedScout;

    const cachedExp = this.cache.formationExperience$.value;
    if (cachedExp) {
      this.formationExperience = this.sanitizeFormationExperience(cachedExp);
      this.cache.formationExperience$.next({ ...this.formationExperience });
    } else this.formationExperience = {};

    // Optimizer results are snapshots; do not restore an unkeyed response across team contexts.

    this.opponentOptimalLineup = null;
  }

  private persistUiState(): void {
    this.cache.optimizerUi$.next({
      selectedMyFormation: this.selectedMyFormation,
      selectedMyTactic: this.selectedMyTactic,
      selectedOpponentFormation: this.selectedOpponentFormation,
      selectedOpponentTactic: this.selectedOpponentTactic,
      coachType: this.coachType,
      assistantManagerLevel: this.assistantManagerLevel,
      teamAttitude: this.teamAttitude,
      preferredTactic: this.selectedMyTactic,
      objective: this.objective,
      selectedAlternative: this.selectedAlternative,
      playerSortColumn: this.playerSortColumn,
      playerSortDirection: this.playerSortDirection
    });
  }

  private initializeTranslations(): void {
    this.tactics = [
      { value: 'Auto', label: this.translate.instant('optimizer.tactics.auto') },
      { value: 'Normal', label: this.translate.instant('optimizer.tactics.normal') },
      { value: 'Counter', label: this.translate.instant('optimizer.tactics.counter') },
      { value: 'AttackInMiddle', label: this.translate.instant('optimizer.tactics.attackMiddle') },
      { value: 'AttackOnWings', label: this.translate.instant('optimizer.tactics.attackWings') },
      { value: 'Pressing', label: this.translate.instant('optimizer.tactics.pressing') },
      { value: 'PlayCreatively', label: this.translate.instant('optimizer.tactics.creatively') },
      { value: 'LongShots', label: this.translate.instant('optimizer.tactics.longShots') }
    ];
    this.attitudes = [
      { value: 'Normal', label: this.translate.instant('optimizer.attitudes.normal') },
      { value: 'PIC', label: this.translate.instant('optimizer.attitudes.pic') },
      { value: 'MOTS', label: this.translate.instant('optimizer.attitudes.mots') }
    ];
    this.coachTypes = [
      { value: 'Neutral', label: this.translate.instant('optimizer.coach.neutral') },
      { value: 'Offensive', label: this.translate.instant('optimizer.coach.offensive') },
      { value: 'Defensive', label: this.translate.instant('optimizer.coach.defensive') }
    ];
    this.experienceLevels = [
      { value: 10, label: this.translate.instant('formationExperience.10') },
      { value: 9, label: this.translate.instant('formationExperience.9') },
      { value: 8, label: this.translate.instant('formationExperience.8') },
      { value: 7, label: this.translate.instant('formationExperience.7') },
      { value: 6, label: this.translate.instant('formationExperience.6') },
      { value: 5, label: this.translate.instant('formationExperience.5') },
      { value: 4, label: this.translate.instant('formationExperience.4') },
      { value: 3, label: this.translate.instant('formationExperience.3') }
    ];
  }

  getFormationExperience(formation: string): number | null {
    return this.formationExperience[formation] ?? null;
  }

  setFormationExperience(formation: string, value: number | null): void {
    this.manuallySetFormationExperience.add(formation);
    if (!this.isValidFormationExperience(value)) delete this.formationExperience[formation];
    else this.formationExperience[formation] = value;
    this.cache.formationExperience$.next({ ...this.formationExperience });
    this.onOptimizerInputChange();
  }

  private isValidFormationExperience(value: unknown): value is number {
    return typeof value === 'number' && Number.isInteger(value) && value >= 3 && value <= 10;
  }

  private sanitizeFormationExperience(experience: unknown): { [formation: string]: number } {
    const sanitized: { [formation: string]: number } = {};
    if (!experience || typeof experience !== 'object') return sanitized;
    const values = experience as Record<string, unknown>;
    for (const formation of this.availableFormations) {
      const value = values[formation];
      if (this.isValidFormationExperience(value)) sanitized[formation] = value;
    }
    return sanitized;
  }

  private clearFormationExperience(): void {
    this.formationExperienceRequestSequence++;
    this.manuallySetFormationExperience.clear();
    this.formationExperience = {};
    this.cache.formationExperience$.next(null);
  }

  onObjectiveChange(): void {
    this.persistUiState();
    this.onOptimizerInputChange();
  }

  get displayedFormationExperienceMissing(): boolean {
    const formation = this.displayedLineup.formation;
    return !!formation && this.result?.inputSnapshot?.formationExperience?.[formation] === undefined;
  }

  optimizeLineup(): void {
    if (!this.myTeamId || !this.opponentTeamId) {
      this.error = this.translate.instant('optimizer.enterBothTeamIds');
      return;
    }

    this.optimizeSubscription?.unsubscribe();
    const requestId = ++this.optimizeSequence;
    this.loading = true;
    this.error = null;
    this.resultStale = true;
    this.cache.optimizerResult$.next(null);

    const request = this.createRequestSnapshot();

    this.optimizeSubscription = this.hattrickApi.optimizeLineup(request).pipe(takeUntil(this.destroy$)).subscribe({
      next: (response) => {
        if (requestId !== this.optimizeSequence || !this.sameContext(request, this.createRequestSnapshot())) return;
        this.result = response;
        this.selectedAlternative = 0;
        this.loading = false;
        this.resultStale = false;
        this.resultStaleReason = null;
        this.resultGeneratedAt = response.generatedAt ?? new Date().toISOString();
        this.resultTeamContext = this.currentTeamContext();
        this.cache.optimizerResult$.next(response);
        this.persistUiState();
      },
      error: (err) => {
        if (requestId !== this.optimizeSequence) return;
        this.error = this.translate.instant('optimizer.errorOptimizing') + err.message;
        this.loading = false;
      }
    });
  }

  get displayedLineup(): Lineup {
    return this.result?.alternatives?.[this.selectedAlternative]?.lineup ?? this.result?.optimalLineup ?? {
      positions: {}, tacticType: '', tacticSkill: '',
      predictedRatings: { midfield: 0, rightDefense: 0, centralDefense: 0, leftDefense: 0, rightAttack: 0, centralAttack: 0, leftAttack: 0, overall: 0 }
    };
  }

  get displayedRatings(): LineupRatings | null {
    return this.result?.alternatives?.[this.selectedAlternative]?.ratings ?? this.result?.comparison?.myTeamRatings ?? null;
  }

  get activeAlternative(): OptimizerResponse['alternatives'][number] | null {
    return this.result?.alternatives?.[this.selectedAlternative] ?? null;
  }

  get displayedStrengths(): string[] {
    if (this.selectedAlternative === 0) return (this.result?.comparison.strengths ?? []).map(s => this.localizeText(s));
    const my = this.displayedRatings;
    const opponent = this.result?.comparison.opponentRatings;
    if (!my || !opponent) return [];
    const strengths: string[] = [];
    if (my.midfield > opponent.midfield) strengths.push(this.translate.instant('optimizer.comparison.matchupLabels.midfield'));
    for (const matchup of this.attackDefenseMatchups) {
      if (my[matchup.myKey] > opponent[matchup.opponentKey]) {
        strengths.push(this.translate.instant('optimizer.comparison.matchupLabels.attack', {
          lane: this.translate.instant(matchup.lane), opponentLane: this.translate.instant(matchup.opponentLane)
        }));
      }
    }
    for (const matchup of this.defenseAttackMatchups) {
      if (my[matchup.myKey] > opponent[matchup.opponentKey]) {
        strengths.push(this.translate.instant('optimizer.comparison.matchupLabels.defense', {
          lane: this.translate.instant(matchup.lane), opponentLane: this.translate.instant(matchup.opponentLane)
        }));
      }
    }
    return strengths;
  }

  get displayedWeaknesses(): string[] {
    if (this.selectedAlternative === 0) return (this.result?.comparison.weaknesses ?? []).map(s => this.localizeText(s));
    const my = this.displayedRatings;
    const opponent = this.result?.comparison.opponentRatings;
    if (!my || !opponent) return [];
    const weaknesses: string[] = [];
    if (my.midfield < opponent.midfield) weaknesses.push(this.translate.instant('optimizer.comparison.matchupLabels.midfield'));
    for (const matchup of this.attackDefenseMatchups) {
      if (my[matchup.myKey] < opponent[matchup.opponentKey]) {
        weaknesses.push(this.translate.instant('optimizer.comparison.matchupLabels.attack', {
          lane: this.translate.instant(matchup.lane), opponentLane: this.translate.instant(matchup.opponentLane)
        }));
      }
    }
    for (const matchup of this.defenseAttackMatchups) {
      if (my[matchup.myKey] < opponent[matchup.opponentKey]) {
        weaknesses.push(this.translate.instant('optimizer.comparison.matchupLabels.defense', {
          lane: this.translate.instant(matchup.lane), opponentLane: this.translate.instant(matchup.opponentLane)
        }));
      }
    }
    return weaknesses;
  }

  get selectedAlternativeReasons(): string[] {
    const alt = this.activeAlternative;
    if (!alt) return [];
    return [this.translate.instant('optimizer.alternatives.selectedSummary', {
      expectedPoints: alt.expectedPoints.toFixed(2), win: (alt.winProbability * 100).toFixed(1), draw: (alt.drawProbability * 100).toFixed(1)
    })];
  }

  get modelWarnings(): string[] {
    if (!this.result) return [];
    const warnings = [...(this.result.modelWarnings ?? this.result.warnings ?? []),
      ...(this.result.ownTeamProvenance?.warnings ?? []), ...(this.result.opponentProvenance?.warnings ?? [])];
    const translations: Array<[string, string]> = [
      ['Approximation details:', 'approximationDetails'],
      ['Spirit adjusts midfield', 'contextApproximation'],
      ['Formation experience below', 'formationApproximation'],
      ['The model is unvalidated', 'unvalidated'],
      ['Team spirit was not available', 'noSpirit'],
      ['Confidence was not available', 'noConfidence'],
      ['Opponent ratings came from a default', 'defaultOpponent'],
      ['Opponent tactic is recorded in the snapshot', 'opponentTacticNotModeled'],
      ['Opponent ratings aggregate historical tactics', 'historicalTacticApproximation'],
      ['Long Shots evaluates', 'longShotsApproximation'],
      ['Opponent sector ratings are an estimate', 'hiddenOpponentSkills'],
      ['Matchline does not contain private player skills', 'matchlineNoSkills'],
      ['No match observations available', 'noOpponentObservations'],
      ['Mock ratings are synthetic', 'syntheticRatings'],
      ['No played opponent ratings were available', 'defaultOpponent']
    ];
    return [...new Set(warnings)].map(w => {
      const entry = translations.find(([prefix]) => w.startsWith(prefix));
      return entry ? this.translate.instant(`optimizer.warnings.${entry[1]}`)
        : this.translate.currentLang === 'pl' ? this.translate.instant('optimizer.warnings.additional') : w;
    });
  }

  getConfidenceLabel(value?: string): string {
    const map: Record<string, string> = {
      'unvalidated-low': 'optimizer.confidenceLevels.unvalidatedLow',
      low: 'optimizer.confidenceLevels.low',
      'low-moderate': 'optimizer.confidenceLevels.lowModerate',
      moderate: 'optimizer.confidenceLevels.moderate',
      high: 'optimizer.confidenceLevels.high'
    };
    const key = value ? map[value.toLowerCase()] : undefined;
    return key ? this.translate.instant(key) : (value ?? '—');
  }

  getOpponentSourceLabel(value?: string): string {
    const map: Record<string, string> = {
      lastmatch: 'optimizer.sources.lastMatch',
      default: 'optimizer.sources.default',
      mock: 'optimizer.sources.mock',
      weightedscout: 'optimizer.sources.weightedScout',
      scout: 'optimizer.sources.weightedScout'
    };
    const key = value ? map[value.toLowerCase()] : undefined;
    return key ? this.translate.instant(key) : (value ?? '—');
  }

  getObjectiveLabel(value?: string): string {
    const key = value === 'Win' ? 'optimizer.objective.win' : value === 'Draw' ? 'optimizer.objective.draw' : 'optimizer.objective.expectedPoints';
    return this.translate.instant(key);
  }

  private createRequestSnapshot(): OptimizerRequest {
    const nextOpp = this.cache.nextOpponent$.value;
    const isNextOpponent = nextOpp?.opponentTeamId === this.opponentTeamId;
    return {
      myTeamId: Number(this.myTeamId), opponentTeamId: Number(this.opponentTeamId),
      preferredTactic: this.selectedMyTactic, teamAttitude: this.teamAttitude, focusAreas: [],
      coachType: this.coachType, assistantManagerLevel: this.assistantManagerLevel,
      teamSpiritLevel: this.teamSpiritLevel, confidenceLevel: this.confidenceLevel, objective: this.objective,
      formationExperience: this.sanitizeFormationExperience(this.formationExperience), preferredFormation: this.selectedMyFormation,
      language: this.translate.currentLang || 'pl', matchId: isNextOpponent ? nextOpp!.matchId : 0,
      isHomeMatch: isNextOpponent ? !!nextOpp!.isHomeMatch : false,
      matchDate: isNextOpponent ? nextOpp!.matchDate : undefined
    };
  }

  private sameContext(a: OptimizerRequest, b: OptimizerRequest): boolean {
    return JSON.stringify(a) === JSON.stringify(b);
  }

  onOptimizerInputChange(): void {
    if (!this.result && !this.loading) return;
    this.resultStale = true;
    this.resultStaleReason = this.translate.instant('optimizer.staleResult');
    this.optimizeSequence++;
    this.optimizeSubscription?.unsubscribe();
    this.cache.optimizerResult$.next(null);
    this.loading = false;
    this.sendingOrders = false;
    this.ordersSuccess = false;
  }

  private currentTeamContext(): string {
    const own = this.myTeamPlayers.map(p => p.playerId).sort((a, b) => a - b).join(',');
    const scout = this.opponentScout?.likelyStarters.map(s => `${s.playerId}:${s.slot}:${s.appearances}`).sort().join(',') ?? '';
    const next = this.cache.nextOpponent$.value;
    return `${this.myTeamId}|${this.opponentTeamId}|${own}|${scout}|${next?.matchId ?? 0}|${next?.isHomeMatch ?? false}`;
  }

  private invalidateTeamContext(): void {
    this.onOptimizerInputChange();
    this.result = null;
    this.cache.optimizerResult$.next(null);
  }

  getPositionKeys(): string[] {
    if (!this.displayedLineup?.positions) return [];
    return Object.keys(this.displayedLineup.positions);
  }

  getDisplayedPosition(position: string): LineupPosition | null {
    return this.displayedLineup.positions[position] ?? null;
  }

  getDisplayedPositionRows(): string[][] {
    const positions = this.getPositionKeys();
    const gk = positions.filter(p => p === 'GK');
    const defenders = positions.filter(p => ['RWB', 'RCD', 'CD', 'LCD', 'LWB', 'CDO', 'CDTW', 'WBD', 'WBN', 'WBO', 'WBTM'].includes(p));
    const midfielders = positions.filter(p => ['RW', 'RIM', 'IM', 'LIM', 'LW', 'WO', 'WD', 'WTM'].includes(p));
    const forwards = positions.filter(p => ['RFW', 'FW', 'LFW', 'FTW', 'DF'].includes(p));
    const known = new Set([...gk, ...defenders, ...midfielders, ...forwards]);
    midfielders.push(...positions.filter(p => !known.has(p)));
    return [gk, defenders, midfielders, forwards];
  }

  getTacticLabel(value: string): string {
    const map: { [key: string]: string } = {
      'Auto': 'optimizer.tactics.auto',
      'Normal': 'optimizer.tactics.normal',
      'Counter': 'optimizer.tactics.counter',
      'AttackInMiddle': 'optimizer.tactics.attackMiddle',
      'AttackOnWings': 'optimizer.tactics.attackWings',
      'Pressing': 'optimizer.tactics.pressing',
      'PlayCreatively': 'optimizer.tactics.creatively',
      'LongShots': 'optimizer.tactics.longShots'
    };
    const key = map[value];
    return key ? this.translate.instant(key) : value;
  }

  getAttitudeLabel(value: string): string {
    const map: { [key: string]: string } = {
      'Normal': 'optimizer.attitudes.normal',
      'PIC': 'optimizer.attitudes.pic',
      'MOTS': 'optimizer.attitudes.mots'
    };
    const key = map[value];
    return key ? this.translate.instant(key) : value;
  }

  selectAlternative(index: number): void {
    this.selectedAlternative = index;
    this.persistUiState();
  }

  getPositionLabel(position: string): string {
    const labels: { [key: string]: string } = {
      'GK': this.translate.instant('optimizer.positions.GK'),
      'RWB': this.translate.instant('optimizer.positions.RWB'),
      'RCD': this.translate.instant('optimizer.positions.RCD'),
      'CD': this.translate.instant('optimizer.positions.CD'),
      'LCD': this.translate.instant('optimizer.positions.LCD'),
      'LWB': this.translate.instant('optimizer.positions.LWB'),
      'RW': this.translate.instant('optimizer.positions.RW'),
      'RIM': this.translate.instant('optimizer.positions.RIM'),
      'IM': this.translate.instant('optimizer.positions.IM'),
      'LIM': this.translate.instant('optimizer.positions.LIM'),
      'LW': this.translate.instant('optimizer.positions.LW'),
      'RFW': this.translate.instant('optimizer.positions.RFW'),
      'FW': this.translate.instant('optimizer.positions.FW'),
      'LFW': this.translate.instant('optimizer.positions.LFW'),
      // Dodatkowe pozycje z formacji
      'CDO': this.translate.instant('optimizer.positions.CDO') || 'CDO',
      'CDTW': this.translate.instant('optimizer.positions.CDTW') || 'CDTW',
      'WBD': this.translate.instant('optimizer.positions.WBD') || 'WBD',
      'WBN': this.translate.instant('optimizer.positions.WBN') || 'WBN',
      'WBO': this.translate.instant('optimizer.positions.WBO') || 'WBO',
      'WBTM': this.translate.instant('optimizer.positions.WBTM') || 'WBTM',
      'WO': this.translate.instant('optimizer.positions.WO') || 'WO',
      'WD': this.translate.instant('optimizer.positions.WD') || 'WD',
      'WTM': this.translate.instant('optimizer.positions.WTM') || 'WTM',
      'FTW': this.translate.instant('optimizer.positions.FTW') || 'FTW',
      'DF': this.translate.instant('optimizer.positions.DF') || 'DF'
    };
    return labels[position] || position;
  }

  getPositionDescription(position: string): string {
    const descriptions: { [key: string]: string } = {
      'GK': 'Bramkarz',
      'RWB': 'Prawy Boczny Obroca',
      'RCD': 'Prawy rodkowy Obroca',
      'CD': 'rodkowy Obroca',
      'LCD': 'Lewy rodkowy Obroca',
      'LWB': 'Lewy Boczny Obroca',
      'RW': 'Prawy Pomocnik',
      'RIM': 'Prawy Wewntrzny Pomocnik',
      'IM': 'rodkowy Pomocnik',
      'LIM': 'Lewy Wewntrzny Pomocnik',
      'LW': 'Lewy Pomocnik',
      'RFW': 'Prawy Napastnik',
      'FW': 'rodkowy Napastnik',
      'LFW': 'Lewy Napastnik',
      'CDO': 'rodkowy Obroca Ofensywny',
      'CDTW': 'rodkowy Obroca do Skrzyda',
      'WBD': 'Prawy Boczny Obroca Defensywny',
      'WBN': 'Prawy Boczny Obroca Normalny',
      'WBO': 'Prawy Boczny Obroca Ofensywny',
      'WBTM': 'Prawy Boczny Obroca do rodka',
      'WO': 'Prawy Skrzydowy Ofensywny',
      'WD': 'Prawy Skrzydowy Defensywny',
      'WTM': 'Prawy Skrzydowy do rodka',
      'FTW': 'Napastnik do Skrzyda',
      'DF': 'Napastnik Defensywny'
    };
    return descriptions[position] || position;
  }

  loadMyTeam(): void {
    if (!this.myTeamId) return;
    const teamId = Number(this.myTeamId);
    
    // Nie pobieraj ponownie jeśli dane są już załadowane dla tego samego ID
    if (this.myTeamPlayers.length > 0 && this.lastLoadedMyTeamId === this.myTeamId) {
      return;
    }
    
    this.loadingMyTeam = true;
    this.hattrickApi.getTeam(teamId).subscribe({
      next: (team) => {
        if (teamId !== Number(this.myTeamId)) return;
        this.myTeamPlayers = team.players;
        this.myTeamName = team.teamName;
        this.lastLoadedMyTeamId = this.myTeamId;
        this.loadingMyTeam = false;
        this.cache.ownTeam$.next(team);
        this.onOptimizerInputChange();
        // Generuj statystyki dla graczy
        this.generatePlayerStats();
        // Pobierz doświadczenie formacji
        this.loadFormationExperience();
      },
      error: () => {
        if (teamId !== Number(this.myTeamId)) return;
        this.loadingMyTeam = false;
      }
    });
  }

  loadFormationExperience(): void {
    if (!this.myTeamId) return;
    const teamId = Number(this.myTeamId);
    const requestSequence = ++this.formationExperienceRequestSequence;

    if (this.cache.formationExperience$.value !== null) {
      this.formationExperience = this.sanitizeFormationExperience(this.cache.formationExperience$.value);
      this.cache.formationExperience$.next({ ...this.formationExperience });
      return;
    }

    this.hattrickApi.getFormationExperience(teamId).subscribe({
      next: (experience) => {
        if (teamId !== Number(this.myTeamId) || requestSequence !== this.formationExperienceRequestSequence) return;
        const currentExperience = { ...this.formationExperience };
        const validExperience = this.sanitizeFormationExperience(experience);
        for (const formation of this.availableFormations) {
          if (this.manuallySetFormationExperience.has(formation)) {
            const current = currentExperience[formation];
            if (this.isValidFormationExperience(current)) validExperience[formation] = current;
            else delete validExperience[formation];
          }
        }
        this.formationExperience = validExperience;
        this.cache.formationExperience$.next({ ...this.formationExperience });
        this.onOptimizerInputChange();
      },
      error: (err) => {
        if (teamId !== Number(this.myTeamId) || requestSequence !== this.formationExperienceRequestSequence) return;
        console.error('Error loading formation experience:', err);
        // W przypadku błędu zachowaj domyślne wartości
      }
    });
  }

  loadOpponentTeam(): void {
    if (!this.opponentTeamId) return;
    const teamId = Number(this.opponentTeamId);
    
    // Nie pobieraj ponownie jeśli dane są już załadowane dla tego samego ID
    if (this.opponentTeamPlayers.length > 0 && this.lastLoadedOpponentId === this.opponentTeamId) {
      return;
    }
    
    this.loadingOpponentTeam = true;

    // Najpierw pobierz podstawowe info o drużynie (nazwa) – tylko jeśli nie mamy w cache
    const cachedOpp = this.cache.opponentTeam$.value;
    if (cachedOpp?.teamId === this.opponentTeamId) {
      this.opponentTeamName = cachedOpp.teamName;
    } else {
      this.hattrickApi.getTeam(teamId).subscribe({
        next: (team) => {
          if (teamId !== Number(this.opponentTeamId)) return;
          this.opponentTeamName = team.teamName;
          this.cache.opponentTeam$.next(team);
        },
        error: () => {}
      });
    }

    // Następnie pobierz graczy z wzbogaconymi statystykami
    this.hattrickApi.getPlayers(teamId).subscribe({
      next: (players) => {
        if (teamId !== Number(this.opponentTeamId)) return;
        this.opponentTeamPlayers = players;
        this.lastLoadedOpponentId = this.opponentTeamId;
        this.loadingOpponentTeam = false;
        this.cache.opponentPlayers$.next(players);
        // Generuj statystyki dla graczy przeciwnika (tylko jeśli brak danych z API)
        this.generatePlayerStats();
        // Zbuduj optymalny skład dla przeciwnika
        this.buildOpponentOptimalLineup();
      },
      error: () => {
        if (teamId !== Number(this.opponentTeamId)) return;
        this.loadingOpponentTeam = false;
      }
    });
  }

  onMyTeamIdChange(): void {
    this.onOptimizerInputChange();
    this.result = null;
    this.cache.optimizerResult$.next(null);
    if (this.myTeamId) {
      // Wyczyść dane jeśli ID się zmieniło
      if (this.lastLoadedMyTeamId !== this.myTeamId) {
        this.myTeamPlayers = [];
        this.myTeamName = '';
        this.clearFormationExperience();
        this.myTeamStats = null;
        this.cache.myTeamStats$.next(null);
      }
      this.loadMyTeam();
      this.loadMyTeamStats();
    }
  }

  onOpponentTeamIdChange(): void {
    this.onOptimizerInputChange();
    this.result = null;
    this.cache.optimizerResult$.next(null);
    if (this.opponentTeamId) {
      // Wyczyść dane jeśli ID się zmieniło
      if (this.lastLoadedOpponentId !== this.opponentTeamId) {
        this.opponentTeamPlayers = [];
        this.opponentTeamName = '';
        this.opponentTeamStats = null;
        this.opponentOptimalLineup = null;
        this.opponentScout = null;
        this.cache.opponentPlayers$.next(null);
        this.cache.opponentTeamStats$.next(null);
        this.cache.opponentOptimalLineup$.next(null);
        this.cache.opponentScout$.next(null);
      }
      this.loadOpponentTeam();
      this.loadOpponentTeamStats();
      this.loadOpponentScout();
    }
  }

  loadOpponentScout(): void {
    if (!this.opponentTeamId) return;
    const teamId = Number(this.opponentTeamId);

    const cached = this.cache.opponentScout$.value;
    if (cached?.teamId === this.opponentTeamId) {
      this.opponentScout = cached;
      this.buildOpponentOptimalLineup();
      return;
    }

    this.loadingOpponentScout = true;
    this.hattrickApi.getOpponentScout(teamId).subscribe({
      next: (report) => {
        if (teamId !== Number(this.opponentTeamId)) return;
        this.opponentScout = report;
        this.loadingOpponentScout = false;
        this.cache.opponentScout$.next(report);
        this.onOptimizerInputChange();
        this.buildOpponentOptimalLineup();
      },
      error: (err) => {
        if (teamId !== Number(this.opponentTeamId)) return;
        console.error('Error loading opponent scout:', err);
        this.opponentScout = null;
        this.loadingOpponentScout = false;
      }
    });
  }

  // Wysyłanie składu do Hattricka (wymaga tokenu ze scope set_matchorder)
  sendingOrders: boolean = false;
  ordersSuccess: boolean = false;
  ordersError: string | null = null;

  get canSendOrders(): boolean {
    const nextOpp = this.cache.nextOpponent$.value;
    return !this.resultStale && !!this.displayedLineup?.positions
      && !!nextOpp?.matchId
      && nextOpp.opponentTeamId === this.opponentTeamId
      && this.resultTeamContext === this.currentTeamContext();
  }

  sendLineupToHattrick(): void {
    const nextOpp = this.cache.nextOpponent$.value;
    if (!this.canSendOrders || !nextOpp) return;
    const lineup = this.displayedLineup;
    if (!lineup) return;

    const positions: { [slot: string]: { playerId: number; behaviour: string } } = {};
    const players: Player[] = [];
    for (const [slot, pos] of Object.entries(lineup.positions)) {
      if (!pos.player) continue;
      positions[slot] = { playerId: pos.player.playerId, behaviour: pos.behavior };
      players.push(pos.player);
    }
    const validSlots = new Set(['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'FW', 'LFW']);
    if (players.length !== 11 || new Set(players.map(p => p.playerId)).size !== 11
      || Object.keys(positions).length !== 11 || !positions['GK']
      || Object.keys(positions).some(slot => !validSlots.has(slot))) {
      this.ordersError = this.translate.instant('matchOrders.invalidLineup');
      return;
    }

    // Kapitan: najbardziej doświadczony; wykonawca SFG: najlepsze stałe fragmenty.
    const captain = players.reduce((a, b) => ((a.experience ?? 0) >= (b.experience ?? 0) ? a : b));
    const spTaker = players.reduce((a, b) => ((a.skills?.setPieces ?? 0) >= (b.skills?.setPieces ?? 0) ? a : b));

    this.sendingOrders = true;
    this.ordersSuccess = false;
    this.ordersError = null;
    this.hattrickApi.sendMatchOrders({
      matchId: nextOpp.matchId,
      positions,
      tactic: lineup.tacticType,
      attitude: this.activeAlternative?.attitude ?? this.result?.inputSnapshot?.teamAttitude ?? 'Normal',
      assistantManagerLevel: this.result?.inputSnapshot?.assistantManagerLevel ?? 0,
      captainId: captain.playerId,
      setPiecesTakerId: spTaker.playerId
    }).subscribe({
      next: (response) => {
        this.sendingOrders = false;
        if (response?.success !== true) {
          this.ordersError = response?.error ?? response?.message ?? this.translate.instant('matchOrders.failed');
          this.ordersSuccess = false;
        } else {
          this.ordersSuccess = true;
        }
      },
      error: (err) => {
        this.sendingOrders = false;
        this.ordersError = err?.error?.error ?? err?.message ?? 'error';
      }
    });
  }

  getWeatherIcon(weatherId: number): string {
    switch (weatherId) {
      case 0: return '🌧️';
      case 1: return '☁️';
      case 2: return '⛅';
      case 3: return '☀️';
      default: return '';
    }
  }

  getWeatherLabel(weatherId: number): string {
    const keys: { [id: number]: string } = {
      0: 'weather.rain',
      1: 'weather.overcast',
      2: 'weather.partlyCloudy',
      3: 'weather.sunny'
    };
    const key = keys[weatherId];
    return key ? this.translate.instant(key) : this.translate.instant('weather.unknown');
  }

  get scoutTacticSummary(): string {
    if (!this.opponentScout) return '';
    return Object.entries(this.opponentScout.tacticCounts)
      .sort((a, b) => b[1] - a[1])
      .map(([tactic, count]) => `${this.getTacticLabel(tactic)} (${count})`)
      .join(', ');
  }

  get myTeamAverageAge(): string {
    if (this.myTeamPlayers.length === 0) return '0.0';
    const sum = this.myTeamPlayers.reduce((acc, p) => acc + p.age, 0);
    return (sum / this.myTeamPlayers.length).toFixed(1);
  }

  get myTeamAverageForm(): string {
    if (this.myTeamPlayers.length === 0) return '0.0';
    const sum = this.myTeamPlayers.reduce((acc, p) => acc + p.form, 0);
    return (sum / this.myTeamPlayers.length).toFixed(1);
  }

  get opponentTeamAverageAge(): string {
    if (this.opponentTeamPlayers.length === 0) return '0.0';
    const sum = this.opponentTeamPlayers.reduce((acc, p) => acc + p.age, 0);
    return (sum / this.opponentTeamPlayers.length).toFixed(1);
  }

  get opponentTeamAverageForm(): string {
    if (this.opponentTeamPlayers.length === 0) return '0.0';
    const sum = this.opponentTeamPlayers.reduce((acc, p) => acc + p.form, 0);
    return (sum / this.opponentTeamPlayers.length).toFixed(1);
  }

  loadMyTeamStats(): void {
    if (!this.myTeamId) return;
    const teamId = Number(this.myTeamId);

    if (this.cache.myTeamStats$.value) {
      this.myTeamStats = this.cache.myTeamStats$.value;
      return;
    }

    this.loadingMyTeamStats = true;
    this.hattrickApi.getTeamMatchStats(teamId).subscribe({
      next: (stats: any) => {
        if (teamId !== Number(this.myTeamId)) return;
        this.myTeamStats = stats;
        this.loadingMyTeamStats = false;
        this.cache.myTeamStats$.next(stats);
        this.onOptimizerInputChange();
      },
      error: (err: any) => {
        if (teamId !== Number(this.myTeamId)) return;
        // Bez fallbacku do mocków — sekcja statystyk pozostaje pusta, błąd w konsoli.
        console.error('Error loading team stats:', err);
        this.myTeamStats = null;
        this.loadingMyTeamStats = false;
      }
    });
  }

  loadOpponentTeamStats(): void {
    if (!this.opponentTeamId) return;
    const teamId = Number(this.opponentTeamId);

    if (this.cache.opponentTeamStats$.value) {
      this.opponentTeamStats = this.cache.opponentTeamStats$.value;
      if (this.opponentTeamPlayers.length > 0 && !this.opponentOptimalLineup) {
        this.buildOpponentOptimalLineup();
      }
      return;
    }

    this.loadingOpponentTeamStats = true;
    this.hattrickApi.getTeamMatchStats(teamId).subscribe({
      next: (stats: any) => {
        if (teamId !== Number(this.opponentTeamId)) return;
        this.opponentTeamStats = stats;
        this.loadingOpponentTeamStats = false;
        this.cache.opponentTeamStats$.next(stats);
        this.onOptimizerInputChange();
        // Przebuduj skład przeciwnika z poprawną formacją
        if (this.opponentTeamPlayers.length > 0) {
          this.buildOpponentOptimalLineup();
        }
      },
      error: (err: any) => {
        if (teamId !== Number(this.opponentTeamId)) return;
        // Bez fallbacku do mocków — skład przeciwnika budujemy z dostępnych danych graczy.
        console.error('Error loading opponent stats:', err);
        this.opponentTeamStats = null;
        this.loadingOpponentTeamStats = false;
        if (this.opponentTeamPlayers.length > 0) {
          this.buildOpponentOptimalLineup();
        }
      }
    });
  }

  get winRate(): string {
    if (!this.myTeamStats?.statistics) return '0.0';
    const stats = this.myTeamStats.statistics;
    return stats.totalMatches > 0 ? stats.winRate.toFixed(1) : '0.0';
  }

  get goalDifference(): number {
    if (!this.myTeamStats?.statistics) return 0;
    return this.myTeamStats.statistics.goalDifference;
  }

  // ==================== SORTOWANIE GRACZY ====================
  
  sortPlayers(column: string): void {
    if (this.playerSortColumn === column) {
      this.playerSortDirection = this.playerSortDirection === 'asc' ? 'desc' : 'asc';
    } else {
      this.playerSortColumn = column;
      this.playerSortDirection = 'desc';
    }
  }

  get sortedMyTeamPlayers(): Player[] {
    if (!this.myTeamPlayers || this.myTeamPlayers.length === 0) return [];
    
    return [...this.myTeamPlayers].sort((a, b) => {
      let valueA = this.getPlayerSortValue(a, this.playerSortColumn);
      let valueB = this.getPlayerSortValue(b, this.playerSortColumn);
      
      if (typeof valueA === 'string') {
        valueA = valueA.toLowerCase();
        valueB = (valueB as string).toLowerCase();
      }
      
      if (valueA < valueB) return this.playerSortDirection === 'asc' ? -1 : 1;
      if (valueA > valueB) return this.playerSortDirection === 'asc' ? 1 : -1;
      return 0;
    });
  }

  getPlayerSortValue(player: Player, column: string): any {
    switch (column) {
      case 'name': return `${player.firstName} ${player.lastName}`;
      case 'age': return player.age;
      case 'form': return player.form;
      case 'stamina': return player.stamina;
      case 'keeper': return player.skills?.keeper || 0;
      case 'defending': return player.skills?.defending || 0;
      case 'playmaking': return player.skills?.playmaking || 0;
      case 'winger': return player.skills?.winger || 0;
      case 'passing': return player.skills?.passing || 0;
      case 'scoring': return player.skills?.scoring || 0;
      case 'setPieces': return player.skills?.setPieces || 0;
      case 'goals': return player.matchStats?.goals || 0;
      case 'assists': return player.matchStats?.assists || 0;
      case 'avgForm': return player.matchStats?.averageForm || player.form;
      case 'goalsPerMatch': return player.matchStats?.goalsPerMatch || 0;
      case 'matchesPerGoal': return player.matchStats?.matchesPerGoal || 999;
      default: return 0;
    }
  }

  // ==================== ZMIANA FORMACJI I TAKTYKI ====================
  
  onMyFormationChange(): void {
    this.onOptimizerInputChange();
    this.persistUiState();
    // Po zmianie formacji natychmiast przelicz sklad z ograniczeniem do tej formacji.
    if (this.myTeamId && this.opponentTeamId && this.myTeamPlayers.length >= 11) {
      this.optimizeLineup();
    }
  }

  onMyTacticChange(): void {
    this.onOptimizerInputChange();
    this.preferredTactic = this.selectedMyTactic;
    this.persistUiState();
    if (this.result && this.myTeamId && this.opponentTeamId) {
      this.optimizeLineup();
    }
  }

  onOpponentFormationChange(): void {
    this.persistUiState();
    if (this.selectedOpponentFormation !== 'Auto' && this.opponentTeamPlayers.length > 0) {
      this.buildOpponentOptimalLineup();
    }
  }

  onOpponentTacticChange(): void {
    this.persistUiState();
    if (this.opponentTeamPlayers.length > 0) {
      this.buildOpponentOptimalLineup();
    }
  }

  // ==================== OPTIMAL LINEUP DLA PRZECIWNIKA ====================

  buildOpponentOptimalLineup(): void {
    const starters = this.opponentScout?.likelyStarters ?? [];
    if (!starters.length) { this.opponentOptimalLineup = null; return; }
    
    // Użyj najczęstszej formacji z ostatnich 5 meczów przeciwnika
    const formation = this.opponentScout?.mostCommonFormation || '—';
    
    const lineup: Lineup = {
      positions: {},
      tacticType: this.opponentScout?.mostCommonTactic || '—',
      tacticSkill: '',
      predictedRatings: {
        midfield: 0, rightDefense: 0, centralDefense: 0, leftDefense: 0,
        rightAttack: 0, centralAttack: 0, leftAttack: 0, overall: 0
      },
      formation: formation
    };

    for (const starter of starters) {
      lineup.positions[starter.slot] = {
        position: starter.slot,
        player: null,
        behavior: 'Unknown'
      };
    }

    this.opponentOptimalLineup = lineup;
  }

  getFormationPositions(formation: string): string[] {
    const formationMap: { [key: string]: string[] } = {
      '5-5-0': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'LW'],
      '5-4-1': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'LIM', 'LW', 'FW'],
      '5-3-2': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RIM', 'IM', 'LIM', 'RFW', 'LFW'],
      '5-2-3': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'LW', 'RFW', 'FW', 'LFW'],
      '4-5-1': ['GK', 'RWB', 'RCD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'FW'],
      '4-4-2': ['GK', 'RWB', 'RCD', 'LCD', 'LWB', 'RW', 'RIM', 'LIM', 'LW', 'RFW', 'LFW'],
      '4-3-3': ['GK', 'RWB', 'RCD', 'LCD', 'LWB', 'RW', 'IM', 'LW', 'RFW', 'FW', 'LFW'],
      '3-5-2': ['GK', 'RCD', 'CD', 'LCD', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'LFW'],
      '3-4-3': ['GK', 'RCD', 'CD', 'LCD', 'RW', 'RIM', 'LIM', 'LW', 'RFW', 'FW', 'LFW'],
      '2-5-3': ['GK', 'RCD', 'LCD', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'FW', 'LFW']
    };
    return formationMap[formation] || formationMap['4-4-2'];
  }

  findBestPlayerForPosition(position: string, players: Player[], usedPlayers: Set<number>): Player | null {
    const available = players.filter(p => !usedPlayers.has(p.playerId));
    if (available.length === 0) return null;

    return available.reduce((best, player) => {
      const bestScore = this.getPositionScore(best, position);
      const playerScore = this.getPositionScore(player, position);
      return playerScore > bestScore ? player : best;
    });
  }

  getPositionScore(player: Player, position: string): number {
    // Sprawdź czy gracz ma ocenę na tej pozycji z poprzednich meczów
    if (player.matchStats?.positionRatings?.[position]) {
      return player.matchStats.positionRatings[position];
    }

    // Jeśli nie ma rzeczywistych ocen, użyj calculateSkillBasedRating
    return this.calculateSkillBasedRating(player, position);
  }

  getBestFormationForTeam(players: Player[]): string {
    // Analiza składu i wybór najlepszej formacji
    const defenders = players.filter(p => p.skills.defending > 10).length;
    const midfielders = players.filter(p => p.skills.playmaking > 10).length;
    const forwards = players.filter(p => p.skills.scoring > 10).length;
    const wingers = players.filter(p => p.skills.winger > 10).length;

    if (defenders >= 5 && midfielders >= 4) return '5-4-1';
    if (midfielders >= 5) return '4-5-1';
    if (forwards >= 3 && wingers >= 2) return '4-3-3';
    if (defenders >= 4 && forwards >= 2) return '4-4-2';
    if (midfielders >= 5 && forwards >= 2) return '3-5-2';
    
    return '4-4-2'; // Domyślna
  }

  assignPlayersToFormation(formation: string, players: Player[]): void {
    // Ta metoda może być używana do wizualizacji przypisania
    // W praktyce optymalizator robi to automatycznie
  }

  // ==================== GENEROWANIE STATYSTYK GRACZY ====================

  generatePlayerStats(): void {
    // Nie generujemy mockowych ocen - używamy tylko rzeczywistych danych z API
    this.myTeamPlayers = this.myTeamPlayers.map(player => {
      if (!player.matchStats) {
        player.matchStats = {
          totalMatches: 0,
          goals: 0,
          assists: 0,
          yellowCards: 0,
          redCards: 0,
          averageRating: 0,
          averageForm: player.form,
          goalsPerMatch: 0,
          matchesPerGoal: 0,
          minutesPlayed: 0,
          positionRatings: {}
        };
      }
      return player;
    });
    
    this.opponentTeamPlayers = this.opponentTeamPlayers.map(player => {
      if (!player.matchStats) {
        player.matchStats = {
          totalMatches: 0,
          goals: 0,
          assists: 0,
          yellowCards: 0,
          redCards: 0,
          averageRating: 0,
          averageForm: player.form,
          goalsPerMatch: 0,
          matchesPerGoal: 0,
          minutesPlayed: 0,
          positionRatings: {}
        };
      }
      return player;
    });
  }

  calculateSkillBasedRating(player: Player, position: string): number {
    const skills = player.skills;
    // Skala Hattrick 0-20 (ocena meczowa). Bramkarz magiczny (19) powinien dawac ~9 na start.
    // Forma 1-9 -> mnoznik 0.7-1.1, kondycja 1-9 -> 0.9-1.05.
    const formMult = 0.6 + (player.form / 9) * 0.5;
    const staminaMult = 0.9 + (player.stamina / 9) * 0.15;
    const eff = formMult * staminaMult;

    let main = 0;
    switch (position) {
      case 'GK':
        main = skills.keeper;
        break;
      case 'RWB':
      case 'LWB':
        main = 0.7 * skills.defending + 0.3 * skills.winger;
        break;
      case 'RCD':
      case 'LCD':
      case 'CD':
        main = skills.defending;
        break;
      case 'RW':
      case 'LW':
        main = 0.6 * skills.winger + 0.4 * skills.playmaking;
        break;
      case 'RIM':
      case 'LIM':
      case 'IM':
        main = skills.playmaking;
        break;
      case 'RFW':
      case 'LFW':
      case 'FW':
        main = skills.scoring;
        break;
      default:
        main = skills.playmaking;
    }

    // Wspolczynnik 0.4 kalibruje wynik do rzeczywistych ocen meczowych Hattrick.
    return Math.max(0, Math.min(20, main * 0.4 * eff));
  }

  getPlayerPositionRating(player: Player, position: string, backendRating?: number): string {
    // Prefer the selected optimizer result so ratings change together with its lineup.
    if (backendRating !== undefined && backendRating > 0) {
      return backendRating.toFixed(1);
    }
    // Use observed CHPP ratings only when the result did not include a placement rating.
    const real = player.matchStats?.positionRatings?.[position];
    if (real !== undefined && real > 0) {
      return real.toFixed(1);
    }
    // Fallback: frontendowe oszacowanie na podstawie umiejetnosci.
    return this.calculateSkillBasedRating(player, position).toFixed(1);
  }

  // ==================== POMOCNICZE ====================

  getOpponentPositionKeys(): string[] {
    return this.opponentScout?.likelyStarters.map(s => s.slot) ?? [];
  }

  getOpponentPositionRows(): string[][] {
    const positions = this.getOpponentPositionKeys();
    const gk = positions.filter(p => p === 'GK');
    const defenders = positions.filter(p => ['RWB', 'RCD', 'CD', 'LCD', 'LWB', 'CDO', 'CDTW', 'WBD', 'WBN', 'WBO', 'WBTM'].includes(p));
    const midfielders = positions.filter(p => ['RW', 'RIM', 'IM', 'LIM', 'LW', 'WO', 'WD', 'WTM'].includes(p));
    const forwards = positions.filter(p => ['RFW', 'FW', 'LFW', 'FTW', 'DF'].includes(p));
    const known = new Set([...gk, ...defenders, ...midfielders, ...forwards]);
    midfielders.push(...positions.filter(p => !known.has(p)));
    return [gk, defenders, midfielders, forwards];
  }

  getOpponentStarter(position: string): ScoutLikelyStarter | null {
    return this.opponentScout?.likelyStarters.find(s => s.slot === position) ?? null;
  }

  // Sektory uzywane w wykresie porownania (7 aspektow Hattrick)
  comparisonSectors: { key: keyof import('../../models/lineup.model').LineupRatings; label: string }[] = [
    { key: 'midfield', label: 'optimizer.comparison.midfield' },
    { key: 'leftDefense', label: 'optimizer.comparison.leftDefense' },
    { key: 'centralDefense', label: 'optimizer.comparison.centralDefense' },
    { key: 'rightDefense', label: 'optimizer.comparison.rightDefense' },
    { key: 'leftAttack', label: 'optimizer.comparison.leftAttack' },
    { key: 'centralAttack', label: 'optimizer.comparison.centralAttack' },
    { key: 'rightAttack', label: 'optimizer.comparison.rightAttack' }
  ];

  get attackDefenseMatchups(): Array<{ lane: string; opponentLane: string; myKey: keyof LineupRatings; opponentKey: keyof LineupRatings }> {
    return [
      { lane: 'optimizer.comparison.lanes.right', opponentLane: 'optimizer.comparison.lanes.left', myKey: 'rightAttack', opponentKey: 'leftDefense' },
      { lane: 'optimizer.comparison.lanes.center', opponentLane: 'optimizer.comparison.lanes.center', myKey: 'centralAttack', opponentKey: 'centralDefense' },
      { lane: 'optimizer.comparison.lanes.left', opponentLane: 'optimizer.comparison.lanes.right', myKey: 'leftAttack', opponentKey: 'rightDefense' }
    ];
  }

  get defenseAttackMatchups(): Array<{ lane: string; opponentLane: string; myKey: keyof LineupRatings; opponentKey: keyof LineupRatings }> {
    return [
      { lane: 'optimizer.comparison.lanes.right', opponentLane: 'optimizer.comparison.lanes.left', myKey: 'rightDefense', opponentKey: 'leftAttack' },
      { lane: 'optimizer.comparison.lanes.center', opponentLane: 'optimizer.comparison.lanes.center', myKey: 'centralDefense', opponentKey: 'centralAttack' },
      { lane: 'optimizer.comparison.lanes.left', opponentLane: 'optimizer.comparison.lanes.right', myKey: 'leftDefense', opponentKey: 'rightAttack' }
    ];
  }

  localizeText(text: string): string {
    const parts = text.split(' / ');
    if (parts.length < 2) return text;
    return this.translate.currentLang === 'en' ? parts[1].trim() : parts[0].trim();
  }

  getSectorBarWidth(myValue: number, oppValue: number, side: 'my' | 'opp'): number {
    const total = (myValue || 0) + (oppValue || 0);
    if (total <= 0) return 50;
    const pct = ((side === 'my' ? myValue : oppValue) / total) * 100;
    return Math.max(5, Math.min(95, pct));
  }

  // Przewidywana liczba akcji w meczu wg poradnik (Umanx):
  // 6 akcji wspolnych + 4 unikatowe na druzyne. Szansa na akcje: x^a / (x^a + y^a),
  // gdzie a ~ 2.75 (przedzial 2.5-3), x/y = poziom pomocy. Max 10 akcji na zespol.
  getPredictedActions(myMid: number, oppMid: number, side: 'my' | 'opp'): number {
    return 10 * this.getPredictedChanceShare(myMid, oppMid, side);
  }

  getPredictedChanceShare(myMid: number, oppMid: number, side: 'my' | 'opp'): number {
    const x = Math.max(myMid || 0, 0.01);
    const y = Math.max(oppMid || 0, 0.01);
    const a = 2.75; // Shared midfield exponent in the backend match model.
    const xa = Math.pow(x, a);
    const ya = Math.pow(y, a);
    const pMy = xa / (xa + ya);
    return side === 'my' ? pMy : 1 - pMy;
  }

  // Przewidywana liczba bramek: akcje * sredni P(gol) wazony rozkladem akcji.
  // Rozklad: 35% srodek, 25% prawe skrzydlo, 25% lewe skrzydlo, 15% SFG.
  // SFG przyblizone porownaniem srodek-srodek (brak ratingu SP w modelu).
  // Wzor finalizacji: x^a / (x^a + y^a), a ~ 3.5.
  getExpectedGoals(result: any, side: 'my' | 'opp'): number {
    const my = result.comparison.myTeamRatings;
    const opp = result.comparison.opponentRatings;
    const actions = this.getPredictedActions(my.midfield, opp.midfield, side);
    const attack = side === 'my' ? my : opp;
    const defense = side === 'my' ? opp : my;
    const a = 3.5;
    const p = (x: number, y: number) => {
      const xa = Math.pow(Math.max(x || 0, 0.01), a);
      const ya = Math.pow(Math.max(y || 0, 0.01), a);
      return xa / (xa + ya);
    };
    const pCentral = p(attack.centralAttack, defense.centralDefense);
    const pRight = p(attack.rightAttack, defense.leftDefense);
    const pLeft = p(attack.leftAttack, defense.rightDefense);
    const pGoal = 0.35 * pCentral + 0.25 * pRight + 0.25 * pLeft + 0.15 * pCentral;
    return actions * pGoal;
  }

  getDisplayedExpectedGoals(side: 'my' | 'opp'): number {
    if (side === 'my' && this.activeAlternative) return this.activeAlternative.expectedGoalsFor;
    if (side === 'opp' && this.activeAlternative) return this.activeAlternative.expectedGoalsAgainst;
    return this.result ? this.getExpectedGoals(this.result, side) : 0;
  }

  getSkillLevel(value: number): string {
    if (!Number.isInteger(value) || value < 0 || value > 20) return this.translate.instant('playerAbilities.0');
    return this.translate.instant(`playerAbilities.${value}`);
  }
}
