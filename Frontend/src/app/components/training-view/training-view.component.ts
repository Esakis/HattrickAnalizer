import { Component, OnInit } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { TrainingSummary } from '../../models/training.model';

@Component({
  selector: 'app-training-view',
  templateUrl: './training-view.component.html',
  styleUrls: ['./training-view.component.scss']
})
export class TrainingViewComponent implements OnInit {
  summary: TrainingSummary | null = null;
  loading = false;
  error: string | null = null;

  constructor(
    private hattrickApi: HattrickApiService,
    private translate: TranslateService
  ) {}

  ngOnInit(): void {
    this.loadSummary();
  }

  loadSummary(): void {
    this.loading = true;
    this.error = null;
    this.hattrickApi.getTrainingSummary().subscribe({
      next: (summary) => {
        this.summary = summary;
        this.loading = false;
      },
      error: (err) => {
        this.error = err?.error?.error ?? err?.message ?? 'error';
        this.loading = false;
      }
    });
  }

  getTrainingTypeLabel(): string {
    if (!this.summary) return '';
    const key = `training.types.${this.summary.trainingTypeName}`;
    const translated = this.translate.instant(key);
    return translated === key ? this.summary.trainingTypeName : translated;
  }

  getSkillLevelName(value: number): string {
    const levels = ['beznadziejny', 'fatalny', 'nędzny', 'kiepski', 'słaby', 'przeciętny',
                    'zadowalający', 'solidny', 'znakomity', 'fantastyczny', 'olśniewający',
                    'błyskotliwy', 'mistrzowski', 'światowej klasy', 'nadprzyrodzony', 'tytaniczny',
                    'nieziemski', 'mityczny', 'magiczny', 'utopijny', 'boski'];
    return levels[Math.min(value, levels.length - 1)] || `${value}`;
  }
}
