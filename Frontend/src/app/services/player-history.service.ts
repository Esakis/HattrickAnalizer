import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, Subject, tap } from 'rxjs';
import { environment } from '../../environments/environment';

export interface PlayerSkillHistory {
  id: number;
  playerId: number;
  teamId: number;
  recordedDate: string;
  // Umiejętności
  keeper: number;
  defending: number;
  playmaking: number;
  winger: number;
  passing: number;
  scoring: number;
  setPieces: number;
  // Podstawowe
  form: number;
  stamina: number;
  age: number;
  tsi: number;
  experience: number;
  loyalty: number;
  leadership: number;
  injuryLevel: number;
  injuryStatusKnown?: boolean;
  matchStatsAvailable?: boolean;
  // Statystyki meczowe
  totalMatches: number;
  goals: number;
  assists: number;
  yellowCards: number;
  redCards: number;
  averageRating: number;
  averageForm: number;
  minutesPlayed: number;
}

export interface PlayerChangeResult {
  playerId: number;
  playerName: string;
  changed: boolean;
  changedFields: string[];
  teamId: number;
  recordedAt: string;
  firstSnapshot: boolean;
  fields: { field: string; previous: number; current: number }[];
  warnings: string[];
}

@Injectable({ providedIn: 'root' })
export class PlayerHistoryService {
  readonly changed$ = new Subject<void>();
  private readonly apiUrl = `${environment.apiUrl}/playerhistory`;

  constructor(private http: HttpClient) {}

  getPlayerHistory(playerId: number): Observable<PlayerSkillHistory[]> {
    return this.http.get<PlayerSkillHistory[]>(`${this.apiUrl}/${playerId}`);
  }

  sync(): Observable<PlayerChangeResult[]> {
    return this.http.post<PlayerChangeResult[]>(`${this.apiUrl}/sync`, {}).pipe(tap(() => this.changed$.next()));
  }

  recent(count = 30): Observable<PlayerChangeResult[]> {
    return this.http.get<PlayerChangeResult[]>(`${this.apiUrl}/recent`, { params: { count } });
  }
}
