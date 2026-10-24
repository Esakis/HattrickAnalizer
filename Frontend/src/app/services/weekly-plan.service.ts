import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';
import { Player } from '../models/player.model';
import { TrainingPlayerEntry, TrainingSummary } from '../models/training.model';

export type TrainingMatch = 'league' | 'friendly';

/** CHPP matches.MatchType: league=1, senior friendlies=4/5/8/9.
 * https://wiki.hattrick.org/wiki/CHPP_Development/XML/matches */
export function trainingMatchKind(type?: string | number): TrainingMatch | null {
  const value = String(type ?? '').toLowerCase();
  if (value === '1' || value === 'league') return 'league';
  if (['4', '5', '8', '9'].includes(value) || value.includes('friendly')) return 'friendly';
  return null;
}
export interface TrainingAssignment {
  playerId: number;
  match: TrainingMatch;
  slot: string;
  startMinute: number;
  minutes: number;
}
export interface WeeklyTrainingPlan {
  version: 1;
  teamId: number;
  weekStart: string;
  selectedPlayerIds: number[];
  matchDates: Record<TrainingMatch, string>;
  assignments: TrainingAssignment[];
  updatedAt: string;
}
export interface TrainingPlanIssue {
  code: string;
  severity: 'error' | 'warning';
  playerId?: number;
  match?: TrainingMatch;
}
export interface TrainingCoverage {
  playerId: number;
  recorded: number | null;
  projected: number | null;
  missing: number | null;
  hasUnknownEffect: boolean;
}
export interface TrainingAssessment {
  players: TrainingCoverage[];
  issues: TrainingPlanIssue[];
  ready: boolean;
}
export interface SuggestedTrainingPlan {
  plan: WeeklyTrainingPlan;
  unassignedPlayerIds: number[];
  unknownPlayerIds: number[];
}

export const TRAINING_SLOTS = ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'FW', 'LFW'];
// Keep the same legal position sets as the optimizer's FormationData.
const FORMATIONS = [
  'GK RWB RCD CD LCD LWB RW RIM IM LIM LW',
  'GK RWB RCD CD LCD LWB RW RIM LIM LW FW',
  'GK RWB RCD CD LCD LWB RIM IM LIM RFW LFW',
  'GK RWB RCD LCD LWB RW RIM IM LIM LW FW',
  'GK RWB RCD LCD LWB RW RIM LIM LW RFW LFW',
  'GK RWB RCD LCD LWB RW IM LW RFW FW LFW',
  'GK RCD CD LCD RW RIM IM LIM LW RFW LFW',
  'GK RCD CD LCD RW RIM LIM LW RFW FW LFW',
  'GK RCD LCD RW RIM IM LIM LW RFW FW LFW',
  'GK RWB RCD CD LCD LWB RW LW RFW FW LFW'
].map(value => value.split(' '));

/** Numeric effects only where the backend/manual defines them. null means an
 * unquantified small effect, not zero training. Special set-piece bonuses are
 * deliberately separate from this capped 90-minute basic coverage. */
export function trainingFactor(type: number, slot: string): number | null {
  if (!TRAINING_SLOTS.includes(slot)) return null;
  const defender = ['RWB', 'RCD', 'CD', 'LCD', 'LWB'].includes(slot);
  const wingBack = ['RWB', 'LWB'].includes(slot);
  const winger = ['RW', 'LW'].includes(slot);
  const midfielder = ['RIM', 'IM', 'LIM'].includes(slot);
  const forward = ['RFW', 'FW', 'LFW'].includes(slot);
  switch (type) {
    case 0: case 1: return 0;
    case 2: return 1;
    case 3: return defender ? 1 : null;
    case 4: return forward ? 1 : null;
    case 5: return winger ? 1 : wingBack ? 0.5 : null;
    case 6: return null;
    case 7: return midfielder || winger || forward ? 1 : null;
    case 8: return midfielder ? 1 : winger ? 0.5 : null;
    case 9: return slot === 'GK' ? 1 : 0;
    case 10: return defender || midfielder || winger ? 1 : null;
    case 11: return null;
    case 12: return winger || forward ? 1 : null;
    default: return null;
  }
}

