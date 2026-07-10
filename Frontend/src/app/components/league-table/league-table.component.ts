import { Component, OnInit } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { LeagueSimulationReport } from '../../models/league-simulation.model';

@Component({
  selector: 'app-league-table',
  templateUrl: './league-table.component.html',
  styleUrls: ['./league-table.component.scss']
})
export class LeagueTableComponent implements OnInit {
  // Lewa kolumna: aktualna tabela (z raportu "od obecnej kolejki").
  report: LeagueSimulationReport | null = null;
  loading = false;
  error: string | null = null;

  // Prawa kolumna: symulacja uruchamiana na żądanie.
  simReport: LeagueSimulationReport | null = null;
  simLoading = false;
  simError: string | null = null;
  simMode: 'current' | 'first' | null = null;

  constructor(private hattrickApi: HattrickApiService) {}

  ngOnInit(): void {
    this.loadCurrentTable();
  }

  loadCurrentTable(): void {
    this.loading = true;
    this.error = null;
    this.hattrickApi.getLeagueSimulation(false).subscribe({
      next: (report) => {
        this.report = report;
        this.loading = false;
      },
      error: (err) => {
        this.error = err?.error?.error ?? err?.message ?? 'error';
        this.loading = false;
      }
    });
  }

  runSimulation(fromFirstRound: boolean): void {
    this.simLoading = true;
    this.simError = null;
    this.simMode = fromFirstRound ? 'first' : 'current';
    this.hattrickApi.getLeagueSimulation(fromFirstRound).subscribe({
      next: (report) => {
        this.simReport = report;
        this.simLoading = false;
      },
      error: (err) => {
        this.simError = err?.error?.error ?? err?.message ?? 'error';
        this.simLoading = false;
      }
    });
  }

  get positions(): number[] {
    const n = this.simReport?.teams?.length ?? 0;
    return Array.from({ length: n }, (_, i) => i + 1);
  }

  // Intensywność tła komórki heatmapy proporcjonalna do prawdopodobieństwa.
  probabilityStyle(p: number): { [k: string]: string } {
    const alpha = Math.min(1, Math.max(0, p));
    return { 'background-color': `rgba(76, 175, 80, ${alpha.toFixed(2)})` };
  }

  formatProbability(p: number): string {
    if (p <= 0) return '';
    if (p < 0.01) return '<1%';
    return `${Math.round(p * 100)}%`;
  }
}
