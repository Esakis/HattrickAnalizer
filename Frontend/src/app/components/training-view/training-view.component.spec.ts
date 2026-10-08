import { of } from 'rxjs';
import { TranslateService } from '@ngx-translate/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { TrainingPlayerEntry } from '../../models/training.model';
import { TrainingViewComponent } from './training-view.component';

describe('TrainingViewComponent', () => {
  function component(language: 'en' | 'pl' = 'en'): TrainingViewComponent {
    const translate = {
      currentLang: language,
      instant: (key: string) => key
    } as TranslateService;
    return new TrainingViewComponent({ getTrainingSummary: () => of(null) } as unknown as HattrickApiService, translate);
  }

  it('localizes unsupported training type codes and shows the original code', () => {
    const view = component();
    view.summary = {
      teamId: 55, trainingTypeCode: 13, trainingTypeName: 'Unknown(13)', trainedSkill: '',
      trainingLevel: 90, staminaTrainingPart: 10, trainerName: '', lastMatchId: 0,
      lastMatchDate: null, players: []
    };

    expect(view.getTrainingTypeLabel()).toBe('training.types.Unknown (13)');
  });

  it('shows an unavailable skill as unknown, while retaining the valid level-zero label', () => {
    const view = component();
    expect(view.getSkillLevelName(0)).toBe('playerAbilities.0');
    expect(view.getTrainedSkillLabel({ trainedSkillValue: 0, trainedSkillAvailable: false } as TrainingPlayerEntry)).toBe('training.skillUnknown');
    expect(view.getTrainedSkillLabel({ trainedSkillValue: 0, trainedSkillAvailable: true } as TrainingPlayerEntry)).toBe('0 (playerAbilities.0)');
    expect(view.getSkillLevelName(-1)).toBe('training.skillUnknown');
    expect(view.getSkillLevelName(21)).toBe('training.skillUnknown');
  });

  it('preserves an unknown warning in English and labels it in Polish', () => {
    const english = component('en');
    const polish = component('pl');
    const translateWarning = (english as any).translateTrainingWarning.bind(english);
    const translatePolishWarning = (polish as any).translateTrainingWarning.bind(polish);

    expect(translateWarning('A new backend warning')).toBe('A new backend warning');
    expect(translatePolishWarning('A new backend warning')).toBe('training.warnings.additional: A new backend warning');
  });

  it('translates official timeline and staff validation warnings', () => {
    const view = component();
    const translateWarning = (view as any).translateTrainingWarning.bind(view);

    expect(translateWarning('Match 1: official lineup timeline is ambiguous.')).toBe('training.warnings.timelineUnknown');
    expect(translateWarning('CHPP stafflist TrainerSkillLevel was missing or outside 1..5.')).toBe('training.warnings.coachSkillMalformed');
  });
});
