import { Component, OnInit } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { OptimizerRequest, OptimizerResponse } from '../../models/lineup.model';
import { TranslateService } from '@ngx-translate/core';
import { DataCacheService } from '../../services/data-cache.service';

@Component({
  selector: 'app-lineup-optimizer',
  templateUrl: './lineup-optimizer.component.html',
  styleUrls: ['./lineup-optimizer.component.scss']
})
export class LineupOptimizerComponent implements OnInit {
  myTeamId: number = 0;
  opponentTeamId: number = 0;
  preferredTactic: string = 'Normal';

  result: OptimizerResponse | null = null;
  loading: boolean = false;
  error: string | null = null;

  tactics: { value: string; label: string }[] = [];

  constructor(
    private hattrickApi: HattrickApiService,
    private translate: TranslateService,
    private cache: DataCacheService
  ) {
    this.initializeTranslations();
  }

  ngOnInit(): void {
    this.cache.auth$.subscribe(auth => {
      if (auth.authorized && auth.ownTeamId && !this.myTeamId) {
        this.myTeamId = auth.ownTeamId;
      }
    });
    this.cache.nextOpponent$.subscribe(opp => {
      if (opp?.opponentTeamId && !this.opponentTeamId) {
        this.opponentTeamId = opp.opponentTeamId;
      }
    });
  }

  private initializeTranslations(): void {
    this.tactics = [
      { value: 'Normal', label: this.translate.instant('optimizer.tactics.normal') },
      { value: 'Offensive', label: this.translate.instant('optimizer.tactics.offensive') },
      { value: 'Defensive', label: this.translate.instant('optimizer.tactics.defensive') },
      { value: 'Counter', label: this.translate.instant('optimizer.tactics.counter') },
      { value: 'AttackMiddle', label: this.translate.instant('optimizer.tactics.attackMiddle') },
      { value: 'AttackWings', label: this.translate.instant('optimizer.tactics.attackWings') }
    ];
  }

  optimizeLineup(): void {
    if (!this.myTeamId || !this.opponentTeamId) {
      this.error = this.translate.instant('optimizer.enterBothTeamIds');
      return;
    }

    this.loading = true;
    this.error = null;

    const request: OptimizerRequest = {
      myTeamId: this.myTeamId,
      opponentTeamId: this.opponentTeamId,
      preferredTactic: this.preferredTactic,
      focusAreas: []
    };

    this.hattrickApi.optimizeLineup(request).subscribe({
      next: (response) => {
        this.result = response;
        this.loading = false;
      },
      error: (err) => {
        this.error = this.translate.instant('optimizer.errorOptimizing') + err.message;
        this.loading = false;
      }
    });
  }

  getPositionKeys(): string[] {
    if (!this.result?.optimalLineup?.positions) return [];
    return Object.keys(this.result.optimalLineup.positions);
  }

  getPositionLabel(position: string): string {
    const labels: { [key: string]: string } = {
      'GK': this.translate.instant('optimizer.positions.GK'),
      'RWB': this.translate.instant('optimizer.positions.RWB'),
      'RCD': this.translate.instant('optimizer.positions.RCD'),
      'LCD': this.translate.instant('optimizer.positions.LCD'),
      'LWB': this.translate.instant('optimizer.positions.LWB'),
      'RW': this.translate.instant('optimizer.positions.RW'),
      'CM': this.translate.instant('optimizer.positions.CM'),
      'LW': this.translate.instant('optimizer.positions.LW'),
      'RFW': this.translate.instant('optimizer.positions.RFW'),
      'CFW': this.translate.instant('optimizer.positions.CFW'),
      'LFW': this.translate.instant('optimizer.positions.LFW')
    };
    return labels[position] || position;
  }
}
