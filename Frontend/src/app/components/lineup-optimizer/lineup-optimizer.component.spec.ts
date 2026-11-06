import { Subject, of } from 'rxjs';
import { TranslateService } from '@ngx-translate/core';
import { Lineup, OptimizerResponse } from '../../models/lineup.model';
import { Player } from '../../models/player.model';
import { DataCacheService } from '../../services/data-cache.service';
import { LineupOptimizerComponent } from './lineup-optimizer.component';
import { rosterFingerprint, teamContextFingerprint } from '../../services/match-plan.service';
import { convertToParamMap } from '@angular/router';
import { TRAINING_FORMATIONS } from '../../services/training-formations';

describe('LineupOptimizerComponent', () => {
  let component: LineupOptimizerComponent;
  let cache: DataCacheService;
  let optimizeSubjects: Subject<OptimizerResponse>[];
  let formationExperienceSubjects: Subject<{ [formation: string]: number }>[];
  let api: any;
  const slots = ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'FW'];

  function player(playerId: number): Player {
    return {
      playerId, firstName: 'Player', lastName: `${playerId}`, age: 25, tsi: 1,
      skills: { keeper: 1, defending: 1, playmaking: 1, winger: 1, passing: 1, scoring: 1, setPieces: 1 },
      form: 5, stamina: 5, experience: playerId, loyalty: 1, leadership: 1,
      specialty: '', injuryLevel: 0, shirtNumber: playerId
    };
  }

  function lineup(offset: number, positionSlots: string[] = slots): Lineup {
    const positions: Lineup['positions'] = {};
    const behavior: Record<string, string> = { GK: 'GK', RWB: 'WBO', RCD: 'RCD', CD: 'CD', LCD: 'LCD', LWB: 'WBD', RW: 'WO', RIM: 'IMO', IM: 'IM', LIM: 'IMO', LW: 'WTM', FW: 'FTW', RFW: 'FTW', LFW: 'FTW' };
    positionSlots.forEach((slot, index) => positions[slot] = {
      position: slot, player: player(offset + index), behavior: behavior[slot], rating: offset + index
    });
    return {
      positions, tacticType: 'Counter', tacticSkill: 'excellent', formation: '4-5-1',
      predictedRatings: { midfield: offset, rightDefense: offset, centralDefense: offset, leftDefense: offset, rightAttack: offset, centralAttack: offset, leftAttack: offset, overall: offset }
    };
  }

  function response(firstOffset = 100, secondOffset = 200): OptimizerResponse {
    const first = lineup(firstOffset);
    const second = lineup(secondOffset, ['GK', 'RCD', 'CD', 'LCD', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'LFW']);
    second.formation = '3-5-2';
    const ratings = first.predictedRatings;
    return {
      optimalLineup: first,
      recommendations: ['recommendation'],
      comparison: { myTeamRatings: ratings, opponentRatings: ratings, strengths: [], weaknesses: [] },
      alternatives: [
        { formation: '4-5-1', tactic: 'Counter', attitude: 'Normal', winProbability: .4, drawProbability: .3, lossProbability: .3, expectedGoalsFor: 1.5, expectedGoalsAgainst: 1, expectedPoints: 1.5, disorderRisk: .1, ratings, lineup: first },
        { formation: '3-5-2', tactic: 'Normal', attitude: 'PIC', winProbability: .2, drawProbability: .6, lossProbability: .2, expectedGoalsFor: 1, expectedGoalsAgainst: 1, expectedPoints: 1.2, disorderRisk: .2, ratings: second.predictedRatings, lineup: second }
      ],
      inputSnapshot: { myTeamId: 1, opponentTeamId: 2, preferredTactic: 'Auto', teamAttitude: 'Normal', focusAreas: [], coachType: 'Neutral', assistantManagerLevel: 4, objective: 'ExpectedPoints', formationExperience: {}, language: 'en' }
    };
  }

  beforeEach(() => {
    cache = new DataCacheService();
    optimizeSubjects = [];
    formationExperienceSubjects = [];
    api = {
      optimizeLineup: jasmine.createSpy('optimizeLineup').and.callFake(() => {
        const subject = new Subject<OptimizerResponse>();
        optimizeSubjects.push(subject);
        return subject.asObservable();
      }),
      getFormationExperience: jasmine.createSpy('getFormationExperience').and.callFake(() => {
        const subject = new Subject<{ [formation: string]: number }>();
        formationExperienceSubjects.push(subject);
        return subject.asObservable();
      }),
      sendMatchOrders: jasmine.createSpy('sendMatchOrders').and.returnValue(of({ success: true }))
    };
    const translate = {
      currentLang: 'en',
      instant: (key: string) => key,
      onLangChange: new Subject(),
      onTranslationChange: new Subject()
    } as unknown as TranslateService;
    component = new LineupOptimizerComponent(api, translate, cache, {getPlan:()=>undefined,save:()=>true} as any);
    component.myTeamId = 1;
    component.opponentTeamId = 2;
    cache.nextOpponent$.next({ matchId: 42, opponentTeamId: 2, opponentTeamName: 'Opp', isHomeMatch: true });
  });

  it('clears training-specific constraints when selecting a different fixture', () => {
    component.trainingHandoff={teamId:1,weekStart:'',matchDate:'2099-01-01T00:00:00Z',assignments:[{playerId:7,match:'friendly',slot:'IM',startMinute:0,minutes:90}]};
    component.requiredPlayerIds=[7,8]; component.excludedPlayerIds=[9]; component.lockedPositions={IM:7}; component.requiredPositionSlots=['IM'];
    component.upcomingMatches=[{matchId:50,opponentTeamId:2,opponentTeamName:'Other',matchDate:'2099-01-02T00:00:00Z'}];
    spyOn(component,'loadOpponentTeam'); spyOn(component,'loadOpponentTeamStats'); spyOn(component,'loadOpponentScout');
    component.selectUpcomingMatch(50);
    expect(component.trainingHandoff).toBeNull();
    expect(component.requiredPlayerIds).toEqual([8]);
    expect(component.excludedPlayerIds).toEqual([9]);
    expect(component.lockedPositions).toEqual({});
    expect(component.requiredPositionSlots).toEqual([]);
  });

  function initializeTrainingQuery(formation = '3-5-2'): void {
    (component as any).route = { snapshot: { queryParamMap: convertToParamMap({
      trainingMatch: 'friendly', trainingFormation: formation,
      trainingMatchDate: '2099-01-01T00:00:00Z', traineeIds: '7', trainingSlots: 'IM',
      trainingAssignments: JSON.stringify([{ playerId: 7, match: 'friendly', slot: 'IM', startMinute: 0, minutes: 90 }])
    }) } };
    spyOn(component, 'loadOpponentTeam');
    spyOn(component, 'loadOpponentTeamStats');
    spyOn(component, 'loadOpponentScout');
    component.ngOnInit();
  }

  it('hands the chosen training formation and every required position to the optimizer', () => {
    initializeTrainingQuery();
    expect(component.selectedMyFormation).toBe('3-5-2');
    const request = (component as any).createRequestSnapshot();
    expect(request.preferredFormation).toBe('3-5-2');
    expect(new Set(request.requiredPositionSlots)).toEqual(new Set(TRAINING_FORMATIONS.find(f => f.name === '3-5-2')!.slots));
    component.ngOnDestroy();
  });

  it('keeps a fresh training formation when an older saved match plan is restored', () => {
    initializeTrainingQuery();
    component.myTeamPlayers = [player(7)];
    const result = response();
    result.inputSnapshot!.preferredFormation = '4-4-2';
    result.inputSnapshot!.requiredPositionSlots = ['RWB'];
    (component as any).plans = { getPlan: () => ({ result, selectedAlternative: 0 }) };
    (component as any).tryRestorePlan();
    expect(component.selectedMyFormation).toBe('3-5-2');
    expect(component.requiredPositionSlots).toContain('IM');
    expect(component.requiredPositionSlots).not.toContain('RWB');
    expect(component.resultStale).toBeTrue();
    component.ngOnDestroy();
  });

  it('clears the imported formation on fixture change while preserving a manual formation change', () => {
    initializeTrainingQuery();
    component.upcomingMatches = [{ matchId: 50, opponentTeamId: 2, opponentTeamName: 'Other', matchDate: '2099-01-02T00:00:00Z' }];
    component.selectUpcomingMatch(50);
    expect(component.selectedMyFormation).toBe('Auto');
    expect(component.requiredPositionSlots).toEqual([]);

    component.trainingHandoff = { teamId: 1, weekStart: '', matchDate: '2099-01-01T00:00:00Z', assignments: [] };
    (component as any).trainingFormation = '3-5-2';
    component.selectedMyFormation = '4-4-2';
    component.selectUpcomingMatch(50);
    expect(component.selectedMyFormation).toBe('4-4-2');
    component.ngOnDestroy();
  });

  it('ignores an unsupported training formation instead of passing it to the backend', () => {
    initializeTrainingQuery('1-1-8');
    expect(component.selectedMyFormation).toBe('Auto');
    expect(component.requiredPositionSlots).toEqual(['IM']);
    component.ngOnDestroy();
  });

  it('loads formation experience even when the dashboard has already cached the roster', () => {
    component.myTeamPlayers = [player(1)];
    component.lastLoadedMyTeamId = 1;
    component.loadMyTeam();
    expect(api.getFormationExperience).toHaveBeenCalledWith(1);
    formationExperienceSubjects[0].next({'4-4-2':10});
    expect(component.formationExperience['4-4-2']).toBe(10);
  });

  it('switches the displayed full lineup and exports the selected alternative snapshot', () => {
    component.result = response();
    component.resultStale = false;
    component.selectAlternative(1);
    (component as any).resultTeamContext = (component as any).currentTeamContext();

    expect(component.displayedLineup.positions['GK']?.player?.playerId).toBe(200);
    expect(component.displayedLineup.positions['GK']?.behavior).toBe('GK');
    expect(component.displayedRatings?.midfield).toBe(200);
    expect(component.canSendOrders).toBeTrue();

    component.sendLineupToHattrick();
    const request = api.sendMatchOrders.calls.mostRecent().args[0];
    expect(Object.keys(request.positions).length).toBe(11);
    expect(request.positions['GK'].playerId).toBe(200);
    expect(request.positions['RCD'].behaviour).toBe('RCD');
    expect(request.attitude).toBe('PIC');
    expect(request.assistantManagerLevel).toBe(4);
    expect(component.ordersSuccess).toBeTrue();
  });

  it('keeps an explicitly unlinked draft separate from the next real match', () => {
    component.selectUpcomingMatch(0);
    expect(component.nextMatch).toBeNull();
    expect((component as any).createRequestSnapshot().matchId).toBe(0);
  });

  it('preserves bench and substitution drafts when selecting another alternative', () => {
    const orders = {bench:[{playerId:999,positionCode:0}],substitutions:[{minute:60}]};
    const plans = {getPlan:()=>({orders}),save:jasmine.createSpy().and.returnValue(true)};
    (component as any).plans = plans;
    component.result=response();
    component.selectAlternative(1);
    expect(plans.save.calls.mostRecent().args[0].orders).toEqual(orders);
    expect(plans.save.calls.mostRecent().args[0].status).toBe('draft');
  });

  it('restores a matching saved result and detects changed form or team context', () => {
    const result=response();
    result.inputSnapshot!.matchId=42;
    result.inputSnapshot!.matchDate='2099-01-01T00:00:00Z';
    result.inputSnapshot!.isHomeMatch=true;
    result.inputSnapshot!.teamSpiritLevel=5;
    result.inputSnapshot!.confidenceLevel=5;
    cache.nextOpponent$.next({ ...cache.nextOpponent$.value!, matchDate: result.inputSnapshot!.matchDate });
    const players=Object.values(result.optimalLineup.positions).map(p=>p.player!);
    const team={teamId:1,teamName:'Test',players,teamSpiritLevel:5,confidenceLevel:5};
    cache.ownTeam$.next(team);
    component.myTeamPlayers=players;
    const saved={result,referenceResult:result,selectedAlternative:1,rosterFingerprint:rosterFingerprint(players),teamContextKey:teamContextFingerprint(team),matchDate:result.inputSnapshot!.matchDate};
    (component as any).plans={getPlan:()=>saved};
    (component as any).tryRestorePlan();
    expect(component.result).toBe(result);
    expect(component.selectedAlternative).toBe(1);
    expect(component.resultStale).toBeFalse();
    expect(component.teamSpiritLevel).toBe(5);
    expect(component.confidenceLevel).toBe(5);
    component.result=null; (component as any).restoredPlanKey='';
    cache.formationExperience$.next({'4-4-2':10});
    (component as any).tryRestorePlan();
    expect(component.formationExperience['4-4-2']).toBe(10);
    expect(component.resultStale).toBeTrue();
    component.result=null; (component as any).restoredPlanKey='';
    component.myTeamPlayers=players.map((p,i)=>i===0?{...p,form:p.form+1}:p);
    (component as any).tryRestorePlan();
    expect(component.resultStale).toBeTrue();
    component.result=null; (component as any).restoredPlanKey=''; component.myTeamPlayers=players;
    cache.ownTeam$.next({...team,teamSpiritLevel:4,confidenceLevel:7});
    (component as any).tryRestorePlan();
    expect(component.resultStale).toBeTrue();
    expect(component.teamSpiritLevel).toBe(4);
    expect(component.confidenceLevel).toBe(7);
    expect((component as any).createRequestSnapshot().teamSpiritLevel).toBe(4);
    expect((component as any).createRequestSnapshot().confidenceLevel).toBe(7);
  });

  [
    { description: 'kickoff changes', matchDate: '2099-01-02T00:00:00Z', isHomeMatch: true },
    { description: 'home/away changes', matchDate: '2099-01-01T00:00:00Z', isHomeMatch: false }
  ].forEach(fixture => {
    it(`marks a restored plan stale when the same fixture's ${fixture.description}`, () => {
      const result = response();
      Object.assign(result.inputSnapshot!, { matchId: 42, matchDate: '2099-01-01T00:00:00Z', isHomeMatch: true });
      const players = Object.values(result.optimalLineup.positions).map(p => p.player!);
      const team = { teamId: 1, teamName: 'Test', players, teamSpiritLevel: 5, confidenceLevel: 5 };
      cache.ownTeam$.next(team);
      cache.nextOpponent$.next({ ...cache.nextOpponent$.value!, matchDate: fixture.matchDate, isHomeMatch: fixture.isHomeMatch });
      component.myTeamPlayers = players;
      (component as any).plans = { getPlan: () => ({
        result, referenceResult: result, selectedAlternative: 1, rosterFingerprint: rosterFingerprint(players),
        teamContextKey: teamContextFingerprint(team), matchDate: result.inputSnapshot!.matchDate
      }) };

      (component as any).tryRestorePlan();

      expect(component.result).toBe(result);
      expect(component.resultStale).toBeTrue();
      expect(cache.optimizerResult$.value).toBeNull();
      expect(component.canSendOrders).toBeFalse();
      const request = (component as any).createRequestSnapshot();
      expect(request.matchId).toBe(42);
      expect(request.matchDate).toBe(fixture.matchDate);
      expect(request.isHomeMatch).toBe(fixture.isHomeMatch);
    });
  });

  it('shows backend success=false as a failed send', () => {
    api.sendMatchOrders.and.returnValue(of({ success: false, error: 'denied' }));
    component.result = response();
    component.resultStale = false;
    (component as any).resultTeamContext = (component as any).currentTeamContext();

    component.sendLineupToHattrick();

    expect(component.ordersSuccess).toBeFalse();
    expect(component.ordersError).toBe('denied');
  });

  it('localizes the deliberately unvalidated confidence label', () => {
    expect(component.getConfidenceLabel('unvalidated-low')).toBe('optimizer.confidenceLevels.unvalidatedLow');
  });

  it('localizes the scout source alias and backend unvalidated warning', () => {
    const result = response();
    result.modelWarnings = ['The model is unvalidated as a real-match forecast.'];
    component.result = result;

    expect(component.getOpponentSourceLabel('scout')).toBe('optimizer.sources.weightedScout');
    expect(component.modelWarnings).toContain('optimizer.warnings.unvalidated');
  });

  it('reports optimizer request errors and clears loading', () => {
    component.optimizeLineup();
    optimizeSubjects[0].error(new Error('network unavailable'));
    expect(component.loading).toBeFalse();
    expect(component.error).toContain('network unavailable');
    expect(component.resultStale).toBeTrue();
  });

  it('refuses to export an incomplete lineup', () => {
    const incomplete = response();
    delete incomplete.alternatives[0].lineup.positions['FW'];
    delete incomplete.optimalLineup.positions['FW'];
    component.result = incomplete;
    component.resultStale = false;
    (component as any).resultTeamContext = (component as any).currentTeamContext();

    component.sendLineupToHattrick();

    expect(api.sendMatchOrders).not.toHaveBeenCalled();
    expect(component.ordersError).toBe('matchOrders.invalidLineup');
  });

  it('refuses duplicate players and a lineup without its goalkeeper', () => {
    const duplicate = response();
    duplicate.alternatives[0].lineup.positions['FW'].player = duplicate.alternatives[0].lineup.positions['GK'].player;
    component.result = duplicate;
    component.resultStale = false;
    (component as any).resultTeamContext = (component as any).currentTeamContext();
    component.sendLineupToHattrick();
    expect(api.sendMatchOrders).not.toHaveBeenCalled();

    const noKeeper = response();
    delete noKeeper.alternatives[0].lineup.positions['GK'];
    component.result = noKeeper;
    component.ordersError = null;
    (component as any).resultTeamContext = (component as any).currentTeamContext();
    component.sendLineupToHattrick();
    expect(api.sendMatchOrders).not.toHaveBeenCalled();
  });

  it('clones formation experience into the optimizer request', () => {
    component.formationExperience = { '4-4-2': 6 };
    component.optimizeLineup();
    const sentRequest = api.optimizeLineup.calls.mostRecent().args[0];

    component.formationExperience['4-4-2'] = 10;
    expect(sentRequest.formationExperience['4-4-2']).toBe(6);
  });

  it('starts with Win and sends Auto formation and tactic unless the user changes them', () => {
    expect(component.objective).toBe('Win');
    expect(component.selectedMyFormation).toBe('Auto');
    expect(component.selectedMyTactic).toBe('Auto');

    component.optimizeLineup();
    let request = api.optimizeLineup.calls.mostRecent().args[0];
    expect(request.objective).toBe('Win');
    expect(request.preferredFormation).toBe('Auto');
    expect(request.preferredTactic).toBe('Auto');

    component.objective = 'Draw';
    component.selectedMyTactic = 'Counter';
    component.selectedMyFormation = '4-5-1';
    component.onMyTacticChange();
    component.onMyFormationChange();
    component.optimizeLineup();
    request = api.optimizeLineup.calls.mostRecent().args[0];
    expect(request.objective).toBe('Draw');
    expect(request.preferredFormation).toBe('4-5-1');
    expect(request.preferredTactic).toBe('Counter');
  });

  it('keeps missing formation experience unknown and omits it from the request', () => {
    component.formationExperience = { '4-4-2': 6 };
    expect(component.getFormationExperience('4-4-2')).toBe(6);
    expect(component.getFormationExperience('3-5-2')).toBeNull();

    component.optimizeLineup();
    const request = api.optimizeLineup.calls.mostRecent().args[0];
    expect(request.formationExperience['4-4-2']).toBe(6);
    expect(request.formationExperience['3-5-2']).toBeUndefined();
  });

  it('removes unknown and invalid experience values from state, cache, and requests', () => {
    component.setFormationExperience('4-4-2', 6);
    component.setFormationExperience('4-4-2', null);
    component.setFormationExperience('3-5-2', 11);
    component.setFormationExperience('4-3-3', 6.5);

    expect(component.formationExperience).toEqual({});
    expect(cache.formationExperience$.value).toEqual({});
    component.optimizeLineup();
    expect(api.optimizeLineup.calls.mostRecent().args[0].formationExperience).toEqual({});
  });

  it('preserves known and unknown experience through translation reinitialization', () => {
    component.formationExperience = { '4-4-2': 8 };
    (component as any).initializeTranslations();

    expect(component.getFormationExperience('4-4-2')).toBe(8);
    expect(component.getFormationExperience('3-5-2')).toBeNull();
  });

  it('sanitizes cached experience values before using or keeping them in cache', () => {
    cache.formationExperience$.next({ '4-4-2': 7.5, '3-5-2': 10, 'not-a-formation': 8 } as any);
    component.loadFormationExperience();

    expect(component.formationExperience).toEqual({ '3-5-2': 10 });
    expect(cache.formationExperience$.value).toEqual({ '3-5-2': 10 });
    expect(api.getFormationExperience).not.toHaveBeenCalled();
  });

  it('rejects invalid API experience and ignores stale responses after team switches, including switching back', () => {
    component.myTeamId = 1;
    component.loadFormationExperience();
    component.myTeamId = 2;
    (component as any).clearFormationExperience();
    component.loadFormationExperience();
    component.myTeamId = 1;
    (component as any).clearFormationExperience();
    component.loadFormationExperience();

    formationExperienceSubjects[0].next({ '4-4-2': 10 });
    formationExperienceSubjects[1].next({ '4-4-2': 8 });
    expect(component.formationExperience).toEqual({});

    formationExperienceSubjects[2].next({ '4-4-2': 11, '3-5-2': 7.5, '4-3-3': 6 });
    expect(component.formationExperience).toEqual({ '4-3-3': 6 });
    expect(cache.formationExperience$.value).toEqual({ '4-3-3': 6 });
  });

  it('persists the objective in UI cache and uses Win for older cached state', () => {
    component.objective = 'Draw';
    component.onObjectiveChange();
    expect(cache.optimizerUi$.value.objective).toBe('Draw');

    component.objective = 'Win';
    (component as any).restoreFromCache();
    expect(component.objective).toBe('Draw');

    cache.optimizerUi$.next({ ...cache.optimizerUi$.value, objective: undefined as any });
    component.objective = 'ExpectedPoints';
    (component as any).restoreFromCache();
    expect(component.objective).toBe('Win');
  });

  it('maps selected XI attack and defense ratings to the opposite opponent lanes', () => {
    component.result = response();
    component.selectAlternative(1);
    const selectedRatings = component.result.alternatives[1].ratings;
    Object.assign(selectedRatings, {
      rightAttack: 17, centralAttack: 16, leftAttack: 15,
      rightDefense: 27, centralDefense: 26, leftDefense: 25
    });
    Object.assign(component.result.comparison.opponentRatings, {
      leftDefense: 74, centralDefense: 75, rightDefense: 76,
      leftAttack: 84, centralAttack: 85, rightAttack: 86
    });

    expect(component.attackDefenseMatchups.map(matchup => [
      component.displayedRatings![matchup.myKey], component.result!.comparison.opponentRatings[matchup.opponentKey]
    ])).toEqual([[17, 74], [16, 75], [15, 76]]);
    expect(component.defenseAttackMatchups.map(matchup => [
      component.displayedRatings![matchup.myKey], component.result!.comparison.opponentRatings[matchup.opponentKey]
    ])).toEqual([[27, 84], [26, 85], [25, 86]]);
  });

  it('derives alternative strengths from matched lanes rather than same-named sectors', () => {
    component.result = response();
    component.selectAlternative(1);
    const ratings = component.result.alternatives[1].ratings;
    Object.assign(ratings, {
      midfield: 8,
      rightAttack: 20, centralAttack: 5, leftAttack: 8,
      rightDefense: 9, centralDefense: 5, leftDefense: 5
    });
    Object.assign(component.result.comparison.opponentRatings, {
      midfield: 7,
      rightAttack: 10, centralAttack: 4, leftAttack: 5,
      rightDefense: 5, centralDefense: 8, leftDefense: 30
    });
    (component as any).translate.instant = (key: string, params?: { lane: string; opponentLane: string }) =>
      params ? `${key}:${params.lane}|${params.opponentLane}` : key;

    expect(component.displayedStrengths).toEqual([
      'optimizer.comparison.matchupLabels.midfield',
      'optimizer.comparison.matchupLabels.attack:optimizer.comparison.lanes.left|optimizer.comparison.lanes.right',
      'optimizer.comparison.matchupLabels.defense:optimizer.comparison.lanes.right|optimizer.comparison.lanes.left',
      'optimizer.comparison.matchupLabels.defense:optimizer.comparison.lanes.center|optimizer.comparison.lanes.center'
    ]);
    expect(component.displayedWeaknesses).toEqual([
      'optimizer.comparison.matchupLabels.attack:optimizer.comparison.lanes.right|optimizer.comparison.lanes.left',
      'optimizer.comparison.matchupLabels.attack:optimizer.comparison.lanes.center|optimizer.comparison.lanes.center',
      'optimizer.comparison.matchupLabels.defense:optimizer.comparison.lanes.left|optimizer.comparison.lanes.right'
    ]);
  });

  it('keeps backend descriptions for the selected primary alternative', () => {
    const result = response();
    result.comparison.strengths = ['Mocna obrona / Strong defense'];
    result.comparison.weaknesses = ['Słaba pomoc / Weak midfield'];
    component.result = result;
    component.selectedAlternative = 0;

    expect(component.displayedStrengths).toEqual(['Strong defense']);
    expect(component.displayedWeaknesses).toEqual(['Weak midfield']);
  });

  it('uses the shared 2.75 exponent for a midfield-based chance-share estimate', () => {
    const share = Math.pow(2, 2.75) / (Math.pow(2, 2.75) + Math.pow(1, 2.75));
    expect(component.getPredictedChanceShare(2, 1, 'my')).toBeCloseTo(share, 10);
    expect(component.getPredictedChanceShare(2, 1, 'opp')).toBeCloseTo(1 - share, 10);
    expect(component.getPredictedActions(2, 1, 'my')).toBeCloseTo(share * 10, 10);
  });

  it('keeps only the latest optimizer response and invalidates a request after input changes', () => {
    component.optimizeLineup();
    component.teamAttitude = 'MOTS';
    component.onOptimizerInputChange();
    component.optimizeLineup();
    optimizeSubjects[0].next(response(10, 20));
    expect(component.result).toBeNull();

    optimizeSubjects[1].next(response(30, 40));
    expect(component.result?.optimalLineup.positions['GK']?.player?.playerId).toBe(30);

    component.onOptimizerInputChange();
    expect(component.resultStale).toBeTrue();
    expect(component.canSendOrders).toBeFalse();
  });
});
