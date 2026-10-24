import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { Team } from '../../models/team.model';
import { TrainingSummary } from '../../models/training.model';
import { DataCacheService, NextOpponent } from '../../services/data-cache.service';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { MatchPlanService, SavedMatchPlan, rosterFingerprint, teamContextFingerprint } from '../../services/match-plan.service';
import { PlayerChangeResult, PlayerHistoryService } from '../../services/player-history.service';
import { TrainingAssessment, WeeklyPlanService } from '../../services/weekly-plan.service';

export function isMatchPlanCurrent(plan: SavedMatchPlan, team: Team, match: NextOpponent, now = Date.now()): boolean {
  const kickoff = Date.parse(match.matchDate ?? '');
  const input = plan.result.inputSnapshot;
  return Number.isFinite(kickoff) && kickoff > now
    && plan.teamId === team.teamId && plan.matchId === match.matchId && plan.opponentTeamId === match.opponentTeamId
    && Date.parse(plan.matchDate ?? '') === kickoff
    && input?.myTeamId === team.teamId && input?.opponentTeamId === match.opponentTeamId && input?.matchId === match.matchId
    && plan.rosterFingerprint === rosterFingerprint(team.players)
    && plan.teamContextKey === teamContextFingerprint(team);
}

@Component({
  selector: 'app-week-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, TranslateModule],
  templateUrl: './week-dashboard.component.html',
  styleUrls: ['./week-dashboard.component.scss']
})
export class WeekDashboardComponent implements OnInit, OnDestroy {
  private readonly destroy$ = new Subject<void>();
  team: Team | null = null;
  nextMatch: NextOpponent | null = null;
  savedPlan?: SavedMatchPlan;
  stalePlan = false;
  training: TrainingSummary | null = null;
  trainingAssessment: TrainingAssessment | null = null;
  trainingLoading = false;
  trainingError = false;
  changes: PlayerChangeResult[] = [];
  changesLoading = false;
  changesError = false;

  constructor(
    private cache: DataCacheService,
    private api: HattrickApiService,
    private matchPlans: MatchPlanService,
    private weeklyPlans: WeeklyPlanService,
    private history: PlayerHistoryService
  ) {}

  ngOnInit(): void {
    this.cache.ownTeam$.pipe(takeUntil(this.destroy$)).subscribe(team => {
      const changedTeam = this.team?.teamId !== team?.teamId;
      this.team = team;
      this.updateMatchPlan();
      if (team && changedTeam) { this.loadTraining(); this.loadChanges(); }
      else this.updateTrainingPlan();
    });
    this.cache.nextOpponent$.pipe(takeUntil(this.destroy$)).subscribe(next => {
      this.nextMatch = next; this.updateMatchPlan();
    });
    this.matchPlans.changed$.pipe(takeUntil(this.destroy$)).subscribe(() => this.updateMatchPlan());
    this.weeklyPlans.storageChanged$.pipe(takeUntil(this.destroy$)).subscribe(() => this.updateTrainingPlan());
    this.history.changed$.pipe(takeUntil(this.destroy$)).subscribe(() => this.loadChanges());
  }

  ngOnDestroy(): void { this.destroy$.next(); this.destroy$.complete(); }

  get planStatusKey(): string {
    if (!this.savedPlan) return 'week.noPlan';
    if (this.stalePlan) return 'week.planStale';
    return this.savedPlan.status === 'sent' ? 'week.planSent' : this.savedPlan.status === 'ready' ? 'week.planReady' : 'week.planDraft';
  }

  get trainingNeeds(): number {
    return this.trainingAssessment?.players.filter(player => player.missing !== 0).length ?? 0;
  }

  get nextMatchQuery(): Record<string, number> {
    return this.nextMatch ? { opponentId: this.nextMatch.opponentTeamId, matchId: this.nextMatch.matchId } : {};
  }

  playerName(playerId: number): string {
    const player = this.team?.players.find(p => p.playerId === playerId);
    return player ? `${player.firstName} ${player.lastName}` : `#${playerId}`;
  }

  loadChanges(): void {
    if (!this.team) return;
    const teamId = this.team.teamId;
    this.changesLoading = true;
    this.changesError = false;
    this.history.recent(12).pipe(takeUntil(this.destroy$)).subscribe({
      next: changes => {
        if (this.team?.teamId !== teamId) return;
        this.changes = changes.filter(change => change.teamId === teamId && change.changed && !change.firstSnapshot).slice(0, 6);
        this.changesLoading = false;
      },
      error: () => { this.changesLoading = false; this.changesError = true; }
    });
  }

  private loadTraining(): void {
    this.trainingLoading = true;
    this.trainingError = false;
    this.api.getTrainingSummary().pipe(takeUntil(this.destroy$)).subscribe({
      next: summary => {
        if (summary.teamId !== this.team?.teamId) return;
        this.training = summary;
        this.trainingLoading = false;
        this.updateTrainingPlan();
      },
      error: () => { this.trainingLoading = false; this.trainingError = true; }
    });
  }

  private updateMatchPlan(): void {
    this.savedPlan = this.team && this.nextMatch
      ? this.matchPlans.getPlan(this.team.teamId, this.nextMatch.matchId, this.nextMatch.opponentTeamId) : undefined;
    this.stalePlan = !!this.savedPlan && !!this.team && !!this.nextMatch
      && !isMatchPlanCurrent(this.savedPlan, this.team, this.nextMatch);
  }

  private updateTrainingPlan(): void {
    if (!this.team || !this.training || this.team.teamId !== this.training.teamId) return;
    const plan = this.weeklyPlans.getPlan(this.team.teamId, this.training.weekStart ?? '');
    this.trainingAssessment = this.weeklyPlans.evaluate(plan, this.training, this.team.players);
  }
}
