import { Player } from '../models/player.model';
import { TrainingAssignment } from './weekly-plan.service';

export interface PlayerOrder {
  playerOutId: number; playerInId: number; minute: number; position: number; behaviour: number; standing: number;
}
export interface TrainingHandoff {
  teamId: number; weekStart: string; matchDate: string; assignments: TrainingAssignment[];
}
export const PITCH_SLOTS = ['GK','RWB','RCD','CD','LCD','LWB','RW','RIM','IM','LIM','LW','RFW','FW','LFW'];
export const orderMinute = (minute: number): number => minute === 45 ? 46 : minute;
export const playedMinute = (minute: number): number => minute === 46 ? 45 : minute;

/** The training handoff uses a fixed formation and unconditional substitutions.
 * Reject timelines that cannot be faithfully translated, rather than promising minutes. */
export function trainingTimelineIssues(assignments: TrainingAssignment[]): string[] {
  const issues = new Set<string>();
  const groups = new Map<number, TrainingAssignment[]>();
  for (const a of assignments) {
    if (!a || !Number.isInteger(a.playerId) || a.playerId <= 0) { issues.add('missingPlayer'); continue; }
    if (!PITCH_SLOTS.includes(a.slot) || !Number.isInteger(a.startMinute) || !Number.isInteger(a.minutes)
      || a.startMinute < 0 || a.minutes <= 0 || a.startMinute + a.minutes > 90) issues.add('invalidMinutes');
    if (a.startMinute % 5 || (a.startMinute + a.minutes) % 5) issues.add('orderMinute');
    groups.set(a.playerId, [...groups.get(a.playerId) ?? [], a]);
  }
  for (const intervals of groups.values()) {
    intervals.sort((a,b) => a.startMinute - b.startMinute);
    if (new Set(intervals.map(a => a.slot)).size > 1) issues.add('trainingPositionChange');
    for (let i = 1; i < intervals.length; i++) {
      if (intervals[i].startMinute !== intervals[i-1].startMinute + intervals[i-1].minutes) issues.add('playerReentry');
    }
  }
  if (issues.has('missingPlayer')) return [...issues];
  for (let i=0; i<assignments.length; i++) for (let j=i+1; j<assignments.length; j++) {
    const a=assignments[i], b=assignments[j];
    if (a.slot===b.slot && a.startMinute < b.startMinute+b.minutes && b.startMinute < a.startMinute+a.minutes) issues.add('slotOverlap');
  }
  // Each occupancy transition on a slot needs one order, including filling a gap.
  let count = 0;
  for (const slot of new Set(assignments.map(a => a.slot))) {
    const intervals = assignments.filter(a => a.slot === slot);
    const boundaries = new Set(intervals.flatMap(a => [a.startMinute, a.startMinute + a.minutes]).filter(m => m > 0 && m < 90));
    for (const minute of boundaries) {
      const before = intervals.find(a => a.startMinute < minute && a.startMinute + a.minutes >= minute)?.playerId;
      const after = intervals.find(a => a.startMinute <= minute && a.startMinute + a.minutes > minute)?.playerId;
      if (before !== after) count++;
    }
  }
  if (count > 5) issues.add('tooManyOrders');
  return [...issues];
}

