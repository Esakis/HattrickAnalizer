import { of } from 'rxjs';
import { TranslateService } from '@ngx-translate/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { TrainingPlayerEntry } from '../../models/training.model';
import { TrainingViewComponent } from './training-view.component';
import { DataCacheService } from '../../services/data-cache.service';
import { WeeklyPlanService } from '../../services/weekly-plan.service';
import { Router } from '@angular/router';
import { Player } from '../../models/player.model';
import { TRAINING_FORMATIONS } from '../../services/training-formations';

describe('TrainingViewComponent', () => {
  function component(language: 'en' | 'pl' = 'en'): TrainingViewComponent {
    const translate = {
      currentLang: language,
      instant: (key: string) => key
    } as TranslateService;
    return new TrainingViewComponent({ getTrainingSummary: () => of(null) } as unknown as HattrickApiService, translate,
      new DataCacheService(), new WeeklyPlanService(), { navigate: jasmine.createSpy('navigate') } as unknown as Router);
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

  it('hands the friendly date, opponent and starter constraints to the optimizer instead of the league fixture', () => {
    const view = component();
    view.summary = {
      teamId: 55, trainingTypeCode: 8, trainingTypeKnown: true, trainingTypeName: 'Playmaking', trainedSkill: 'Playmaking',
      trainingLevel: 100, staminaTrainingPart: 10, trainerName: '', lastMatchId: 0, lastMatchDate: null,
      weekStart: '2026-10-15T20:00:00Z', weekEnd: '2026-10-16T12:00:00Z',
      players: [{ playerId: 1, observedFullMinutes: 0, observedHalfMinutes: 0 } as TrainingPlayerEntry]
    };
    view.roster = [{ playerId: 1, injuryLevel: -1, injuryStatusKnown: true, suspensionStatusKnown: true } as Player];
    view.plan = {
      version: 1, teamId: 55, weekStart: view.summary.weekStart!, selectedPlayerIds: [1], updatedAt: '',
      matchDates: { league: '2026-10-17T14:00:00Z', friendly: '2026-10-20T14:00:00Z' },
      assignments: [{ playerId: 1, match: 'friendly', slot: 'IM', startMinute: 0, minutes: 90 }]
    };
    view.upcomingMatches = [
      { matchId: 10, opponentTeamId: 101, opponentTeamName: 'League', matchType: '1', matchDate: view.plan.matchDates.league },
      { matchId: 20, opponentTeamId: 202, opponentTeamName: 'Friendly', matchType: '8', matchDate: view.plan.matchDates.friendly }
    ];
    view.recalculate();
    view.prepareMatch('friendly');
    expect((view as any).router.navigate).toHaveBeenCalledWith(['/optimizer'], { queryParams: {
      traineeIds: '1', lockedPositions: '{"IM":1}', trainingMatch: 'friendly', trainingWeek: view.plan.weekStart,
      trainingMatchDate: '2026-10-20T14:00:00.000Z', trainingAssignments: JSON.stringify(view.plan!.assignments), trainingSlots: 'IM', trainingExcludedIds: '', opponentId: 202, matchId: 20
    } });
    for (const unavailable of [{ isSuspended: true }, { canOptimize: false }]) {
      const original = view.roster[0];
      view.roster = [{ ...original, ...unavailable }];
      view.recalculate();
      (view as any).router.navigate.calls.reset();
      view.prepareMatch('friendly');
      expect(view.canPrepare('friendly')).toBeFalse();
      expect((view as any).router.navigate).not.toHaveBeenCalled();
      view.roster = [original];
    }
    view.plan.matchDates.friendly = '2026-10-21T14:00:00Z';
    expect(view.fixtureForPlan('friendly')).toBeUndefined();
  });

  function recommendationView(): TrainingViewComponent {
    const view = component();
    view.roster = Array.from({ length: 12 }, (_, index) => ({
      playerId: index + 1, firstName: `Player ${index + 1}`, lastName: 'Test', age: 18,
      injuryLevel: -1, injuryStatusKnown: true, suspensionStatusKnown: true,
      skills: { keeper: 1, defending: 4, playmaking: 8, winger: 6, passing: 5, scoring: 3, setPieces: 2 }
    } as Player));
    view.summary = {
      teamId: 55, trainingTypeCode: 8, trainingTypeKnown: true, trainingTypeName: 'Playmaking', trainedSkill: 'Playmaking',
      trainingLevel: 100, staminaTrainingPart: 10, trainerName: '', lastMatchId: 0, lastMatchDate: null,
      weekStart: '2026-10-15T20:00:00Z', weekEnd: '2026-10-16T12:00:00Z',
      players: view.roster.map(player => ({ playerId: player.playerId, observedFullMinutes: 0, observedHalfMinutes: 0 } as TrainingPlayerEntry))
    };
    view.plan = {
      version: 1, teamId: 55, weekStart: view.summary.weekStart!, selectedPlayerIds: [12], updatedAt: '',
      matchDates: { league: '2026-10-17T14:00:00Z', friendly: '2026-10-20T14:00:00Z' },
      assignments: [{ playerId: 12, match: 'league', slot: 'RIM', startMinute: 0, minutes: 90 }]
    };
    view.previewTrainingType = 8;
    view.refreshRecommendations();
    spyOn((view as any).weeklyPlans, 'savePlan').and.returnValue(true);
    return view;
  }

  it('previews other formations and training without changing the weekly plan or recorded type', () => {
    const view = recommendationView();
    const original = JSON.stringify(view.plan);
    view.previewFormation = '4-4-2';
    view.previewTrainingType = 3;
    view.refreshRecommendations();
    expect(view.selectedRecommendation?.fullSlotsPerMatch).toBe(4);
    expect(view.canApplyRecommendation).toBeFalse();
    view.applyRecommendation();
    expect(JSON.stringify(view.plan)).toBe(original);
    expect(view.summary?.trainingTypeCode).toBe(8);
    expect((view as any).weeklyPlans.savePlan).not.toHaveBeenCalled();
  });

  it('adds current-type recommendations, keeps manual entries and reports half coverage honestly', () => {
    const view = recommendationView();
    const manual = { ...view.plan!.assignments[0] };
    const suggestedIds = view.selectedRecommendation!.trainees.map(player => player.playerId);
    expect(view.canApplyRecommendation).toBeTrue();
    view.applyRecommendation();
    expect(view.plan?.formation).toBe('3-5-2');
    expect(view.plan?.assignments).toContain(manual);
    expect(view.plan?.selectedPlayerIds).toContain(12);
    for (const id of suggestedIds) expect(view.plan?.selectedPlayerIds).toContain(id);
    expect((view as any).weeklyPlans.savePlan).toHaveBeenCalled();
    expect(view.assessment.players.some(player => player.projected === 45 && player.missing === 45)).toBeTrue();
    expect(view.assessment.ready).toBeFalse();
  });

  it('restricts slot editing to the plan formation while keeping an incompatible saved slot visible', () => {
    const view = recommendationView();
    view.plan!.formation = '4-4-2';
    expect(view.assignmentSlots()).not.toContain('IM');
    expect(view.assignmentSlots('IM')).toContain('IM');
    expect(view.assignmentSlots()).toEqual(TRAINING_FORMATIONS.find(item => item.name === '4-4-2')!.slots);
  });

  it('preserves preview positions and matches when applying to an empty two-match plan', () => {
    const view = recommendationView();
    view.roster = view.roster.slice(0, 10);
    view.plan!.selectedPlayerIds = [];
    view.plan!.assignments = [];
    view.refreshRecommendations();
    const preview = view.selectedRecommendation!.trainees;
    view.applyRecommendation();
    for (const trainee of preview) {
      expect(view.plan!.assignments).toContain({
        playerId: trainee.playerId, match: trainee.match, slot: trainee.slot, startMinute: 0, minutes: 90
      });
      const coverage = view.assessment.players.find(player => player.playerId === trainee.playerId)!;
      expect(coverage.projected).toBe(trainee.factor * 90);
    }
  });

  it('adds manual trainees to a legal position when a formation has no full training positions', () => {
    const view = recommendationView();
    view.summary!.trainingTypeCode = 4;
    view.plan!.formation = '5-5-0';
    view.addAssignment('friendly');
    expect(TRAINING_FORMATIONS.find(item => item.name === '5-5-0')!.slots).toContain(view.assignments('friendly')[0].slot);
  });

  it('hands the complete selected formation to the optimizer with the trainee constraints', () => {
    const view = recommendationView();
    view.plan!.formation = '3-5-2';
    view.recalculate();
    view.prepareMatch('league');
    const params = (view as any).router.navigate.calls.mostRecent().args[1].queryParams;
    expect(params.trainingFormation).toBe('3-5-2');
    expect(params.trainingSlots).toBe(TRAINING_FORMATIONS.find(item => item.name === '3-5-2')!.slots.join(','));
    expect(params.lockedPositions).toBe('{"RIM":12}');
    expect(params.traineeIds).toBe('12');
  });
});
