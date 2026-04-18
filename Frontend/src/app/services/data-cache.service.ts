import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { Team } from '../models/team.model';
import { Lineup, OptimizerResponse } from '../models/lineup.model';
import { Player } from '../models/player.model';

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

export interface OptimizerUiState {
  selectedMyFormation: string;
  selectedMyTactic: string;
  selectedOpponentFormation: string;
  selectedOpponentTactic: string;
  coachType: string;
  assistantManagerLevel: number;
  teamAttitude: string;
  preferredTactic: string;
  selectedAlternative: number;
  playerSortColumn: string;
  playerSortDirection: 'asc' | 'desc';
}

const DEFAULT_OPTIMIZER_UI: OptimizerUiState = {
  selectedMyFormation: 'Auto',
  selectedMyTactic: 'Auto',
  selectedOpponentFormation: 'Auto',
  selectedOpponentTactic: 'Auto',
  coachType: 'Neutral',
  assistantManagerLevel: 0,
  teamAttitude: 'Normal',
  preferredTactic: 'Auto',
  selectedAlternative: 0,
  playerSortColumn: 'form',
  playerSortDirection: 'desc'
};

@Injectable({ providedIn: 'root' })
export class DataCacheService {
  readonly auth$ = new BehaviorSubject<AuthInfo>({ authorized: false });
  readonly ownTeam$ = new BehaviorSubject<Team | null>(null);
  readonly nextOpponent$ = new BehaviorSubject<NextOpponent | null>(null);
  readonly opponentTeam$ = new BehaviorSubject<Team | null>(null);

  readonly opponentPlayers$ = new BehaviorSubject<Player[] | null>(null);
  readonly myTeamStats$ = new BehaviorSubject<any>(null);
  readonly opponentTeamStats$ = new BehaviorSubject<any>(null);
  readonly formationExperience$ = new BehaviorSubject<{ [k: string]: number } | null>(null);
  readonly optimizerResult$ = new BehaviorSubject<OptimizerResponse | null>(null);
  readonly opponentOptimalLineup$ = new BehaviorSubject<Lineup | null>(null);
  readonly optimizerUi$ = new BehaviorSubject<OptimizerUiState>({ ...DEFAULT_OPTIMIZER_UI });
}
