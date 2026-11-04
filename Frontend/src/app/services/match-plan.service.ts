import { Injectable, OnDestroy } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { OptimizerResponse } from '../models/lineup.model';
import { Player } from '../models/player.model';
import { TrainingHandoff } from './training-orders';
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
  trainingPlan?: TrainingHandoff;
  referenceResult?: OptimizerResponse;
}

export function teamContextFingerprint(team: Team | null): string {
  return JSON.stringify([team?.teamSpiritLevel ?? null, team?.confidenceLevel ?? null]);
}

export function rosterFingerprint(players: Player[]): string {
  return JSON.stringify([...players].sort((a, b) => a.playerId - b.playerId).map(p => [
    p.playerId, p.skills, p.form, p.stamina, p.experience, p.loyalty, p.injuryLevel,
    p.injuryStatusKnown, p.isSuspended, p.suspensionStatusKnown, p.skillsAvailable, p.specialty, p.motherClubBonus
  ]));
}

@Injectable({ providedIn: 'root' })
export class MatchPlanService implements OnDestroy {
  readonly changed$ = new BehaviorSubject(0);
  storageError = false;
  // Only failed writes live here. Successful records are always read from storage.
  private pending = new Map<string, SavedMatchPlan>();
  private readonly onStorage = (event: StorageEvent) => {
    if (event.key === null || event.key.startsWith('hattrick.matchPlans.')) this.changed$.next(this.changed$.value + 1);
  };
  constructor() { window.addEventListener('storage', this.onStorage); }
  ngOnDestroy(): void { window.removeEventListener('storage', this.onStorage); }
  private key(plan: SavedMatchPlan): string {
    return `hattrick.matchPlans.v2.${plan.teamId}.${plan.matchId}.${plan.opponentTeamId}`;
  }
  getPlans(teamId: number): SavedMatchPlan[] {
    const plans = new Map<string, SavedMatchPlan>();
    const add = (plan: SavedMatchPlan) => {
      if (plan?.teamId === teamId && plan.result?.inputSnapshot) plans.set(this.key(plan), plan);
    };
    try {
      // Read legacy records without rewriting them: another open tab may still use v1.
      try {
        const legacy = JSON.parse(localStorage.getItem(`hattrick.matchPlans.v1.${teamId}`) ?? '[]');
        if (Array.isArray(legacy)) legacy.forEach(add);
      } catch { /* A damaged legacy list must not hide newer records. */ }
      const prefix = `hattrick.matchPlans.v2.${teamId}.`;
      for (let i = 0; i < localStorage.length; i++) {
        const key = localStorage.key(i);
        if (!key?.startsWith(prefix)) continue;
        try { add(JSON.parse(localStorage.getItem(key) ?? 'null')); } catch { /* Isolate damaged records. */ }
      }
    } catch { /* Storage unavailable: retain unsaved work for this session. */ }
    for (const plan of this.pending.values()) if (plan.teamId === teamId) add(plan);
    return JSON.parse(JSON.stringify([...plans.values()].sort((a,b) => Date.parse(b.savedAt) - Date.parse(a.savedAt))));
  }
  getPlan(teamId: number, matchId: number, opponentTeamId: number): SavedMatchPlan | undefined {
    return this.getPlans(teamId).find(p => p.matchId === matchId && p.opponentTeamId === opponentTeamId);
  }
  save(plan: SavedMatchPlan): boolean {
    const snapshot: SavedMatchPlan = JSON.parse(JSON.stringify(plan));
    const key = this.key(snapshot);
    this.pending.set(key, snapshot);
    try {
      // A single atomic key per match prevents concurrent writes to unrelated plans from losing data.
      for (const [pendingKey, pendingPlan] of this.pending) {
        localStorage.setItem(pendingKey, JSON.stringify(pendingPlan));
        this.pending.delete(pendingKey);
      }
    } catch { /* The failed record remains available until retry or session end. */ }
    this.storageError = this.pending.size > 0;
    this.changed$.next(this.changed$.value + 1);
    return !this.pending.has(key);
  }
}
