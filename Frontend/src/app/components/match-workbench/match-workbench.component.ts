import { Component, EventEmitter, Input, OnChanges, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { firstValueFrom } from 'rxjs';
import { Lineup, LineupRatings, OptimizerResponse } from '../../models/lineup.model';
import { Player } from '../../models/player.model';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { MatchPlanService, SavedMatchPlan, rosterFingerprint } from '../../services/match-plan.service';
import { NextOpponent } from '../../services/data-cache.service';
import { trainingFactor, WeeklyPlanService } from '../../services/weekly-plan.service';
import { TrainingSummary } from '../../models/training.model';

export const BEHAVIOURS: Record<string, string[]> = {
  GK: ['GK'], RWB: ['RWB','WBO','WBD','WBTM'], LWB: ['LWB','WBO','WBD','WBTM'],
  RCD: ['RCD','CDO','CDTW'], CD: ['CD','CDO'], LCD: ['LCD','CDO','CDTW'],
  RW: ['RW','WO','WD','WTM'], LW: ['LW','WO','WD','WTM'],
  RIM: ['RIM','IMO','IMD','IMTW'], IM: ['IM','IMO','IMD'], LIM: ['LIM','IMO','IMD','IMTW'],
  RFW: ['RFW','DF','FTW'], FW: ['FW','DF','FTW'], LFW: ['LFW','DF','FTW']
};
export const SECTORS: (keyof LineupRatings)[] = ['midfield','rightDefense','centralDefense','leftDefense','rightAttack','centralAttack','leftAttack'];
interface EditorRow { slot: string; playerId: number; behaviour: string; }
interface Substitution { playerOutId: number; playerInId: number; minute: number; position: number; behaviour: number; standing: number; }

@Component({
  selector: 'app-match-workbench', standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, TranslateModule],
  templateUrl: './match-workbench.component.html', styleUrls: ['./match-workbench.component.scss']
})
export class MatchWorkbenchComponent implements OnChanges {
  @Input() response!: OptimizerResponse;
  @Input() lineup!: Lineup;
  @Input() reference: OptimizerResponse | null = null;
  @Input() players: Player[] = [];
  @Input() stale = false;
  @Input() mockMode = false;
  @Input() nextMatch: NextOpponent | null = null;
  @Input() selectedAlternative = 0;
  @Input() teamContextKey = '';
  @Output() evaluated = new EventEmitter<OptimizerResponse>();
  rows: EditorRow[] = [];
  tactic = 'Normal';
  captainId = 0;
  setPiecesTakerId = 0;
  bench = Array(7).fill(0) as number[];
  kickers = Array(11).fill(0) as number[];
  substitutions: Substitution[] = [];
  dirty = false;
  busy = false;
  preview: any = null;
  message = '';
  error = '';
  captureMessage = '';
  lastLineup?: Lineup;
  lastKey = '';
  private operationSequence = 0;
  readonly sectors = SECTORS;
  readonly tactics = ['Normal','Counter','AttackInMiddle','AttackOnWings','Pressing','PlayCreatively','LongShots'];
  readonly benchRoles = ['GK','CD','WB','IM','FW','W','extra'];
  readonly standingCodes = [-1,0,1,2,3,4,5,6,7,8,9];
  scenarios: { label: string; current: number; recommended: number }[] = [];
  training?: TrainingSummary;
  private trainingTeamId = 0;

  constructor(private api: HattrickApiService, private plans: MatchPlanService, private translate: TranslateService, private weekly: WeeklyPlanService) {}

