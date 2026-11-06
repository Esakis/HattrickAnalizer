import { Player } from '../models/player.model';
import { TRAINING_FORMATIONS, TrainingRecommendationService } from './training-recommendation.service';
import { trainingFactor } from './weekly-plan.service';

describe('Formation training recommendations', () => {
  const service = new TrainingRecommendationService();
  const player = (playerId: number, overrides: Partial<Player> = {}): Player => ({
    playerId, firstName: `Player ${playerId}`, lastName: 'Test', age: 19, tsi: 1000,
    form: 7, stamina: 7, experience: 3, loyalty: 20, leadership: 3, specialty: '', shirtNumber: playerId,
    injuryLevel: -1, injuryStatusKnown: true, suspensionStatusKnown: true, isSuspended: false,
    skills: { keeper: 2, defending: 5, playmaking: 8, winger: 6, passing: 5, scoring: 4, setPieces: 3 },
    ...overrides
  });
  const roster = (count: number) => Array.from({ length: count }, (_, index) => player(index + 1));

  it('counts full and half positions for the requested formation across two games', () => {
    const result = service.recommend('3-5-2', roster(24)).find(item => item.trainingTypeCode === 8)!;
    expect(result.fullSlotsPerMatch).toBe(3);
    expect(result.halfSlotsPerMatch).toBe(2);
    expect(result.fullCapacity).toBe(6);
    expect(result.halfCapacity).toBe(4);
    expect(result.fullPlayerCount).toBe(6);
    expect(result.halfPlayerCount).toBe(4);
    expect(new Set(result.trainees.map(item => item.playerId)).size).toBe(10);
    expect(result.trainees.every(item => item.minutes === 90 && item.startMinute === 0)).toBeTrue();
  });

  it('uses different capacities for different formations and accepts a single game', () => {
    const result = service.recommend('5-3-2', roster(24), ['friendly']);
    expect(result.find(item => item.trainingTypeCode === 3)!.fullCapacity).toBe(5);
    expect(result.find(item => item.trainingTypeCode === 8)!.fullCapacity).toBe(3);
    expect(result.find(item => item.trainingTypeCode === 8)!.halfCapacity).toBe(0);
    expect(result.find(item => item.trainingTypeCode === 5)!.fullCapacity).toBe(0);
    expect(result.find(item => item.trainingTypeCode === 5)!.halfCapacity).toBe(2);
    expect(result.every(item => item.trainees.every(trainee => trainee.match === 'friendly'))).toBeTrue();
  });

  it('fills full places in both matches before using half places when players are scarce', () => {
    const result = service.recommend('3-5-2', roster(7)).find(item => item.trainingTypeCode === 8)!;
    expect(result.fullPlayerCount).toBe(6);
    expect(result.halfPlayerCount).toBe(1);
    expect(result.trainees.filter(item => item.factor === 1 && item.match === 'friendly').length).toBe(3);
  });

  it('ranks by distinct full trainees before half trainees, without inventing small-effect factors', () => {
    const result = service.recommend('3-5-2', roster(24));
    expect(result[0].trainingTypeCode).toBe(2);
    expect(result[0].fullPlayerCount).toBe(22);
    expect(result.some(item => item.trainingTypeCode === 6 || item.trainingTypeCode === 11)).toBeFalse();
    for (let index = 1; index < result.length; index++) {
      expect(result[index - 1].fullPlayerCount).toBeGreaterThanOrEqual(result[index].fullPlayerCount);
    }
  });

  it('does not recommend injured, suspended, optimizer-ineligible or unknown trained skills', () => {
    const players = [player(1), player(2, { injuryLevel: 1 }), player(3, { isSuspended: true }),
      player(4, { canOptimize: false }), player(5, { skillsAvailable: false }), player(6)];
    players[5].skills.playmakingAvailable = false;
    const result = service.recommend('3-5-2', players).find(item => item.trainingTypeCode === 8)!;
    expect(result.trainees.map(item => item.playerId)).toEqual([1]);
    expect(result.fullCapacity).toBe(6);
    expect(result.fullPlayerCount).toBe(1);
  });

  it('warns about unknown availability and never calls an unknown comparison a role fit', () => {
    const candidate = player(1, { injuryStatusKnown: undefined });
    candidate.skills.keeperAvailable = false;
    const result = service.recommend('3-5-2', [candidate]).find(item => item.trainingTypeCode === 8)!;
    expect(result.trainees[0].reasons).toContain('unknownAvailability');
    expect(result.trainees[0].reasons).not.toContain('roleFit');
    expect(result.trainees[0].skillValue).toBe(8);
  });

  it('prefers younger players with otherwise equal skills and goalkeepers for goalkeeper training', () => {
    const young = player(2, { age: 18 }), older = player(1, { age: 30 });
    const keeper = player(3, { age: 19 });
    keeper.skills.keeper = 12;
    const result = service.recommend('3-5-2', [older, young, keeper], ['league']);
    const goalkeeper = result.find(item => item.trainingTypeCode === 9)!.trainees[0];
    expect(goalkeeper.playerId).toBe(3);
    expect(goalkeeper.roleSkill).toBe('keeper');
    expect(goalkeeper.roleSkillValue).toBe(12);
    expect(goalkeeper.reasons).toContain('roleFit');
    const scoring = service.recommend('5-4-1', [older, young], ['league']).find(item => item.trainingTypeCode === 4)!;
    expect(scoring.trainees[0].playerId).toBe(2);
    expect(scoring.trainees[0].reasons).toContain('young');
  });

  it('keeps all training assignments within the chosen formation and caps each player at one game', () => {
    for (const formation of TRAINING_FORMATIONS) {
      expect(formation.slots.length).toBe(11);
      expect(new Set(formation.slots).size).toBe(11);
      for (const result of service.recommend(formation.name, roster(30))) {
        expect(new Set(result.trainees.map(item => item.playerId)).size).toBe(result.trainees.length);
        for (const item of result.trainees) {
          expect(formation.slots).toContain(item.slot);
          expect(trainingFactor(result.trainingTypeCode, item.slot)).toBe(item.factor);
          expect(result.trainees.filter(other => other.match === item.match && other.slot === item.slot).length).toBe(1);
        }
      }
    }
  });

  it('is deterministic across roster order, deduplicates players and does not mutate input', () => {
    const players = roster(24);
    const before = JSON.stringify(players);
    const result = service.recommend('3-5-2', players);
    expect(service.recommend('3-5-2', [...players].reverse())).toEqual(result);
    expect(service.recommend('3-5-2', [...players, players[0]], ['league', 'friendly', 'friendly'])).toEqual(result);
    expect(JSON.stringify(players)).toBe(before);
  });

  it('handles empty rosters, invalid formations, no games and malformed skills without phantom players', () => {
    expect(service.recommend('invalid', roster(20))).toEqual([]);
    expect(service.recommend('4-4-2', []).every(item => item.fullPlayerCount === 0 && item.halfPlayerCount === 0)).toBeTrue();
    expect(service.recommend('4-4-2', roster(20), []).every(item => item.fullCapacity === 0 && !item.trainees.length)).toBeTrue();
    const candidate = player(1); candidate.skills.playmaking = Number.NaN;
    expect(service.recommend('3-5-2', [candidate]).find(item => item.trainingTypeCode === 8)!.trainees).toEqual([]);
  });
});
