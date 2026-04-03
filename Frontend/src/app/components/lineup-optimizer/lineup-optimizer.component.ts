import { Component } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { OptimizerRequest, OptimizerResponse } from '../../models/lineup.model';

@Component({
  selector: 'app-lineup-optimizer',
  templateUrl: './lineup-optimizer.component.html',
  styleUrls: ['./lineup-optimizer.component.scss']
})
export class LineupOptimizerComponent {
  myTeamId: number = 0;
  opponentTeamId: number = 0;
  preferredTactic: string = 'Normal';
  
  result: OptimizerResponse | null = null;
  loading: boolean = false;
  error: string | null = null;

  tactics = [
    { value: 'Normal', label: 'Normalna' },
    { value: 'Offensive', label: 'Ofensywna' },
    { value: 'Defensive', label: 'Defensywna' },
    { value: 'Counter', label: 'Kontratak' },
    { value: 'AttackMiddle', label: 'Atak środkiem' },
    { value: 'AttackWings', label: 'Atak skrzydłami' }
  ];

  constructor(private hattrickApi: HattrickApiService) {}

  optimizeLineup(): void {
    if (!this.myTeamId || !this.opponentTeamId) {
      this.error = 'Podaj ID obu drużyn';
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
        this.error = 'Błąd podczas optymalizacji składu: ' + err.message;
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
      'GK': 'Bramkarz',
      'RWB': 'Prawy obrońca',
      'RCD': 'Prawy środkowy obrońca',
      'LCD': 'Lewy środkowy obrońca',
      'LWB': 'Lewy obrońca',
      'RW': 'Prawy pomocnik',
      'CM': 'Środkowy pomocnik',
      'LW': 'Lewy pomocnik',
      'RFW': 'Prawy napastnik',
      'CFW': 'Środkowy napastnik',
      'LFW': 'Lewy napastnik'
    };
    return labels[position] || position;
  }
}