  ngOnChanges(): void {
    this.operationSequence++;
    const teamId = this.response?.inputSnapshot?.myTeamId;
    if (teamId && teamId !== this.trainingTeamId) {
      this.trainingTeamId = teamId;
      this.api.getTrainingSummary().subscribe({ next: t => { if (t.teamId === this.trainingTeamId) this.training = t; }, error: () => {} });
    }
    if (this.lineup && this.lineup !== this.lastLineup) {
      this.lastLineup = this.lineup;
      this.rows = Object.entries(this.lineup.positions).map(([slot, pos]) => ({ slot, playerId: pos.player?.playerId ?? 0, behaviour: pos.behavior }));
      this.tactic = this.lineup.tacticType;
      this.dirty = false;
      this.preview = null;
      this.scenarios = [];
      const starters = this.rows.map(r => this.players.find(p => p.playerId === r.playerId)).filter((p): p is Player => !!p);
      if (!this.rows.some(r => r.playerId === this.captainId)) this.captainId = [...starters].sort((a,b) => (b.experience + b.leadership) - (a.experience + a.leadership))[0]?.playerId ?? 0;
      if (!this.rows.some(r => r.playerId === this.setPiecesTakerId)) this.setPiecesTakerId = [...starters].sort((a,b) => b.skills.setPieces - a.skills.setPieces)[0]?.playerId ?? 0;
      this.bench = this.bench.map(id => this.rows.some(r => r.playerId === id) ? 0 : id);
      const input = this.response?.inputSnapshot;
      const key = `${input?.myTeamId}:${input?.matchId}:${input?.opponentTeamId}`;
      if (input && key !== this.lastKey) {
        this.lastKey = key;
        const orders = this.plans.getPlan(input.myTeamId, input.matchId ?? 0, input.opponentTeamId)?.orders;
        if (orders) {
          this.bench = Array(7).fill(0);
          for (const item of orders.bench ?? []) this.bench[item.positionCode] = item.playerId;
          this.kickers = Array.from({ length: 11 }, (_,i) => orders.kickers?.[i] ?? 0);
          this.substitutions = (orders.substitutions ?? []).map((s: Substitution) => ({ ...s }));
          this.captainId = orders.captainId;
          this.setPiecesTakerId = orders.setPiecesTakerId;
        }
      }
    }
    if (this.stale) this.preview = null;
  }

