import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { TranslateService } from '@ngx-translate/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { DataCacheService } from '../../services/data-cache.service';
import { OpponentScoutReport } from '../../models/opponent-scout.model';

@Component({
  selector: 'app-scout-view',
  templateUrl: './scout-view.component.html',
  styleUrls: ['./scout-view.component.scss']
})
export class ScoutViewComponent implements OnInit, OnDestroy {
  private destroy$ = new Subject<void>();

  teamId: number = 0;
  nextOpponentId: number = 0;
  nextOpponentName: string = '';
  report: OpponentScoutReport | null = null;
  loading = false;
  error: string | null = null;

  constructor(
    private hattrickApi: HattrickApiService,
    private cache: DataCacheService,
    private translate: TranslateService
  ) {}

  ngOnInit(): void {
    this.cache.nextOpponent$.pipe(takeUntil(this.destroy$)).subscribe(opp => {
      if (opp?.opponentTeamId) {
        this.nextOpponentId = opp.opponentTeamId;
        this.nextOpponentName = opp.opponentTeamName;
        if (!this.teamId) {
          this.teamId = opp.opponentTeamId;
          this.loadScout();
        }
      }
    });
  }

  ngOnDestroy(): void {
    this.destroy$.next();
    this.destroy$.complete();
  }

  loadScout(): void {
    if (!this.teamId) return;
    this.loading = true;
    this.error = null;
    this.hattrickApi.getOpponentScout(this.teamId).subscribe({
      next: (report) => {
        this.report = report;
        this.loading = false;
      },
      error: (err) => {
        this.error = err?.error?.error ?? err?.message ?? 'error';
        this.report = null;
        this.loading = false;
      }
    });
  }

  loadNextOpponent(): void {
    if (!this.nextOpponentId) return;
    this.teamId = this.nextOpponentId;
    this.loadScout();
  }

  getTacticLabel(value: string): string {
    const map: { [key: string]: string } = {
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

  get formationSummary(): string {
    if (!this.report) return '';
    return Object.entries(this.report.formationCounts)
      .sort((a, b) => b[1] - a[1])
      .map(([f, c]) => `${f} (${c})`)
      .join(', ');
  }

  get tacticSummary(): string {
    if (!this.report) return '';
    return Object.entries(this.report.tacticCounts)
      .sort((a, b) => b[1] - a[1])
      .map(([t, c]) => `${this.getTacticLabel(t)} (${c})`)
      .join(', ');
  }

  // Sektory do siatki ocen ważonych.
  get ratingRows(): { label: string; value: number }[] {
    const r = this.report?.weightedRatings;
    if (!r) return [];
    return [
      { label: 'optimizer.comparison.midfield', value: r.midfieldRating },
      { label: 'optimizer.comparison.leftDefense', value: r.leftDefenseRating },
      { label: 'optimizer.comparison.centralDefense', value: r.centralDefenseRating },
      { label: 'optimizer.comparison.rightDefense', value: r.rightDefenseRating },
      { label: 'optimizer.comparison.leftAttack', value: r.leftAttackRating },
      { label: 'optimizer.comparison.centralAttack', value: r.centralAttackRating },
      { label: 'optimizer.comparison.rightAttack', value: r.rightAttackRating }
    ];
  }
}