export function bestTraining90(fullMinutes: number, halfMinutes: number): number {
  const full = Math.min(90, Math.max(0, fullMinutes));
  return full + Math.min(90 - full, Math.max(0, halfMinutes)) * 0.5;
}

@Injectable({ providedIn: 'root' })
export class WeeklyPlanService {
  readonly storageChanged$ = new Subject<void>();
  private key(teamId: number, weekStart: string): string {
    return `hattrick.training-plan.v1.${teamId}.${weekStart}`;
  }

  getPlan(teamId: number, weekStart: string = ''): WeeklyTrainingPlan {
    const empty: WeeklyTrainingPlan = {
      version: 1, teamId, weekStart, selectedPlayerIds: [],
      matchDates: { league: '', friendly: '' }, assignments: [], updatedAt: ''
    };
    try {
      const raw = localStorage.getItem(this.key(teamId, weekStart));
      if (!raw) return empty;
      const parsed = JSON.parse(raw) as WeeklyTrainingPlan;
      if (parsed.version !== 1 || parsed.teamId !== teamId || parsed.weekStart !== weekStart
        || !Array.isArray(parsed.selectedPlayerIds) || !Array.isArray(parsed.assignments)) return empty;
      return {
        ...empty, ...parsed,
        selectedPlayerIds: [...new Set(parsed.selectedPlayerIds.filter(id => Number.isInteger(id) && id > 0))],
        matchDates: { ...empty.matchDates, ...parsed.matchDates },
        assignments: parsed.assignments.filter(a => a && Number.isInteger(a.playerId)
          && (a.match === 'league' || a.match === 'friendly') && typeof a.slot === 'string')
      };
    } catch { return empty; }
  }

  savePlan(plan: WeeklyTrainingPlan): boolean {
    try {
      plan.updatedAt = new Date().toISOString();
      localStorage.setItem(this.key(plan.teamId, plan.weekStart), JSON.stringify(plan));
      this.storageChanged$.next();
      return true;
    } catch { return false; }
  }

  /** Fill only free intervals; preserve the user's assignments and never turn
   * missing history into a fictional zero. The optimizer completes each XI. */
  suggestAssignments(plan: WeeklyTrainingPlan, summary: TrainingSummary, roster: Player[]): SuggestedTrainingPlan {
    const proposed: WeeklyTrainingPlan = JSON.parse(JSON.stringify(plan));
    const initial = this.evaluate(proposed, summary, roster);
    const unknownPlayerIds = initial.players.filter(p => p.recorded === null).map(p => p.playerId);
    const candidateSlots = [...new Set([...FORMATIONS]
      .sort((a, b) => b.filter(s => trainingFactor(summary.trainingTypeCode, s) === 1).length
        - a.filter(s => trainingFactor(summary.trainingTypeCode, s) === 1).length)
      .flat())].filter(slot => trainingFactor(summary.trainingTypeCode, slot) === 1);
    for (const playerId of proposed.selectedPlayerIds) {
      const player = roster.find(p => p.playerId === playerId);
      if (!player || player.injuryLevel > 0 || player.isSuspended || player.canOptimize === false
        || unknownPlayerIds.includes(playerId)) continue;
      const observed = summary.players.find(p => p.playerId === playerId)!;
      for (const match of ['league', 'friendly'] as TrainingMatch[]) {
        let attempts = 0;
        while (attempts++ < 30) {
          const assessment = this.evaluate(proposed, summary, roster);
          if (assessment.players.find(p => p.playerId === playerId)?.missing === 0) break;
          // Full minutes must replace any half-effect minutes before full coverage
          // can be claimed. A weighted deficit alone would underallocate time.
          const fullPlanned = proposed.assignments.filter(a => a.playerId === playerId && trainingFactor(summary.trainingTypeCode, a.slot) === 1)
            .reduce((total, a) => total + a.minutes, 0);
          const required = 90 - (observed.observedFullMinutes ?? observed.fullTrainingMinutes ?? 0) - fullPlanned;
          if (required <= 0) break;
          const existing = proposed.assignments.filter(a => a.match === match);
          let placed = false;
          for (const slot of candidateSlots) {
            const busy = existing.filter(a => a.slot === slot || a.playerId === playerId);
            const starts = [...new Set([0, ...busy.map(a => a.startMinute + a.minutes)])].sort((a, b) => a - b);
            for (const startMinute of starts) {
              if (startMinute >= 90 || busy.some(a => a.startMinute <= startMinute && a.startMinute + a.minutes > startMinute)) continue;
              const end = Math.min(90, ...busy.filter(a => a.startMinute > startMinute).map(a => a.startMinute));
              const candidate: TrainingAssignment = { playerId, match, slot, startMinute, minutes: Math.min(required, end - startMinute) };
              proposed.assignments.push(candidate);
              const checked = this.evaluate(proposed, summary, roster);
              if (!checked.issues.some(i => i.severity === 'error')) { placed = true; break; }
              proposed.assignments.pop();
            }
            if (placed) break;
          }
          if (!placed) break;
        }
      }
    }
    const final = this.evaluate(proposed, summary, roster);
    return { plan: proposed, unknownPlayerIds,
      unassignedPlayerIds: final.players.filter(p => p.missing !== 0 && p.missing !== null).map(p => p.playerId) };
  }

