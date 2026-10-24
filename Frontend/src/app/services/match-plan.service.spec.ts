import { MatchPlanService, rosterFingerprint, SavedMatchPlan } from './match-plan.service';
import { Player } from '../models/player.model';

describe('MatchPlanService', () => {
  const teamId = 919191;
  const key = `hattrick.matchPlans.v1.${teamId}`;
  beforeEach(() => localStorage.removeItem(key));
  afterEach(() => localStorage.removeItem(key));
  const plan = (matchId: number): SavedMatchPlan => ({ teamId, matchId, opponentTeamId: 123,
    savedAt: new Date().toISOString(), label:'test', status:'draft', selectedAlternative:0, rosterFingerprint:'x',
    result: { inputSnapshot:{myTeamId:teamId} } as any });
  it('retains separate match plans across service recreation', () => {
    const service = new MatchPlanService();
    service.save(plan(10)); service.save(plan(20));
    const restored = new MatchPlanService();
    expect(restored.getPlan(teamId,10,123)?.matchId).toBe(10);
    expect(restored.getPlan(teamId,20,123)?.matchId).toBe(20);
    expect(restored.getPlan(teamId,10,456)).toBeUndefined();
    expect(restored.getPlan(teamId+1,10,123)).toBeUndefined();
  });
  it('rejects malformed storage without crashing the page', () => {
    localStorage.setItem(key,'{incomplete');
    expect(new MatchPlanService().getPlans(teamId)).toEqual([]);
  });
  it('fingerprints relevant roster changes independently of array order', () => {
    const a={playerId:1,form:5,skills:{defending:8}} as Player;
    const b={playerId:2,form:6,skills:{defending:4}} as Player;
    expect(rosterFingerprint([a,b])).toBe(rosterFingerprint([b,a]));
    expect(rosterFingerprint([a,b])).not.toBe(rosterFingerprint([{...a,form:4},b]));
    expect(rosterFingerprint([a,b])).not.toBe(rosterFingerprint([{...a,isSuspended:true},b]));
  });
});
