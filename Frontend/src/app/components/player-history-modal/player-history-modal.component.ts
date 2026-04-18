import {
  Component, Input, Output, EventEmitter,
  OnChanges, SimpleChanges, ViewChild, ElementRef, OnDestroy
} from '@angular/core';
import { PlayerHistoryService, PlayerSkillHistory } from '../../services/player-history.service';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);

type Tab = 'skills' | 'basic' | 'matches';

@Component({
  selector: 'app-player-history-modal',
  templateUrl: './player-history-modal.component.html',
  styleUrls: ['./player-history-modal.component.scss']
})
export class PlayerHistoryModalComponent implements OnChanges, OnDestroy {
  @Input() playerId: number | null = null;
  @Input() playerName: string = '';
  @Output() closed = new EventEmitter<void>();

  @ViewChild('chartCanvas') chartCanvas?: ElementRef<HTMLCanvasElement>;

  history: PlayerSkillHistory[] = [];
  loading = false;
  error: string | null = null;
  activeTab: Tab = 'skills';
  private chart: Chart | null = null;

  constructor(private historyService: PlayerHistoryService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['playerId'] && this.playerId != null) {
      this.loadHistory();
    }
  }

  ngOnDestroy(): void {
    this.chart?.destroy();
  }

  close(): void { this.closed.emit(); }

  setTab(tab: Tab): void {
    this.activeTab = tab;
    setTimeout(() => this.drawChart(), 0);
  }

  private loadHistory(): void {
    this.loading = true;
    this.error = null;
    this.history = [];
    this.chart?.destroy();
    this.chart = null;

    this.historyService.getPlayerHistory(this.playerId!).subscribe({
      next: (data) => {
        this.history = data;
        this.loading = false;
        setTimeout(() => this.drawChart(), 0);
      },
      error: (err) => {
        this.error = 'Błąd pobierania historii: ' + err.message;
        this.loading = false;
      }
    });
  }

  drawChart(): void {
    if (!this.chartCanvas?.nativeElement || this.history.length === 0) return;
    this.chart?.destroy();

    const labels = this.history.map(h => h.recordedDate.split('T')[0]);

    if (this.activeTab === 'skills') this.drawSkillsChart(labels);
    else if (this.activeTab === 'basic') this.drawBasicChart(labels);
    else this.drawMatchesChart(labels);
  }

  private drawSkillsChart(labels: string[]): void {
    const defs: [keyof PlayerSkillHistory, string, string][] = [
      ['keeper',     'Bramkarz',      '#ff6384'],
      ['defending',  'Obrona',        '#36a2eb'],
      ['playmaking', 'Rozgrywanie',   '#ffce56'],
      ['winger',     'Skrzydło',      '#4bc0c0'],
      ['passing',    'Podania',       '#9966ff'],
      ['scoring',    'Skuteczność',   '#ff9f40'],
      ['setPieces',  'Stałe fr.',     '#c9cbcf'],
    ];
    this.renderLineChart(labels, defs, { min: 0, max: 20, reverse: true, label: 'Poziom (1=najlepszy)' });
  }

  private drawBasicChart(labels: string[]): void {
    const defs: [keyof PlayerSkillHistory, string, string][] = [
      ['form',       'Forma',          '#4bc0c0'],
      ['stamina',    'Kondycja',       '#ff9f40'],
      ['experience', 'Doświadczenie',  '#36a2eb'],
      ['loyalty',    'Lojalność',      '#9966ff'],
      ['leadership', 'Przywództwo',    '#ffce56'],
      ['tsi',        'TSI',            '#ff6384'],
    ];
    this.renderLineChart(labels, defs, { label: 'Wartość' });
  }

  private drawMatchesChart(labels: string[]): void {
    const defs: [keyof PlayerSkillHistory, string, string][] = [
      ['goals',         'Gole',             '#ff6384'],
      ['assists',       'Asysty',           '#36a2eb'],
      ['totalMatches',  'Mecze',            '#4bc0c0'],
      ['yellowCards',   'Żółte kartki',     '#ffce56'],
      ['redCards',      'Czerwone kartki',  '#ff4444'],
      ['minutesPlayed', 'Minuty (÷10)',      '#9966ff'],
    ];

    const datasets = defs.map(([key, label, color]) => ({
      label,
      data: this.history.map(h => key === 'minutesPlayed' ? Math.round((h[key] as number) / 10) : h[key] as number),
      borderColor: color,
      backgroundColor: color + '33',
      tension: 0.3,
      fill: false,
      pointRadius: 4,
      pointHoverRadius: 6,
    }));

    this.chart = new Chart(this.chartCanvas!.nativeElement, {
      type: 'line',
      data: { labels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: { legend: { position: 'bottom' } },
        scales: {
          y: { title: { display: true, text: 'Wartość' } },
          x: { title: { display: true, text: 'Data' } }
        }
      }
    });
  }

  private renderLineChart(
    labels: string[],
    defs: [keyof PlayerSkillHistory, string, string][],
    yAxis: { min?: number; max?: number; reverse?: boolean; label: string }
  ): void {
    const datasets = defs.map(([key, label, color]) => ({
      label,
      data: this.history.map(h => h[key] as number),
      borderColor: color,
      backgroundColor: color + '33',
      tension: 0.3,
      fill: false,
      pointRadius: 4,
      pointHoverRadius: 6,
    }));

    this.chart = new Chart(this.chartCanvas!.nativeElement, {
      type: 'line',
      data: { labels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        plugins: { legend: { position: 'bottom' } },
        scales: {
          y: {
            min: yAxis.min,
            max: yAxis.max,
            reverse: yAxis.reverse ?? false,
            title: { display: true, text: yAxis.label }
          },
          x: { title: { display: true, text: 'Data' } }
        }
      }
    });
  }

  changed(i: number, key: keyof PlayerSkillHistory): boolean {
    if (i === 0) return false;
    return this.history[i][key] !== this.history[i - 1][key];
  }

  fmt(val: number | undefined, decimals = 0): string {
    if (val == null) return '—';
    return decimals > 0 ? val.toFixed(decimals) : String(val);
  }
}
