import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Player } from '../models/player.model';

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
}

@Injectable({ providedIn: 'root' })
export class PlayerHistoryService {
  private readonly apiUrl = 'http://localhost:5000/api/playerhistory';

  constructor(private http: HttpClient) {}

  getPlayerHistory(playerId: number): Observable<PlayerSkillHistory[]> {
    return this.http.get<PlayerSkillHistory[]>(`${this.apiUrl}/${playerId}`);
  }

  checkAndSave(players: Player[], teamId: number): Observable<PlayerChangeResult[]> {
    return this.http.post<PlayerChangeResult[]>(`${this.apiUrl}/check-and-save`, { players, teamId });
  }
}
