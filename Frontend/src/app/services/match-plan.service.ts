import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { OptimizerResponse } from '../models/lineup.model';
import { Player } from '../models/player.model';
import { Team } from '../models/team.model';

export interface SavedMatchPlan {
  teamId: number;
  matchId: number;
  opponentTeamId: number;
  matchDate?: string;
  savedAt: string;
  label: string;
  result: OptimizerResponse;
  selectedAlternative: number;
  rosterFingerprint: string;
  teamContextKey?: string;
  status: 'draft' | 'ready' | 'sent';
  orders?: any;
  referenceResult?: OptimizerResponse;
}

export function teamContextFingerprint(team: Team | null): string {
  return JSON.stringify([team?.teamSpiritLevel ?? null, team?.confidenceLevel ?? null]);
}

export function rosterFingerprint(players: Player[]): string {
  return JSON.stringify([...players].sort((a, b) => a.playerId - b.playerId).map(p => [
    p.playerId, p.skills, p.form, p.stamina, p.experience, p.loyalty, p.injuryLevel,
    p.injuryStatusKnown, p.isSuspended, p.suspensionStatusKnown, p.skillsAvailable
  ]));
}

@Injectable({ providedIn: 'root' })
export class MatchPlanService {
  readonly changed$ = new BehaviorSubject(0);
  storageError = false;
  private memory = new Map<number, SavedMatchPlan[]>();

  getPlans(teamId: number): SavedMatchPlan[] {
    if (this.memory.has(teamId)) return JSON.parse(JSON.stringify(this.memory.get(teamId)));
    try {
      const raw = localStorage.getItem(`hattrick.matchPlans.v1.${teamId}`);
      const plans = raw ? JSON.parse(raw) : this.memory.get(teamId) ?? [];
      return Array.isArray(plans) ? plans.filter(p => p.teamId === teamId && p.result?.inputSnapshot) : [];
    } catch { return this.memory.get(teamId) ?? []; }
  }

  getPlan(teamId: number, matchId: number, opponentTeamId: number): SavedMatchPlan | undefined {
    return this.getPlans(teamId).find(p => p.matchId === matchId && p.opponentTeamId === opponentTeamId);
  }

  save(plan: SavedMatchPlan): boolean {
    const plans = [JSON.parse(JSON.stringify(plan)), ...this.getPlans(plan.teamId)
      .filter(p => p.matchId !== plan.matchId || p.opponentTeamId !== plan.opponentTeamId)].slice(0, 16);
    this.memory.set(plan.teamId, plans);
    try {
      localStorage.setItem(`hattrick.matchPlans.v1.${plan.teamId}`, JSON.stringify(plans));
      this.storageError = false;
    } catch { this.storageError = true; }
    this.changed$.next(this.changed$.value + 1);
    return !this.storageError;
  }
}
