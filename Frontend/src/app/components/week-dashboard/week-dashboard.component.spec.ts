import { isMatchPlanCurrent } from './week-dashboard.component';
import { SavedMatchPlan, rosterFingerprint } from '../../services/match-plan.service';
import { Team } from '../../models/team.model';
import { NextOpponent } from '../../services/data-cache.service';

describe('Dashboard match readiness', () => {
  const team: Team = { teamId: 1, teamName: 'Test', players: [] };
  const match: NextOpponent = { matchId: 10, opponentTeamId: 2, opponentTeamName: 'Opponent', matchDate: '2026-10-25T14:00:00Z' };
  const now = Date.parse('2026-10-23T12:00:00Z');
  const plan = (): SavedMatchPlan => ({
    teamId: 1, matchId: 10, opponentTeamId: 2, matchDate: match.matchDate,
    rosterFingerprint: rosterFingerprint(team.players), teamContextKey: '[null,null]', status: 'ready', savedAt: '2026-10-23T10:00:00Z', label: '', selectedAlternative: 0,
    result: { inputSnapshot: { myTeamId: 1, opponentTeamId: 2, matchId: 10 } }
  } as SavedMatchPlan);

  it('accepts only the current team, fixture date, opponent and roster', () => {
    expect(isMatchPlanCurrent(plan(), team, match, now)).toBeTrue();
    expect(isMatchPlanCurrent({ ...plan(), rosterFingerprint: 'old' }, team, match, now)).toBeFalse();
    expect(isMatchPlanCurrent(plan(), team, { ...match, matchId: 11 }, now)).toBeFalse();
    expect(isMatchPlanCurrent(plan(), team, { ...match, opponentTeamId: 3 }, now)).toBeFalse();
    expect(isMatchPlanCurrent(plan(), team, { ...match, matchDate: '2026-10-25T15:00:00Z' }, now)).toBeFalse();
  });

  it('does not label missing dates, passed kickoffs or mismatched optimizer inputs current', () => {
    expect(isMatchPlanCurrent(plan(), team, { ...match, matchDate: undefined }, now)).toBeFalse();
    expect(isMatchPlanCurrent(plan(), team, match, Date.parse('2026-10-26T00:00:00Z'))).toBeFalse();
    const mismatched = plan();
    mismatched.result.inputSnapshot!.opponentTeamId = 99;
    expect(isMatchPlanCurrent(mismatched, team, match, now)).toBeFalse();
  });
});