  evaluate(plan: WeeklyTrainingPlan, summary: TrainingSummary, roster: Player[]): TrainingAssessment {
    const issues: TrainingPlanIssue[] = [];
    const add = (code: string, severity: 'error' | 'warning', playerId?: number, match?: TrainingMatch) => {
      if (!issues.some(i => i.code === code && i.playerId === playerId && i.match === match)) {
        issues.push({ code, severity, playerId, match });
      }
    };
    const byId = new Map(roster.map(p => [p.playerId, p]));
    const invalid = new Set<TrainingAssignment>();
    const knownType = summary.trainingTypeKnown !== false && summary.trainingTypeCode >= 2 && summary.trainingTypeCode <= 12;
    if (!knownType) add('unknownType', 'warning');
    if (!plan.weekStart || plan.weekStart !== (summary.weekStart ?? '') || plan.teamId !== summary.teamId) add('unknownWeek', 'error');

    for (const match of ['league', 'friendly'] as TrainingMatch[]) {
      const assignments = plan.assignments.filter(a => a.match === match);
      if (!assignments.length) continue;
      const date = Date.parse(plan.matchDates[match]);
      const start = Date.parse(plan.weekStart);
      const through = Date.parse(summary.weekEnd ?? '');
      const nextTraining = summary.nextTrainingAt ? Date.parse(summary.nextTrainingAt) : start + 7 * 24 * 60 * 60 * 1000;
      let dateIssue = '';
      if (!Number.isFinite(date) || !Number.isFinite(start) || !Number.isFinite(through) || !Number.isFinite(nextTraining)) dateIssue = 'unknownDate';
      else if (date <= through) dateIssue = 'pastMatch';
      else if (date < start || date + 90 * 60 * 1000 > nextTraining) dateIssue = 'outsideWeek';
      if (dateIssue) {
        add(dateIssue, 'error', undefined, match);
        assignments.forEach(a => invalid.add(a));
      }

      assignments.forEach(a => {
        const player = byId.get(a.playerId);
        if (!player) { add('missingPlayer', 'error', a.playerId, match); invalid.add(a); }
        if (!TRAINING_SLOTS.includes(a.slot) || !Number.isInteger(a.startMinute) || !Number.isInteger(a.minutes)
          || a.startMinute < 0 || a.minutes < 1 || a.startMinute + a.minutes > 90) {
          add('invalidMinutes', 'error', a.playerId, match); invalid.add(a);
        }
        if (player?.injuryLevel && player.injuryLevel > 0) { add('injured', 'error', a.playerId, match); invalid.add(a); }
        // The optimizer's current eligibility contract excludes suspended players
        // for every fixture. Do not promise a friendly lineup it cannot evaluate.
        if (player?.isSuspended) { add('suspended', 'error', a.playerId, match); invalid.add(a); }
        else if (player?.canOptimize === false && !(player.injuryLevel > 0)) { add('optimizerUnavailable', 'error', a.playerId, match); invalid.add(a); }
        if (player && (player.injuryStatusKnown !== true || player.suspensionStatusKnown !== true)) add('unknownAvailability', 'warning', a.playerId, match);
        if (knownType && trainingFactor(summary.trainingTypeCode, a.slot) === null) add('unknownEffect', 'warning', a.playerId, match);
      });
      for (let i = 0; i < assignments.length; i++) {
        for (let j = i + 1; j < assignments.length; j++) {
          const a = assignments[i], b = assignments[j];
          const overlaps = a.startMinute < b.startMinute + b.minutes && b.startMinute < a.startMinute + a.minutes;
          if (overlaps && (a.playerId === b.playerId || a.slot === b.slot)) {
            add(a.playerId === b.playerId ? 'playerOverlap' : 'slotOverlap', 'error', a.playerId, match);
            invalid.add(a); invalid.add(b);
          }
        }
      }
      for (const minute of new Set(assignments.map(a => a.startMinute))) {
        const active = assignments.filter(a => a.startMinute <= minute && minute < a.startMinute + a.minutes);
        if (!FORMATIONS.some(formation => active.every(a => formation.includes(a.slot))) || active.length > 11) {
          add('illegalFormation', 'error', undefined, match);
          active.forEach(a => invalid.add(a));
        }
      }
    }

    const players = plan.selectedPlayerIds.map(playerId => {
      if (!byId.has(playerId)) add('missingPlayer', 'error', playerId);
      const entry = summary.players.find(p => p.playerId === playerId);
      const known = knownType && this.observedMinutesKnown(summary, entry);
      let full = known ? entry!.observedFullMinutes ?? entry!.fullTrainingMinutes ?? 0 : 0;
      let half = known ? entry!.observedHalfMinutes ?? entry!.halfTrainingMinutes ?? 0 : 0;
      const recorded = known ? bestTraining90(full, half) : null;
      let hasUnknownEffect = !!entry && ((entry.smallEffectMinutes ?? 0) > 0 || (entry.verySmallEffectMinutes ?? 0) > 0);
      for (const assignment of plan.assignments.filter(a => a.playerId === playerId && !invalid.has(a))) {
        const factor = trainingFactor(summary.trainingTypeCode, assignment.slot);
        if (factor === 1) full += assignment.minutes;
        else if (factor === 0.5) half += assignment.minutes;
        else if (factor === null) hasUnknownEffect = true;
      }
      const projected = known ? bestTraining90(full, half) : null;
      return { playerId, recorded, projected, missing: projected === null ? null : Math.max(0, 90 - projected), hasUnknownEffect };
    });
    return { players, issues, ready: players.length > 0 && players.every(p => p.missing === 0)
      && !issues.some(i => i.severity === 'error' || i.code === 'unknownAvailability') };
  }

  private observedMinutesKnown(summary: TrainingSummary, entry?: TrainingPlayerEntry): boolean {
    if (!entry || !Number.isFinite(entry.observedFullMinutes ?? entry.fullTrainingMinutes)
      || !Number.isFinite(entry.observedHalfMinutes ?? entry.halfTrainingMinutes)) return false;
    // isEstimate is always true in the training API. Inspect the actual missing-data
    // conditions instead; speed/skill warnings do not make observed minutes unknown.
    const uncertain = /lineup data unavailable|exact played minutes are unknown|timeline.*ambiguous|could not be reconstructed|playedminutes|no official minutes|no substitution timeline|no position.change timeline|no per-player position-minute|details were unavailable|unsupported or ambiguous/i;
    return ![...(entry.warnings ?? []), ...(summary.warnings ?? [])].some(warning => uncertain.test(warning));
  }
}
