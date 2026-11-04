import { Player } from '../models/player.model';
import { TrainingPlayerEntry, TrainingSummary } from '../models/training.model';
import { WeeklyPlanService, WeeklyTrainingPlan, bestTraining90, trainingFactor, trainingMatchKind } from './weekly-plan.service';

describe('Weekly training plan', () => {
  let service: WeeklyPlanService;
  const player = (id: number): Player => ({
    playerId: id, firstName: `Player ${id}`, lastName: 'Test', age: 18, tsi: 1000,
    form: 7, stamina: 7, experience: 3, loyalty: 20, leadership: 3, specialty: '',
    injuryLevel: -1, injuryStatusKnown: true, isSuspended: false, suspensionStatusKnown: true,
    shirtNumber: id, skills: { keeper: 1, defending: 4, playmaking: 8, winger: 6, passing: 5, scoring: 3, setPieces: 2 }
  });
  const entry = (id: number, full = 0, half = 0): TrainingPlayerEntry => ({
    playerId: id, playerName: `Player ${id}`, age: 18, slot: '', fullTraining: full >= 90,
    trainedSkillValue: 8, estimatedWeeksToNextLevel: null,
    observedFullMinutes: full, observedHalfMinutes: half, isEstimate: true, warnings: []
  });
  const summary = (players = [entry(1), entry(2)]): TrainingSummary => ({
    teamId: 991234, trainingTypeCode: 8, trainingTypeName: 'Playmaking', trainingTypeKnown: true,
    trainedSkill: 'Playmaking', trainingLevel: 100, staminaTrainingPart: 10, trainerName: 'Coach',
    lastMatchId: 0, lastMatchDate: null, weekStart: '2026-10-15T20:00:00Z', weekEnd: '2026-10-16T12:00:00Z', players,
    isEstimate: true, warnings: ['Training speed is partial: configure the coach factor.']
  });
  const plan = (): WeeklyTrainingPlan => ({
    version: 1, teamId: 991234, weekStart: '2026-10-15T20:00:00Z', selectedPlayerIds: [1],
    matchDates: { league: '2026-10-17T12:00:00Z', friendly: '2026-10-20T12:00:00Z' },
    assignments: [], updatedAt: ''
  });
  beforeEach(() => { service = new WeeklyPlanService(); });

  it('distinguishes CHPP numeric league and friendly fixtures without turning cup matches into league matches', () => {
    expect(trainingMatchKind('1')).toBe('league');
    for (const code of [4, 5, 8, 9]) expect(trainingMatchKind(code)).toBe('friendly');
    expect(trainingMatchKind('Friendly')).toBe('friendly');
    expect(trainingMatchKind('3')).toBeNull();
    expect(trainingMatchKind(undefined)).toBeNull();
  });

  it('does not mark an impossible eight-substitution goalkeeper plan ready', () => {
    const draft=plan(); draft.selectedPlayerIds=Array.from({length:9},(_,i)=>i+1);
    draft.assignments=draft.selectedPlayerIds.map((id,i)=>({playerId:id,match:'league',slot:'GK',startMinute:i*10,minutes:10}));
    const report=summary(draft.selectedPlayerIds.map(id=>entry(id,80)));
    report.trainingTypeCode=9;
    const result=service.evaluate(draft,report,draft.selectedPlayerIds.map(player));
    expect(result.ready).toBeFalse();
    expect(result.issues.some(i=>i.code==='tooManyOrders')).toBeTrue();
  });

  it('uses the best 90 physical minutes, so two half-effect matches do not make full training', () => {
    expect(bestTraining90(0, 180)).toBe(45);
    expect(bestTraining90(60, 90)).toBe(75);
    expect(bestTraining90(180, 180)).toBe(90);
  });

  it('replaces earlier half-effect minutes with better planned minutes rather than adding weighted totals', () => {
    const draft = plan();
    draft.assignments = [{ playerId: 1, match: 'friendly', slot: 'IM', startMinute: 0, minutes: 60 }];
    const result = service.evaluate(draft, summary([entry(1, 30, 60)]), [player(1)]);
    expect(result.players[0].recorded).toBe(60);
    expect(result.players[0].projected).toBe(90);
    expect(result.ready).toBeTrue();
  });

  it('does not let the same player occupy two simultaneous positions', () => {
    const draft = plan();
    draft.assignments = [
      { playerId: 1, match: 'league', slot: 'RIM', startMinute: 0, minutes: 60 },
      { playerId: 1, match: 'league', slot: 'LIM', startMinute: 45, minutes: 45 }
    ];
    const result = service.evaluate(draft, summary(), [player(1), player(2)]);
    expect(result.issues.some(issue => issue.code === 'playerOverlap')).toBeTrue();
    expect(result.players[0].projected).toBe(0);
    expect(result.ready).toBeFalse();
  });

  it('allows two players to share a slot in consecutive non-overlapping intervals', () => {
    const draft = plan();
    draft.selectedPlayerIds = [1, 2];
    draft.assignments = [
      { playerId: 1, match: 'league', slot: 'IM', startMinute: 0, minutes: 45 },
      { playerId: 2, match: 'league', slot: 'IM', startMinute: 45, minutes: 45 }
    ];
    const result = service.evaluate(draft, summary(), [player(1), player(2)]);
    expect(result.issues.filter(issue => issue.severity === 'error')).toEqual([]);
    expect(result.players.map(p => p.projected)).toEqual([45, 45]);
  });

  it('rejects conflicting slot assignments and incompatible partial formations', () => {
    const draft = plan();
    draft.assignments = [
      { playerId: 1, match: 'league', slot: 'IM', startMinute: 0, minutes: 90 },
      { playerId: 2, match: 'league', slot: 'IM', startMinute: 0, minutes: 90 }
    ];
    expect(service.evaluate(draft, summary(), [player(1), player(2)]).issues.some(i => i.code === 'slotOverlap')).toBeTrue();
    draft.assignments = ['RWB', 'LWB', 'IM', 'RIM', 'LIM', 'RFW', 'FW', 'LFW'].map((slot, index) => ({
      playerId: index + 1, match: 'league', slot, startMinute: 0, minutes: 90
    }));
    expect(service.evaluate(draft, summary(), Array.from({ length: 8 }, (_, index) => player(index + 1))).issues.some(i => i.code === 'illegalFormation')).toBeTrue();
  });

  it('marks missing observed minute data unknown instead of treating it as zero', () => {
    const observed = entry(1);
    observed.warnings = ['Match 1: exact played minutes are unknown because the official lineup timeline could not be reconstructed.'];
    const result = service.evaluate(plan(), summary([observed]), [player(1)]);
    expect(result.players[0].recorded).toBeNull();
    expect(result.players[0].missing).toBeNull();
    expect(result.ready).toBeFalse();
  });

  it('keeps small effects unknown and uses only the specified half effects', () => {
    expect(trainingFactor(8, 'RW')).toBe(0.5);
    expect(trainingFactor(5, 'RWB')).toBe(0.5);
    expect(trainingFactor(8, 'RCD')).toBeNull();
    expect(trainingFactor(6, 'FW')).toBeNull();
    expect(trainingFactor(11, 'GK')).toBeNull();
    expect(trainingFactor(9, 'IM')).toBe(0);
    expect(trainingFactor(12, 'LW')).toBe(1);
  });

  it('excludes played, undated and out-of-week fixtures to prevent double-counting', () => {
    for (const [date, code] of [['2026-10-16T10:00:00Z', 'pastMatch'], ['', 'unknownDate'], ['2026-10-24T10:00:00Z', 'outsideWeek']]) {
      const draft = plan();
      draft.matchDates.league = date;
      draft.assignments = [{ playerId: 1, match: 'league', slot: 'IM', startMinute: 0, minutes: 90 }];
      const result = service.evaluate(draft, summary(), [player(1)]);
      expect(result.issues.some(issue => issue.code === code)).withContext(code).toBeTrue();
      expect(result.players[0].projected).withContext(code).toBe(0);
    }
  });

  it('excludes injury minutes and does not declare uncertain availability ready', () => {
    const draft = plan();
    draft.assignments = [{ playerId: 1, match: 'league', slot: 'IM', startMinute: 0, minutes: 90 }];
    const injured = { ...player(1), injuryLevel: 2 };
    expect(service.evaluate(draft, summary(), [injured]).players[0].projected).toBe(0);
    const unknown = { ...player(1), injuryStatusKnown: false };
    const result = service.evaluate(draft, summary(), [unknown]);
    expect(result.issues.some(i => i.code === 'unknownAvailability')).toBeTrue();
    expect(result.ready).toBeFalse();
  });

  it('persists only within the selected team and week', () => {
    const draft = plan();
    try {
      expect(service.savePlan(draft)).toBeTrue();
      expect(service.getPlan(draft.teamId, draft.weekStart).selectedPlayerIds).toEqual([1]);
      expect(service.getPlan(draft.teamId + 1, draft.weekStart).selectedPlayerIds).toEqual([]);
      expect(service.getPlan(draft.teamId, '2026-10-22T20:00:00Z').selectedPlayerIds).toEqual([]);
    } finally { localStorage.removeItem(`hattrick.training-plan.v1.${draft.teamId}.${draft.weekStart}`); }
  });

  it('suggests both match allocations with legal positions and reports leftover trainees', () => {
    const draft = plan();
    draft.selectedPlayerIds = [1, 2, 3, 4, 5, 6, 7];
    const roster = draft.selectedPlayerIds.map(player);
    const report = summary(draft.selectedPlayerIds.map(id => entry(id)));
    const suggestion = service.suggestAssignments(draft, report, roster);
    expect(suggestion.plan.assignments.filter(a => a.match === 'league').length).toBe(3);
    expect(suggestion.plan.assignments.filter(a => a.match === 'friendly').length).toBe(3);
    expect(suggestion.unassignedPlayerIds).toEqual([7]);
    expect(service.evaluate(suggestion.plan, report, roster).issues.some(i => i.severity === 'error')).toBeFalse();
    expect(draft.assignments.length).toBe(0);
  });

  it('suggests enough full minutes to replace recorded half minutes and preserves manual entries', () => {
    const draft = plan();
    draft.assignments = [{ playerId: 2, match: 'league', slot: 'IM', startMinute: 0, minutes: 90 }];
    const suggestion = service.suggestAssignments(draft, summary([entry(1, 30, 60), entry(2)]), [player(1), player(2)]);
    expect(suggestion.plan.assignments[0]).toEqual(draft.assignments[0]);
    expect(suggestion.plan.assignments.filter(a => a.playerId === 1).reduce((sum, a) => sum + a.minutes, 0)).toBe(60);
    expect(suggestion.unassignedPlayerIds).toEqual([]);
  });

  it('does not allocate invented minutes to a trainee absent from the report', () => {
    const suggestion = service.suggestAssignments(plan(), summary([]), [player(1)]);
    expect(suggestion.unknownPlayerIds).toEqual([1]);
    expect(suggestion.plan.assignments).toEqual([]);
  });

  it('does not propose suspended or optimizer-ineligible players for either fixture', () => {
    for (const unavailable of [{ ...player(1), isSuspended: true }, { ...player(1), canOptimize: false }]) {
      const suggestion = service.suggestAssignments(plan(), summary(), [unavailable]);
      expect(suggestion.plan.assignments).toEqual([]);
      expect(suggestion.unassignedPlayerIds).toEqual([1]);
    }
  });

  it('invalidates manual friendly assignments that the optimizer cannot evaluate', () => {
    const draft = plan();
    draft.assignments = [{ playerId: 1, match: 'friendly', slot: 'IM', startMinute: 0, minutes: 90 }];
    for (const [unavailable, code] of [
      [{ ...player(1), isSuspended: true }, 'suspended'],
      [{ ...player(1), canOptimize: false }, 'optimizerUnavailable']
    ] as const) {
      const assessment = service.evaluate(draft, summary(), [unavailable]);
      expect(assessment.players[0].projected).toBe(0);
      expect(assessment.ready).toBeFalse();
      expect(assessment.issues.some(issue => issue.code === code && issue.match === 'friendly' && issue.severity === 'error')).toBeTrue();
    }
  });

  it('uses the server training deadline across daylight-saving changes and requires the match to finish before it', () => {
    const draft = plan();
    const report = summary();
    draft.weekStart = report.weekStart = '2026-10-22T20:00:00Z';
    report.weekEnd = '2026-10-23T12:00:00Z';
    report.nextTrainingAt = '2026-10-29T21:00:00Z';
    draft.matchDates.friendly = '2026-10-29T19:15:00Z';
    draft.assignments = [{ playerId: 1, match: 'friendly', slot: 'IM', startMinute: 0, minutes: 90 }];
    expect(service.evaluate(draft, report, [player(1)]).ready).toBeTrue();
    draft.matchDates.friendly = '2026-10-29T20:00:00Z';
    expect(service.evaluate(draft, report, [player(1)]).issues.some(i => i.code === 'outsideWeek')).toBeTrue();
  });
});
