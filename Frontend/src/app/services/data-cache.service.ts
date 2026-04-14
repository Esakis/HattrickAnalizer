import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { Team } from '../models/team.model';

export interface NextOpponent {
  matchId: number;
  opponentTeamId: number;
  opponentTeamName: string;
  matchDate?: string;
  matchType?: string;
  isHomeMatch?: boolean;
}

export interface AuthInfo {
  authorized: boolean;
  ownTeamId?: number;
  ownTeamName?: string;
  authorizedAt?: string;
}

@Injectable({ providedIn: 'root' })
export class DataCacheService {
  readonly auth$ = new BehaviorSubject<AuthInfo>({ authorized: false });
  readonly ownTeam$ = new BehaviorSubject<Team | null>(null);
  readonly nextOpponent$ = new BehaviorSubject<NextOpponent | null>(null);
  readonly opponentTeam$ = new BehaviorSubject<Team | null>(null);
}
