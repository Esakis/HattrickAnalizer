import { Component, OnInit } from '@angular/core';
import { TranslateService } from '@ngx-translate/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { TrainingPlayerEntry, TrainingSummary } from '../../models/training.model';

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
    const unknownType = /^Unknown(?:\((\d+)\))?$/.exec(this.summary.trainingTypeName);
    if (unknownType) {
      const label = this.translate.instant('training.types.Unknown');
      return unknownType[1] ? `${label} (${unknownType[1]})` : label;
    }
    const key = `training.types.${this.summary.trainingTypeName}`;
    const translated = this.translate.instant(key);
    return translated === key ? this.summary.trainingTypeName : translated;
  }

  getSkillLevelName(value: number): string {
    if (!Number.isInteger(value) || value < 0 || value > 20) return this.translate.instant('training.skillUnknown');
    return this.translate.instant(`playerAbilities.${value}`);
  }

  getTrainedSkillLabel(player: TrainingPlayerEntry): string {
    if (player.trainedSkillAvailable === false) return this.translate.instant('training.skillUnknown');
    return `${player.trainedSkillValue} (${this.getSkillLevelName(player.trainedSkillValue)})`;
  }

  get summaryWarnings(): string[] {
    if (!this.summary) return [];
    const warnings = this.summary.warnings ?? [];
    const translated = warnings.map(w => this.translateTrainingWarning(w));
    if (this.summary.isEstimate && !translated.some(w => w === this.translate.instant('training.warnings.weekWindowEstimate')
      || w === this.translate.instant('training.warnings.estimateWarning'))) {
      translated.unshift(this.translate.instant('training.warnings.estimateWarning'));
    }
    return translated;
  }

  hasPlayerWarning(warnings?: string[]): boolean {
    return !!warnings?.length;
  }

  getPlayerWarnings(warnings?: string[]): string[] {
    return (warnings ?? []).map(w => this.translateTrainingWarning(w));
  }

  private translateTrainingWarning(warning: string): string {
    const translations: Array<[string, string]> = [
      ['Training window is the trailing seven UTC days', 'weekWindowEstimate'],
      ['Training week uses the documented fallback', 'weekWindowFallback'],
      ['Training speed factors were not fully available', 'speedUnavailable'],
      ['Stamina contribution is unknown', 'staminaUnknown'],
      ['Manual classifies this position as a small effect', 'smallEffectUnknown'],
      ['lineup data unavailable', 'lineupUnavailable'],
      ['CHPP omitted PlayedMinutes', 'minutesUnavailable'],
      ['CHPP lineup has no substitution timeline', 'substitutionUnknown'],
      ['Training speed is partial', 'speedUnavailable'],
      ['Assistant-coach skill levels were unavailable', 'assistantSkillUnknown'],
      ['CHPP did not provide current Morale', 'teamSpiritUnknown'],
      ['CHPP did not provide current SelfConfidence', 'confidenceUnknown'],
      ['CHPP did not provide a recognized TrainingType', 'trainingTypeUnknown'],
      ['TrainingLevel or StaminaTrainingPart is unavailable', 'trainingSettingsUnknown'],
      ['Training week uses the documented fallback', 'weekWindowFallback'],
      ['lineup data unavailable', 'lineupUnavailable'],
      ['CHPP did not identify the set-pieces taker', 'setPiecesTakerUnknown'],
      ['CHPP omitted PlayedMinutes', 'minutesUnavailable'],
      ['CHPP supplied no position-change timeline', 'positionTimelineUnknown'],
      ['CHPP included substitution data but no per-player position-minute segments', 'positionSegmentsUnknown'],
      ['Training type is unavailable', 'trainingTypeUnknown'],
      ['CHPP position code is unknown', 'positionUnknown'],
      ['official manual classifies this position as a small effect', 'smallEffectUnknown'],
      ['official manual classifies this position as a very small', 'verySmallEffectUnknown'],
      ['Set-piece taker is unknown for', 'setPiecesMinutesUnknown'],
      ['Stamina contribution is unknown', 'staminaUnknown'],
      ['Current trained skill was not available', 'trainedSkillUnknown'],
      ['No official minutes could be reconstructed', 'minutesUnavailable'],
      ['unsupported or ambiguous substitution timeline', 'substitutionUnknown'],
      ['official lineup timeline is ambiguous', 'timelineUnknown'],
      ['timeline minutes assume a standard 90-minute regulation match', 'matchDetailsUnavailable'],
      ['Current CHPP stafflist 1.2 was unavailable', 'staffListUnavailable'],
      ['TrainerSkillLevel was missing or outside', 'coachSkillMalformed'],
      ['TrainerType was missing or outside', 'coachTypeMalformed'],
      ['assistant StaffLevel was missing or outside', 'assistantSkillMalformed']
    ];
    const normalizedWarning = warning.toLowerCase();
    const entry = translations.find(([prefix]) => normalizedWarning.includes(prefix.toLowerCase()));
    if (entry) return this.translate.instant(`training.warnings.${entry[1]}`);
    return this.translate.currentLang === 'en' ? warning : `${this.translate.instant('training.warnings.additional')}: ${warning}`;
  }
}