  get starters(): Player[] { return this.rows.map(r => this.players.find(p => p.playerId === r.playerId)).filter((p): p is Player => !!p); }
  get reserves(): Player[] { return this.players.filter(p => !this.rows.some(r => r.playerId === p.playerId) && p.canOptimize !== false); }
  get active(): OptimizerResponse['alternatives'][number] | undefined { return this.response.alternatives?.[this.selectedAlternative] ?? this.response.alternatives?.[0]; }
  get penaltyOrder(): number[] { return this.kickers.filter(Boolean).map(Number); }
  get currentRatings(): LineupRatings { return this.active?.ratings ?? this.lineup.predictedRatings; }
  trainingMinutes(lineup: Lineup): number | null {
    if (!this.training) return null;
    const selected = this.weekly.getPlan(this.training.teamId, this.training.weekStart ?? undefined).selectedPlayerIds;
    let total = 0;
    for (const [slot,pos] of Object.entries(lineup.positions)) {
      if (!pos.player || !selected.includes(pos.player.playerId)) continue;
      const factor = trainingFactor(this.training.trainingTypeCode, slot);
      if (factor === null) return null;
      total += 90 * factor;
    }
    return total;
  }
  get referenceRatings(): LineupRatings { return this.reference?.alternatives?.[0]?.ratings ?? this.reference?.optimalLineup.predictedRatings ?? this.currentRatings; }
  get canSend(): boolean {
    const input = this.response.inputSnapshot;
    return !this.mockMode && !this.stale && !this.dirty && !this.busy && !!this.preview
      && !!input?.matchId && this.nextMatch?.matchId === input.matchId
      && this.nextMatch?.opponentTeamId === input.opponentTeamId
      && !!input.matchDate && new Date(input.matchDate).getTime() > Date.now();
  }
  get changes(): string[] {
    const baseline = this.reference?.optimalLineup;
    if (!baseline) return [];
    return this.rows.filter(r => baseline.positions[r.slot]?.player?.playerId !== r.playerId || baseline.positions[r.slot]?.behavior !== r.behaviour)
      .map(r => `${this.translate.instant('optimizer.positions.' + r.slot)}: ${this.playerName(baseline.positions[r.slot]?.player?.playerId ?? 0)} → ${this.playerName(r.playerId)} (${this.behaviourLabel(r.behaviour, r.slot)})`);
  }
  playerName(id: number): string { const p = this.players.find(p => p.playerId === id); return p ? `${p.firstName} ${p.lastName}` : '—'; }
  trackIndex(index: number): number { return index; }
  behaviours(slot: string): string[] { return BEHAVIOURS[slot] ?? [slot]; }
  behaviourLabel(code: string, slot: string): string { return this.translate.instant('workbench.behaviours.' + (code === slot ? 'normal' : code)); }
  positionLabel(key: string): string { return this.translate.instant('optimizer.positions.' + key); }
  edit(): void { this.operationSequence++; this.dirty = true; this.preview = null; this.message = ''; this.error = ''; }
  ordersChanged(): void { this.operationSequence++; this.preview = null; this.message = ''; this.error = ''; }
  changePlayer(row: EditorRow, id: number): void {
    const other = this.rows.find(r => r !== row && r.playerId === Number(id));
    if (other) other.playerId = row.playerId;
    row.playerId = Number(id);
    this.edit();
  }
  positions(rows = this.rows): Record<string, { playerId: number; behaviour: string }> {
    return Object.fromEntries(rows.map(r => [r.slot, { playerId: r.playerId, behaviour: r.behaviour }]));
  }
  async evaluate(): Promise<void> {
    if (!this.response.inputSnapshot || this.stale || this.busy) return;
    this.busy = true; this.error = ''; this.preview = null;
    const sequence = ++this.operationSequence;
    try {
      const context = { ...this.response.inputSnapshot, teamAttitude: this.active?.attitude ?? this.response.inputSnapshot.teamAttitude,
        preferredFormation: this.lineup.formation, preferredTactic: this.tactic };
      const result = await firstValueFrom(this.api.evaluateLineup(context, this.positions(), this.tactic));
      if (sequence !== this.operationSequence || this.stale) return;
      this.dirty = false;
      this.evaluated.emit(result);
    } catch (e: any) { this.error = e?.error?.error ?? e.message; }
    finally { this.busy = false; }
  }
  addSubstitution(): void {
    this.substitutions.push({ playerOutId: 0, playerInId: 0, minute: 60, position: -1, behaviour: -1, standing: -1 });
    this.ordersChanged();
  }
  orderRequest(): any {
    return { matchId: this.response.inputSnapshot?.matchId ?? 0, positions: this.positions(), tactic: this.tactic,
      attitude: this.active?.attitude ?? this.response.inputSnapshot?.teamAttitude ?? 'Normal',
      assistantManagerLevel: this.response.inputSnapshot?.assistantManagerLevel ?? 0, captainId: Number(this.captainId),
      setPiecesTakerId: Number(this.setPiecesTakerId), bench: this.bench.map((playerId,positionCode) => ({playerId: Number(playerId), positionCode})).filter(b => b.playerId),
      kickers: this.penaltyOrder, substitutions: this.substitutions.map(s => ({...s})) };
  }
  async preparePreview(): Promise<void> {
    if (this.stale || this.dirty || this.busy) return;
    this.busy = true; this.error = '';
    const sequence = ++this.operationSequence;
    try {
      const preview = await firstValueFrom(this.api.previewMatchOrders(this.orderRequest()));
      if (sequence === this.operationSequence && !this.stale && !this.dirty) this.preview = preview;
    }
    catch (e: any) { this.error = e?.error?.error ?? e.message; }
    finally { this.busy = false; }
  }
  private freezePlan(status: 'ready' | 'sent'): SavedMatchPlan {
    const input = this.response.inputSnapshot!;
    return JSON.parse(JSON.stringify({ teamId: input.myTeamId, matchId: input.matchId ?? 0, opponentTeamId: input.opponentTeamId,
      matchDate: input.matchDate, savedAt: new Date().toISOString(), label: this.lineup.formation ?? '',
      result: this.response, selectedAlternative: this.selectedAlternative, rosterFingerprint: rosterFingerprint(this.players), teamContextKey: this.teamContextKey,
      status: input.matchId ? status : 'draft', orders: this.orderRequest(), referenceResult: this.reference ?? this.response }));
  }
  async save(status: 'ready' | 'sent' = 'ready', submitted?: SavedMatchPlan): Promise<void> {
    if (!submitted && (this.stale || this.dirty || !this.response.inputSnapshot)) return;
    const plan = submitted ?? this.freezePlan(status);
    const input = plan.result.inputSnapshot!;
    const alternative = plan.result.alternatives[plan.selectedAlternative];
    const lineup = alternative?.lineup ?? plan.result.optimalLineup;
    const orders = plan.orders;
    if (status === 'ready' && input.matchId) {
      const sequence = ++this.operationSequence;
      try {
        await firstValueFrom(this.api.previewMatchOrders(orders));
        if (sequence !== this.operationSequence || this.stale || this.dirty) return;
      } catch (e: any) { this.error = e?.error?.error ?? e.message; return; }
    }
    const stored = this.plans.save(plan);
    this.message = this.translate.instant(stored ? 'workbench.saved' : 'workbench.storageFailed');
    this.captureMessage = '';
    if (!input.matchId || this.mockMode) return;
    try {
      const capture = await firstValueFrom(this.api.captureForecast({
        matchId: input.matchId, matchDate: input.matchDate, isHomeMatch: input.isHomeMatch,
        tactic: orders.tactic, attitude: orders.attitude, coachType: input.coachType,
        teamSpiritLevel: input.teamSpiritLevel, confidenceLevel: input.confidenceLevel,
        formation: lineup.formation, formationExperience: input.formationExperience[lineup.formation ?? ''],
        weatherId: (plan.result.weather?.weatherId ?? -1) >= 0 ? plan.result.weather?.weatherId : null,
        assistantManagerLevel: input.assistantManagerLevel,
        playerIdsBySlot: Object.fromEntries(Object.entries(lineup.positions).map(([slot,p]) => [slot,p.player!.playerId])),
        behavioursBySlot: Object.fromEntries(Object.entries(lineup.positions).map(([slot,p]) => [slot,p.behavior])),
        predictedRatings: alternative?.ratings ?? lineup.predictedRatings, modelVersion: plan.result.modelVersion
      }));
      this.captureMessage = capture.complete ? this.translate.instant('workbench.forecastSaved')
        : this.translate.instant('workbench.forecastIncomplete') + ' ' + (capture.missingFields ?? []).map((field: string) => {
          const key = 'review.context.' + field; const label = this.translate.instant(key); return label === key ? field : label;
        }).join(', ');
    } catch (e: any) { this.captureMessage = this.translate.instant('workbench.forecastFailed') + ' ' + (e?.error?.error ?? e.message); }
  }
  async send(): Promise<void> {
    if (!this.canSend) return;
    this.busy = true; this.error = '';
    const submitted = this.freezePlan('sent');
    try {
      const response = await firstValueFrom(this.api.sendMatchOrders(submitted.orders));
      if (response.success !== true) throw new Error(response.error ?? this.translate.instant('matchOrders.failed'));
      await this.save('sent', submitted);
      this.message = this.translate.instant('matchOrders.success') + ` (${submitted.label}, #${submitted.matchId})`;
      this.preview = null;
    } catch (e: any) { this.error = e?.error?.error ?? e.message; }
    finally { this.busy = false; }
  }
  async compareScenarios(): Promise<void> {
    if (this.stale || this.dirty || this.busy || !this.response.inputSnapshot) return;
    this.busy = true; this.error = ''; this.scenarios = [];
    const sequence = ++this.operationSequence;
    try {
      const referenceLineup = this.reference?.optimalLineup ?? this.lineup;
      const referenceRows = Object.entries(referenceLineup.positions).map(([slot,p]) => ({slot,playerId:p.player!.playerId,behaviour:p.behavior}));
      for (const [label,factor] of [['workbench.typical',1],['workbench.stronger',1.15],['workbench.weaker',0.85]] as const) {
        const context = {...this.response.inputSnapshot, opponentRatingScale:factor};
        const [own, original] = await Promise.all([
          firstValueFrom(this.api.evaluateLineup({...context,preferredFormation:this.lineup.formation},this.positions(),this.tactic)),
          firstValueFrom(this.api.evaluateLineup({...context,preferredFormation:referenceLineup.formation, requiredPlayerIds:[],excludedPlayerIds:[],lockedPositions:{}},this.positions(referenceRows),referenceLineup.tacticType))
        ]);
        if (sequence !== this.operationSequence || this.stale || this.dirty) return;
        this.scenarios.push({label,current:own.alternatives[0].winProbability,recommended:original.alternatives[0].winProbability});
      }
    } catch (e:any) {this.error=e?.error?.error ?? e.message;}
    finally {this.busy=false;}
  }
}
