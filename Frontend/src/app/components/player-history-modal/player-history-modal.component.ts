import { Component, Input, Output, EventEmitter, OnChanges, SimpleChanges, ViewChild, ElementRef, OnDestroy } from '@angular/core';
import { Subscription } from 'rxjs';
import { TranslateService } from '@ngx-translate/core';
import { PlayerHistoryService, PlayerSkillHistory } from '../../services/player-history.service';
import { Chart, registerables } from 'chart.js';

Chart.register(...registerables);
type Tab = 'skills' | 'basic' | 'matches';
type Metric = keyof PlayerSkillHistory;

@Component({
  selector: 'app-player-history-modal',
  templateUrl: './player-history-modal.component.html',
  styleUrls: ['./player-history-modal.component.scss']
})
export class PlayerHistoryModalComponent implements OnChanges, OnDestroy {
  @Input() playerId: number | null = null;
  @Input() playerName = '';
  @Output() closed = new EventEmitter<void>();
  @ViewChild('chartCanvas') chartCanvas?: ElementRef<HTMLCanvasElement>;
  history: PlayerSkillHistory[] = [];
  loading = false;
  error: string | null = null;
  activeTab: Tab = 'skills';
  readonly tabs: Tab[] = ['skills', 'basic', 'matches'];
  readonly columns: Record<Tab, Metric[]> = {
    skills: ['keeper', 'defending', 'playmaking', 'winger', 'passing', 'scoring', 'setPieces'],
    basic: ['form', 'stamina', 'age', 'tsi', 'experience', 'loyalty', 'leadership', 'injuryLevel'],
    matches: ['totalMatches', 'goals', 'assists', 'yellowCards', 'redCards', 'averageRating', 'averageForm', 'minutesPlayed']
  };
  private chart: Chart | null = null;
  private request?: Subscription;
  private redrawTimer?: ReturnType<typeof setTimeout>;
  private readonly languageChanges: Subscription;

  constructor(private historyService: PlayerHistoryService, private translate: TranslateService) {
    this.languageChanges = this.translate.onLangChange.subscribe(() => this.drawChart());
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['playerId'] && this.playerId != null) this.loadHistory();
  }

  ngOnDestroy(): void {
    this.request?.unsubscribe();
    this.languageChanges.unsubscribe();
    clearTimeout(this.redrawTimer);
    this.chart?.destroy();
  }

  close(): void { this.closed.emit(); }
  setTab(tab: Tab): void {
    this.activeTab = tab;
    clearTimeout(this.redrawTimer);
    this.redrawTimer = setTimeout(() => this.drawChart());
  }

  private loadHistory(): void {
    this.request?.unsubscribe();
    this.loading = true;
    this.error = null;
    this.history = [];
    this.chart?.destroy();
    this.chart = null;
    this.request = this.historyService.getPlayerHistory(this.playerId!).subscribe({
      next: data => {
        this.history = [...data].sort((a, b) => Date.parse(a.recordedDate) - Date.parse(b.recordedDate));
        this.loading = false;
        this.redrawTimer = setTimeout(() => this.drawChart());
      },
      error: () => {
        this.error = this.translate.instant('history.loadError');
        this.loading = false;
      }
    });
  }

  value(row: PlayerSkillHistory, key: Metric): number | null {
    if (key === 'injuryLevel' && row.injuryStatusKnown === false) return null;
    if (this.columns.matches.includes(key) && row.matchStatsAvailable === false) return null;
    const value = row[key];
    return typeof value === 'number' && Number.isFinite(value) ? value : null;
  }

  drawChart(): void {
    if (!this.chartCanvas?.nativeElement || !this.history.length) return;
    this.chart?.destroy();
    const colors = ['#06b6d4', '#8b5cf6', '#f59e0b', '#22c55e', '#ec4899', '#3b82f6', '#f97316', '#64748b'];
    const metrics = this.columns[this.activeTab].filter(key => key !== 'age' && key !== 'injuryLevel');
    const secondary = this.activeTab === 'basic' ? 'tsi' : this.activeTab === 'matches' ? 'minutesPlayed' : null;
    const labels = this.history.map(h => new Date(h.recordedDate).toLocaleString(this.translate.currentLang === 'en' ? 'en-GB' : 'pl-PL'));
    const datasets = metrics.map((key, i) => ({
      label: this.translate.instant('history.fields.' + key),
      data: this.history.map(row => this.value(row, key)),
      borderColor: colors[i % colors.length],
      backgroundColor: colors[i % colors.length] + '33',
      yAxisID: key === secondary ? 'secondary' : 'y',
      stepped: true, fill: false, pointRadius: 4, pointHoverRadius: 6
    }));
    this.chart = new Chart(this.chartCanvas.nativeElement, {
      type: 'line', data: { labels, datasets },
      options: {
        responsive: true, maintainAspectRatio: false,
        plugins: { legend: { position: 'bottom' } },
        scales: {
          y: { min: 0, reverse: false, title: { display: true, text: this.translate.instant(this.activeTab === 'skills' ? 'history.skillAxis' : 'history.value') } },
          secondary: { display: !!secondary, position: 'right', min: 0, grid: { drawOnChartArea: false }, title: { display: true, text: secondary ? this.translate.instant('history.fields.' + secondary) : '' } },
          x: { title: { display: true, text: this.translate.instant('history.date') } }
        }
      }
    });
  }

  changed(i: number, key: Metric): boolean {
    if (i === 0) return false;
    const previous = this.value(this.history[i - 1], key);
    const current = this.value(this.history[i], key);
    return previous != null && current != null && previous !== current;
  }
}
