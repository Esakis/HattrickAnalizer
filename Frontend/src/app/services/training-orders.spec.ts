import { buildTrainingOrders, trainingOrdersCover, trainingTimelineIssues } from './training-orders';
import { TrainingAssignment } from './weekly-plan.service';
import { Player } from '../models/player.model';

describe('Training plan to match orders', () => {
  const assignment=(playerId:number,startMinute:number,minutes:number,slot='IM'):TrainingAssignment=>({playerId,startMinute,minutes,slot,match:'league'});
  const roster=Array.from({length:15},(_,i)=>({playerId:i+1,canOptimize:true,injuryLevel:-1,isSuspended:false} as Player));
  const starting={GK:1,IM:2};
  it('builds a half-time substitution, puts the entrant on the appropriate bench role and covers both intervals',()=>{
    const assignments=[assignment(2,0,45),assignment(3,45,45)];
    const result=buildTrainingOrders(assignments,starting,roster);
    expect(result.substitutions).toEqual([{playerOutId:2,playerInId:3,minute:46,position:-1,behaviour:-1,standing:-1}]);
    expect(result.bench[3]).toBe(3);
    expect(trainingOrdersCover(assignments,starting,result.substitutions)).toBeTrue();
    expect(trainingOrdersCover(assignments,starting,[])).toBeFalse();
  });
  it('fills the remainder using a fresh reserve when a trainee only needs 60 minutes',()=>{
    const assignments=[assignment(2,0,60)];
    const result=buildTrainingOrders(assignments,starting,roster);
    expect(result.substitutions[0].minute).toBe(60);
    expect(result.substitutions[0].playerInId).not.toBe(2);
    expect(trainingOrdersCover(assignments,starting,result.substitutions)).toBeTrue();
  });
  it('rejects eight required substitutions instead of promising nine goalkeepers their remaining minutes',()=>{
    const assignments=Array.from({length:9},(_,i)=>assignment(i+1,i*10,10,'GK'));
    expect(trainingTimelineIssues(assignments)).toContain('tooManyOrders');
    expect(()=>buildTrainingOrders(assignments,{GK:1},roster)).toThrow();
  });
  it('rejects reentry, overlapping slots and unsupported minute marks',()=>{
    expect(trainingTimelineIssues([assignment(2,0,30),assignment(2,60,30)])).toContain('playerReentry');
    expect(trainingTimelineIssues([assignment(2,0,60),assignment(3,30,60)])).toContain('slotOverlap');
    expect(trainingTimelineIssues([assignment(2,0,61)])).toContain('orderMinute');
  });
  it('rejects an entrant already in the starting XI or an unavailable reserve',()=>{
    const assignments=[assignment(2,0,45),assignment(3,45,45)];
    expect(()=>buildTrainingOrders(assignments,{...starting,RIM:3},roster)).toThrowError('trainingLineupMismatch');
    expect(()=>buildTrainingOrders(assignments,starting,roster.map(p=>p.playerId===3?{...p,canOptimize:false}:p))).toThrowError('optimizerUnavailable');
  });
  it('does not count conditional or modified orders as guaranteed training minutes',()=>{
    const assignments=[assignment(2,0,45),assignment(3,45,45)];
    const result=buildTrainingOrders(assignments,starting,roster);
    expect(trainingOrdersCover(assignments,starting,[{...result.substitutions[0],standing:2}])).toBeFalse();
    expect(trainingOrdersCover(assignments,starting,[{...result.substitutions[0],minute:60}])).toBeFalse();
  });
});