/** Complete unspecified intervals with eligible reserves; never select a planned entrant in the XI. */
export function buildTrainingOrders(assignments: TrainingAssignment[], starting: Record<string, number>, roster: Player[]): { bench: number[]; substitutions: PlayerOrder[] } {
  const issues = trainingTimelineIssues(assignments);
  if (issues.length) throw new Error(issues[0]);
  const pitch = { ...starting };
  const used = new Set(Object.values(pitch));
  const planned = new Set(assignments.map(a => a.playerId));
  const bench: number[] = Array(7).fill(0);
  const substitutions: PlayerOrder[] = [];
  for (const a of assignments) {
    if (!(a.slot in pitch)) throw new Error('illegalFormation');
    const player = roster.find(p => p.playerId === a.playerId);
    if (!player || player.canOptimize === false || player.injuryLevel > 0 || player.isSuspended) throw new Error('optimizerUnavailable');
    if (a.startMinute === 0 && pitch[a.slot] !== a.playerId) throw new Error('trainingLineupMismatch');
    if (a.startMinute > 0 && used.has(a.playerId)
      && !assignments.some(b => b.playerId === a.playerId && b.startMinute === 0)) throw new Error('trainingLineupMismatch');
  }
  const boundaries = [...new Set(assignments.flatMap(a => [a.startMinute, a.startMinute + a.minutes]))]
    .filter(m => m > 0 && m < 90).sort((a,b) => a-b);
  for (const minute of boundaries) {
    for (const slot of new Set(assignments.map(a => a.slot))) {
      const before = assignments.find(a => a.slot === slot && a.startMinute < minute && a.startMinute + a.minutes >= minute);
      const after = assignments.find(a => a.slot === slot && a.startMinute <= minute && a.startMinute + a.minutes > minute);
      if (before?.playerId === after?.playerId) continue;
      let next = after?.playerId;
      if (!next) next = roster.find(p => !used.has(p.playerId) && !planned.has(p.playerId)
        && p.canOptimize !== false && p.injuryLevel <= 0 && !p.isSuspended)?.playerId;
      if (!next) throw new Error('trainingReserves');
      if (used.has(next)) throw new Error('playerReentry');
      const role = slot === 'GK' ? 0 : ['RCD','CD','LCD'].includes(slot) ? 1 : ['RWB','LWB'].includes(slot) ? 2 : ['RIM','IM','LIM'].includes(slot) ? 3 : ['RFW','FW','LFW'].includes(slot) ? 4 : 5;
      const benchSlot = !bench[role] ? role : !bench[6] ? 6 : bench.findIndex(id=>!id);
      if (benchSlot < 0) throw new Error('trainingReserves');
      bench[benchSlot] = next; used.add(next);
      substitutions.push({playerOutId: pitch[slot], playerInId: next, minute: orderMinute(minute), position: -1, behaviour: -1, standing: -1});
      pitch[slot] = next;
    }
  }
  return {bench: [...bench, ...Array(Math.max(0,7-bench.length)).fill(0)], substitutions};
}

/** Verify actual unconditional orders still cover every interval promised by the plan. */
export function trainingOrdersCover(assignments: TrainingAssignment[], starting: Record<string, number>, orders: PlayerOrder[]): boolean {
  if (trainingTimelineIssues(assignments).length || orders.length > 5 || orders.some(o => o.standing !== -1 || o.minute < 0)) return false;
  const pitch = {...starting};
  const used = new Set(Object.values(pitch));
  const events = [...orders].sort((a,b) => playedMinute(a.minute)-playedMinute(b.minute));
  const boundaries = [...new Set([0,90,...assignments.flatMap(a => [a.startMinute,a.startMinute+a.minutes]),
    ...events.map(o => playedMinute(o.minute)).filter(m => m >= 0 && m <= 90)])].sort((a,b)=>a-b);
  for (let i=0;i<boundaries.length-1;i++) {
    const minute=boundaries[i];
    for (const order of events.filter(o=>playedMinute(o.minute)===minute)) {
      const slot=Object.keys(pitch).find(s=>pitch[s]===order.playerOutId);
      if (!slot) return false;
      const target=order.position < 0 ? slot : PITCH_SLOTS[order.position];
      if (!target || (target!==slot && pitch[target])) return false;
      if (order.playerInId !== order.playerOutId && used.has(order.playerInId)) return false;
      used.add(order.playerInId);
      delete pitch[slot]; pitch[target]=order.playerInId;
    }
    if (assignments.some(a=>a.startMinute<=minute && a.startMinute+a.minutes>minute && pitch[a.slot]!==a.playerId)) return false;
  }
  return true;
}
