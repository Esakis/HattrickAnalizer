import { MatchPlanService, rosterFingerprint, SavedMatchPlan } from './match-plan.service';
import { Player } from '../models/player.model';

describe('MatchPlanService', () => {
  const teamId = 919191;
  const key = `hattrick.matchPlans.v1.${teamId}`;
  const clean = () => Object.keys(localStorage).filter(k => k === key || k.startsWith(`hattrick.matchPlans.v2.${teamId}.`)).forEach(k => localStorage.removeItem(k));
  const instances: MatchPlanService[] = [];
  const serviceInstance = () => { const instance = new MatchPlanService(); instances.push(instance); return instance; };
  beforeEach(clean);
  afterEach(() => { clean(); instances.splice(0).forEach(service=>service.ngOnDestroy()); });
  const plan = (matchId: number): SavedMatchPlan => ({ teamId, matchId, opponentTeamId: 123,
    savedAt: new Date().toISOString(), label:'test', status:'draft', selectedAlternative:0, rosterFingerprint:'x',
    result: { inputSnapshot:{myTeamId:teamId} } as any });
  it('retains separate match plans across service recreation', () => {
    const service = serviceInstance();
    service.save(plan(10)); service.save(plan(20));
    const restored = serviceInstance();
    expect(restored.getPlan(teamId,10,123)?.matchId).toBe(10);
    expect(restored.getPlan(teamId,20,123)?.matchId).toBe(20);
    expect(restored.getPlan(teamId,10,456)).toBeUndefined();
    expect(restored.getPlan(teamId+1,10,123)).toBeUndefined();
  });
  it('does not lose plans when two tabs save different matches', () => {
    const a = serviceInstance(), b = serviceInstance();
    a.save(plan(1)); b.save(plan(2)); a.save(plan(3));
    expect(serviceInstance().getPlans(teamId).map(p => p.matchId).sort()).toEqual([1,2,3]);
    a.ngOnDestroy(); b.ngOnDestroy();
  });
  it('reads legacy plans and overrides only the edited match', () => {
    localStorage.setItem(key, JSON.stringify([plan(1), plan(2)]));
    const service = serviceInstance();
    service.save({...plan(1),label:'updated'});
    expect(service.getPlan(teamId,1,123)?.label).toBe('updated');
    expect(service.getPlan(teamId,2,123)).toBeDefined();
    service.ngOnDestroy();
  });
  it('rejects malformed storage without crashing the page', () => {
    localStorage.setItem(key,'{incomplete');
    expect(serviceInstance().getPlans(teamId)).toEqual([]);
  });
  it('fingerprints relevant roster changes independently of array order', () => {
    const a={playerId:1,form:5,skills:{defending:8}} as Player;
    const b={playerId:2,form:6,skills:{defending:4}} as Player;
    expect(rosterFingerprint([a,b])).toBe(rosterFingerprint([b,a]));
    expect(rosterFingerprint([a,b])).not.toBe(rosterFingerprint([{...a,form:4},b]));
    expect(rosterFingerprint([a,b])).not.toBe(rosterFingerprint([{...a,isSuspended:true},b]));
  });
});
