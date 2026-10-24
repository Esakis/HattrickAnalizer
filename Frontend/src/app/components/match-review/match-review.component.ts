import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { Subject, finalize, takeUntil } from 'rxjs';
import { CalibrationReport, CalibrationSnapshot, Sector } from '../../models/match-review.model';
import { MatchReviewService } from '../../services/match-review.service';

@Component({
  selector: 'app-match-review', standalone: true,
  imports: [CommonModule, RouterModule, TranslateModule],
  templateUrl: './match-review.component.html', styleUrls: ['./match-review.component.scss']
})
export class MatchReviewComponent implements OnInit, OnDestroy {
  private readonly destroy$ = new Subject<void>();
  readonly sectors: Sector[] = ['midfield', 'rightDefense', 'centralDefense', 'leftDefense', 'rightAttack', 'centralAttack', 'leftAttack'];
  report: CalibrationReport | null = null;
  snapshots: CalibrationSnapshot[] = [];
  loading = false;
  loadingSnapshots = false;
  reportError = false;
  snapshotsError = false;
  constructor(private api: MatchReviewService) {}
  ngOnInit(): void { this.load(); }
  ngOnDestroy(): void { this.destroy$.next(); this.destroy$.complete(); }
  load(): void {
    if (this.loading || this.loadingSnapshots) return;
    this.loading = true;
    this.loadingSnapshots = true;
    this.reportError = false;
    this.snapshotsError = false;
    this.api.report().pipe(takeUntil(this.destroy$), finalize(() => this.loading = false)).subscribe({
      next: report => this.report = report, error: () => this.reportError = true
    });
    this.api.snapshots().pipe(takeUntil(this.destroy$), finalize(() => this.loadingSnapshots = false)).subscribe({
      next: snapshots => this.snapshots = [...snapshots].sort((a, b) => Date.parse(b.recordedAt) - Date.parse(a.recordedAt)),
      error: () => this.snapshotsError = true
    });
  }
  get upcomingSnapshots(): CalibrationSnapshot[] {
    return this.snapshots.filter(s => !s.context.matchDate || Date.parse(s.context.matchDate) > Date.now());
  }
  get historicalSnapshotCount(): number { return this.snapshots.length - this.upcomingSnapshots.length; }
  sourceKey(source: string): string {
    return source === 'replayed-current-model' || source === 'legacy-replay' ? 'review.replayed' : source === 'imported' ? 'review.imported' : 'review.savedForecast';
  }
}
