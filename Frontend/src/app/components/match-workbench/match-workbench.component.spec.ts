import { of, Subject } from 'rxjs';
import { MatchWorkbenchComponent } from './match-workbench.component';
import { OptimizerResponse } from '../../models/lineup.model';

describe('MatchWorkbenchComponent', () => {
  let component: MatchWorkbenchComponent;
  let api: any;
  beforeEach(() => {
    api={evaluateLineup:jasmine.createSpy().and.returnValue(of({alternatives:[]})),sendMatchOrders:jasmine.createSpy()};
    component=new MatchWorkbenchComponent(api,{getPlan:()=>undefined} as any,{instant:(s:string)=>s} as any,{} as any);
    component.response={inputSnapshot:{myTeamId:1,opponentTeamId:2,matchId:3,matchDate:'2099-01-01T00:00:00Z'},alternatives:[]} as unknown as OptimizerResponse;
    component.nextMatch={matchId:3,opponentTeamId:2,opponentTeamName:'Opponent'};
  });
  it('clears previous fixture orders when a new match has no saved orders', () => {
    api.getTrainingSummary=()=>of({teamId:1});
    component.lineup={positions:{GK:{player:{playerId:1},behavior:'GK'}},tacticType:'Normal',setPiecesTakerId:1} as any;
    component.players=[{playerId:1,skills:{setPieces:1},experience:1,leadership:1}] as any;
    component.lastKey='1:2:2';
    component.bench[0]=77; component.kickers[0]=77; component.addSubstitution();
    component.ngOnChanges();
    expect(component.bench.every(id=>id===0)).toBeTrue();
    expect(component.kickers.every(id=>id===0)).toBeTrue();
    expect(component.substitutions).toEqual([]);
  });

  it('swaps occupied slots instead of creating duplicate players', () => {
    component.rows=[{slot:'RIM',playerId:1,behaviour:'RIM'},{slot:'LIM',playerId:2,behaviour:'LIM'}];
    component.changePlayer(component.rows[0],2);
    expect(component.rows.map(r=>r.playerId)).toEqual([2,1]);
    expect(component.dirty).toBeTrue();
  });
  it('requires a current preview and blocks demo, stale and wrong-match sends', () => {
    expect(component.canSend).toBeFalse();
    component.preview={payload:{}};
    expect(component.canSend).toBeTrue();
    component.mockMode=true; expect(component.canSend).toBeFalse();
    component.mockMode=false; component.stale=true; expect(component.canSend).toBeFalse();
    component.stale=false; component.nextMatch!.matchId=4; expect(component.canSend).toBeFalse();
  });
  it('invalidates preview when orders or lineup change', () => {
    component.preview={}; component.ordersChanged(); expect(component.preview).toBeNull();
    component.preview={}; component.edit(); expect(component.preview).toBeNull(); expect(component.dirty).toBeTrue();
  });
  it('passes the selected set-piece taker into manual evaluation', async () => {
    component.lineup={formation:'4-4-2'} as any;
    component.setPiecesTakerId=42;
    component.edit();
    await component.evaluate();
    expect(api.evaluateLineup.calls.mostRecent().args[3]).toBe(42);
  });
  it('blocks preview, save and sending when training intervals are not covered', async () => {
    component.trainingPlan={teamId:1,weekStart:'',matchDate:'',assignments:[{playerId:2,match:'league',slot:'IM',startMinute:45,minutes:45}]};
    component.rows=[{slot:'IM',playerId:1,behaviour:'IM'}];
    api.previewMatchOrders=jasmine.createSpy();
    (component as any).plans.save=jasmine.createSpy();
    component.preview={};
    expect(component.canSend).toBeFalse();
    await component.preparePreview(); await component.save();
    expect(api.previewMatchOrders).not.toHaveBeenCalled();
    expect((component as any).plans.save).not.toHaveBeenCalled();
  });
  it('supports five orders and special timing criteria', () => {
    for(let i=0;i<6;i++) component.addSubstitution();
    expect(component.substitutions.length).toBe(5);
    for(const minute of [-1,45,46,90,91,115]) expect(component.orderMinutes).toContain(minute);
  });

  it('does not evaluate a stale lineup', async () => {
    component.stale=true;
    await component.evaluate();
    expect(api.evaluateLineup).not.toHaveBeenCalled();
  });

  it('records the submitted lineup even when the visible variant changes during sending', async () => {
    const pending = new Subject<any>();
    api.sendMatchOrders.and.returnValue(pending.asObservable());
    api.captureForecast=jasmine.createSpy().and.returnValue(of({complete:true}));
    const save=jasmine.createSpy().and.returnValue(true);
    (component as any).plans={save};
    component.rows=[{slot:'GK',playerId:10,behaviour:'GK'}];
    component.captainId=10; component.setPiecesTakerId=10;
    const lineup:any={formation:'4-4-2',tacticType:'Normal',positions:{GK:{player:{playerId:10},behavior:'GK'}},predictedRatings:{midfield:5}};
    component.lineup=lineup;
    component.response.optimalLineup=lineup;
    component.response.inputSnapshot!.formationExperience={'4-4-2':7};
    component.preview={payload:{}};
    const sending=component.send();
    component.rows[0].playerId=20;
    component.response.optimalLineup.positions['GK'].player!.playerId=20;
    component.stale=true;
    pending.next({success:true}); pending.complete();
    await sending;
    expect(save.calls.mostRecent().args[0].orders.positions.GK.playerId).toBe(10);
    expect(save.calls.mostRecent().args[0].result.optimalLineup.positions.GK.player.playerId).toBe(10);
    expect(save.calls.mostRecent().args[0].status).toBe('sent');
  });
});
