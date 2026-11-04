import { Component, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { TranslateService } from '@ngx-translate/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { TrainingPlayerEntry, TrainingSummary } from '../../models/training.model';
import { Player } from '../../models/player.model';
import { DataCacheService, NextOpponent } from '../../services/data-cache.service';
import { TRAINING_SLOTS, TrainingAssessment, TrainingAssignment, TrainingMatch, WeeklyPlanService, WeeklyTrainingPlan, trainingFactor, trainingMatchKind } from '../../services/weekly-plan.service';

@Component({
  selector: 'app-training-view',
  templateUrl: './training-view.component.html',
  styleUrls: ['./training-view.component.scss']
})
export class TrainingViewComponent implements OnInit, OnDestroy {
  private readonly destroy$ = new Subject<void>();
  summary: TrainingSummary | null = null;
  loading = false;
  error: string | null = null;
  roster: Player[] = [];
  plan: WeeklyTrainingPlan | null = null;
  assessment: TrainingAssessment = { players: [], issues: [], ready: false };
  readonly slots = TRAINING_SLOTS;
  readonly matches: TrainingMatch[] = ['league', 'friendly'];
  storageError = false;
  search = '';
  suggestionMade = false;
  unassignedNames: string[] = [];
  unknownNames: string[] = [];
  upcomingMatches: NextOpponent[] = [];
  fixturesError = false;

  constructor(
    private hattrickApi: HattrickApiService,
    private translate: TranslateService,
    private cache: DataCacheService,
    private weeklyPlans: WeeklyPlanService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.cache.ownTeam$.pipe(takeUntil(this.destroy$)).subscribe(team => {
      if (team) { this.roster = team.players; this.recalculate(); }
    });
    this.cache.nextOpponent$.pipe(takeUntil(this.destroy$)).subscribe(() => this.applyNextMatchDefault());
    this.hattrickApi.getUpcomingMatches().pipe(takeUntil(this.destroy$)).subscribe({
      next: matches => { this.upcomingMatches = matches; this.applyNextMatchDefault(); },
      error: () => { this.fixturesError = true; }
    });
    this.loadSummary();
  }

  ngOnDestroy(): void { this.destroy$.next(); this.destroy$.complete(); }

  loadSummary(): void {
    this.loading = true;
    this.error = null;
    this.hattrickApi.getTrainingSummary().pipe(takeUntil(this.destroy$)).subscribe({
      next: (summary) => {
        this.summary = summary;
        this.plan = this.weeklyPlans.getPlan(summary.teamId, summary.weekStart ?? '');
        this.applyNextMatchDefault();
        this.recalculate();
        this.loading = false;
      },
      error: (err) => {
        this.error = err?.error?.error ?? err?.message ?? 'error';
        this.loading = false;
      }
    });
  }

  get filteredRoster(): Player[] {
    const query = this.search.toLocaleLowerCase();
    return this.roster.filter(p => `${p.firstName} ${p.lastName}`.toLocaleLowerCase().includes(query));
  }

  get selectedPlayers(): Player[] {
    return this.roster.filter(p => this.plan?.selectedPlayerIds.includes(p.playerId));
  }

  togglePlayer(playerId: number): void {
    if (!this.plan) return;
    this.plan.selectedPlayerIds = this.plan.selectedPlayerIds.includes(playerId)
      ? this.plan.selectedPlayerIds.filter(id => id !== playerId) : [...this.plan.selectedPlayerIds, playerId];
    this.save();
  }

  assignments(match: TrainingMatch): TrainingAssignment[] {
    return this.plan?.assignments.filter(a => a.match === match) ?? [];
  }

  addAssignment(match: TrainingMatch): void {
    if (!this.plan || !this.selectedPlayers.length || !this.summary) return;
    const assigned = this.assignments(match);
    const player = this.selectedPlayers.find(p => !assigned.some(a => a.playerId === p.playerId)) ?? this.selectedPlayers[0];
    const preferred = this.slots.filter(slot => trainingFactor(this.summary!.trainingTypeCode, slot) === 1);
    const slot = preferred.find(s => !assigned.some(a => a.slot === s)) ?? preferred[0] ?? 'IM';
    this.plan.assignments.push({ playerId: player.playerId, match, slot, startMinute: 0, minutes: 90 });
    this.save();
  }

  removeAssignment(assignment: TrainingAssignment): void {
    if (!this.plan) return;
    this.plan.assignments = this.plan.assignments.filter(a => a !== assignment);
    this.save();
  }

  suggestAssignments(): void {
    if (!this.plan || !this.summary) return;
    const suggested = this.weeklyPlans.suggestAssignments(this.plan, this.summary, this.roster);
    this.plan = suggested.plan;
    this.unassignedNames = suggested.unassignedPlayerIds.map(id => this.playerName(id));
    this.unknownNames = suggested.unknownPlayerIds.map(id => this.playerName(id));
    this.suggestionMade = true;
    this.save();
  }

  save(): void {
    if (!this.plan) return;
    this.storageError = !this.weeklyPlans.savePlan(this.plan);
    this.recalculate();
  }

  recalculate(): void {
    if (this.plan && this.summary) this.assessment = this.weeklyPlans.evaluate(this.plan, this.summary, this.roster);
  }

  playerName(playerId: number): string {
    const player = this.roster.find(p => p.playerId === playerId);
    return player ? `${player.firstName} ${player.lastName}` : `#${playerId}`;
  }

  factorLabel(slot: string): string {
    const factor = this.summary ? trainingFactor(this.summary.trainingTypeCode, slot) : null;
    return factor === null ? this.translate.instant('planning.unquantified') : `${factor * 100}%`;
  }

  canPrepare(match: TrainingMatch): boolean {
    return this.assignments(match).some(a => a.startMinute === 0)
      && !this.assessment.issues.some(i => i.severity === 'error' && (!i.match || i.match === match));
  }

  prepareMatch(match: TrainingMatch): void {
    if (!this.canPrepare(match)) return;
    const starters = this.assignments(match).filter(a => a.startMinute === 0);
    const lockedPositions = Object.fromEntries(starters.map(a => [a.slot, a.playerId]));
    const trainingMatchDate = new Date(this.plan!.matchDates[match]).toISOString();
    const fixture = this.fixtureForPlan(match);
    this.router.navigate(['/optimizer'], { queryParams: {
      traineeIds: starters.map(a => a.playerId).join(','),
      lockedPositions: JSON.stringify(lockedPositions), trainingMatch: match,
      trainingWeek: this.plan?.weekStart, trainingMatchDate,
      trainingAssignments: JSON.stringify(this.assignments(match)),
      trainingSlots: [...new Set(this.assignments(match).map(a => a.slot))].join(','),
      trainingExcludedIds: this.assignments(match).filter(a => !starters.some(s => s.playerId === a.playerId)).map(a => a.playerId).join(','),
      opponentId: fixture?.opponentTeamId, matchId: fixture?.matchId
    } });
  }

  fixturesFor(match: TrainingMatch): NextOpponent[] {
    return this.upcomingMatches.filter(fixture => trainingMatchKind(fixture.matchType) === match);
  }

  fixtureForPlan(match: TrainingMatch): NextOpponent | undefined {
    const kickoff = Date.parse(this.plan?.matchDates[match] ?? '');
    return this.fixturesFor(match).find(fixture => Math.abs(Date.parse(fixture.matchDate ?? '') - kickoff) < 60000);
  }

  selectFixture(match: TrainingMatch, matchId: number): void {
    if (!this.plan) return;
    const fixture = this.fixturesFor(match).find(item => item.matchId === Number(matchId));
    this.plan.matchDates[match] = fixture?.matchDate ? this.toLocalInput(fixture.matchDate) : '';
    this.save();
  }

  private applyNextMatchDefault(): void {
    const next = this.cache.nextOpponent$.value;
    if (!this.plan || !this.summary) return;
    const through = Date.parse(this.summary.weekEnd ?? '');
    const deadline = Date.parse(this.summary.nextTrainingAt ?? '') || Date.parse(this.plan.weekStart) + 7 * 86400000;
    const fixtures = [...this.upcomingMatches, ...(next ? [next] : [])]
      .sort((a, b) => Date.parse(a.matchDate ?? '') - Date.parse(b.matchDate ?? ''));
    for (const fixture of fixtures) {
      const kind = trainingMatchKind(fixture.matchType);
      const kickoff = Date.parse(fixture.matchDate ?? '');
      if (kind && fixture.matchDate && !this.plan.matchDates[kind] && kickoff > through && kickoff + 90 * 60000 <= deadline) {
        this.plan.matchDates[kind] = this.toLocalInput(fixture.matchDate);
      }
    }
    this.recalculate();
  }

  private toLocalInput(value: string): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  }

  getTrainingTypeLabel(): string {
    if (!this.summary) return '';
    const unknownType = /^Unknown(?:\((\d+)\))?$/.exec(this.summary.trainingTypeName);
    if (unknownType) {
      const label = this.translate.instant('training.types.Unknown');
      return unknownType[1] ? `${label} (${unknownType[1]})` : label;
    }
    const key = `training.types.${this.summary.trainingTypeName}`;
    const translated = this.translate.instant(key);
    return translated === key ? this.summary.trainingTypeName : translated;
  }

  getSkillLevelName(value: number): string {
    if (!Number.isInteger(value) || value < 0 || value > 20) return this.translate.instant('training.skillUnknown');
    return this.translate.instant(`playerAbilities.${value}`);
  }

  getTrainedSkillLabel(player: TrainingPlayerEntry): string {
    if (player.trainedSkillAvailable === false) return this.translate.instant('training.skillUnknown');
    return `${player.trainedSkillValue} (${this.getSkillLevelName(player.trainedSkillValue)})`;
  }

  get summaryWarnings(): string[] {
    if (!this.summary) return [];
    const warnings = this.summary.warnings ?? [];
    const translated = warnings.map(w => this.translateTrainingWarning(w));
    if (this.summary.isEstimate && !translated.some(w => w === this.translate.instant('training.warnings.weekWindowEstimate')
      || w === this.translate.instant('training.warnings.estimateWarning'))) {
      translated.unshift(this.translate.instant('training.warnings.estimateWarning'));
    }
    return translated;
  }

  hasPlayerWarning(warnings?: string[]): boolean {
    return !!warnings?.length;
  }

  getPlayerWarnings(warnings?: string[]): string[] {
    return (warnings ?? []).map(w => this.translateTrainingWarning(w));
  }

  private translateTrainingWarning(warning: string): string {
    const translations: Array<[string, string]> = [
      ['Training window is the trailing seven UTC days', 'weekWindowEstimate'],
      ['Training week uses the documented fallback', 'weekWindowFallback'],
      ['Training speed factors were not fully available', 'speedUnavailable'],
      ['Stamina contribution is unknown', 'staminaUnknown'],
      ['Manual classifies this position as a small effect', 'smallEffectUnknown'],
      ['lineup data unavailable', 'lineupUnavailable'],
      ['CHPP omitted PlayedMinutes', 'minutesUnavailable'],
      ['CHPP lineup has no substitution timeline', 'substitutionUnknown'],
      ['Training speed is partial', 'speedUnavailable'],
      ['Assistant-coach skill levels were unavailable', 'assistantSkillUnknown'],
      ['CHPP did not provide current Morale', 'teamSpiritUnknown'],
      ['CHPP did not provide current SelfConfidence', 'confidenceUnknown'],
      ['CHPP did not provide a recognized TrainingType', 'trainingTypeUnknown'],
      ['TrainingLevel or StaminaTrainingPart is unavailable', 'trainingSettingsUnknown'],
      ['Training week uses the documented fallback', 'weekWindowFallback'],
      ['lineup data unavailable', 'lineupUnavailable'],
      ['CHPP did not identify the set-pieces taker', 'setPiecesTakerUnknown'],
      ['CHPP omitted PlayedMinutes', 'minutesUnavailable'],
      ['CHPP supplied no position-change timeline', 'positionTimelineUnknown'],
      ['CHPP included substitution data but no per-player position-minute segments', 'positionSegmentsUnknown'],
      ['Training type is unavailable', 'trainingTypeUnknown'],
      ['CHPP position code is unknown', 'positionUnknown'],
      ['official manual classifies this position as a small effect', 'smallEffectUnknown'],
      ['official manual classifies this position as a very small', 'verySmallEffectUnknown'],
      ['Set-piece taker is unknown for', 'setPiecesMinutesUnknown'],
      ['Stamina contribution is unknown', 'staminaUnknown'],
      ['Current trained skill was not available', 'trainedSkillUnknown'],
      ['No official minutes could be reconstructed', 'minutesUnavailable'],
      ['unsupported or ambiguous substitution timeline', 'substitutionUnknown'],
      ['official lineup timeline is ambiguous', 'timelineUnknown'],
      ['timeline minutes assume a standard 90-minute regulation match', 'matchDetailsUnavailable'],
      ['Current CHPP stafflist 1.2 was unavailable', 'staffListUnavailable'],
      ['TrainerSkillLevel was missing or outside', 'coachSkillMalformed'],
      ['TrainerType was missing or outside', 'coachTypeMalformed'],
      ['assistant StaffLevel was missing or outside', 'assistantSkillMalformed']
    ];
    const normalizedWarning = warning.toLowerCase();
    const entry = translations.find(([prefix]) => normalizedWarning.includes(prefix.toLowerCase()));
    if (entry) return this.translate.instant(`training.warnings.${entry[1]}`);
    return this.translate.currentLang === 'en' ? warning : `${this.translate.instant('training.warnings.additional')}: ${warning}`;
  }
}
