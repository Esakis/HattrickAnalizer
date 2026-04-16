import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Team } from '../models/team.model';
import { Player } from '../models/player.model';
import { OptimizerRequest, OptimizerResponse } from '../models/lineup.model';
import { AuthInfo, NextOpponent } from './data-cache.service';

@Injectable({
  providedIn: 'root'
})
export class HattrickApiService {
  private apiUrl = 'http://localhost:5000/api';

  constructor(private http: HttpClient) { }

  getTeam(teamId: number): Observable<Team> {
    return this.http.get<Team>(`${this.apiUrl}/team/${teamId}`);
  }

  getPlayers(teamId: number): Observable<Player[]> {
    return this.http.get<Player[]>(`${this.apiUrl}/team/${teamId}/players`);
  }

  getTeamMatchStats(teamId: number): Observable<any> {
    return this.http.get<any>(`${this.apiUrl}/team/${teamId}/match-stats`);
  }

  getFormationExperience(teamId: number): Observable<{ [formation: string]: number }> {
    return this.http.get<{ [formation: string]: number }>(`${this.apiUrl}/team/${teamId}/formation-experience`);
  }

  getNextOpponent(): Observable<NextOpponent> {
    return this.http.get<NextOpponent>(`${this.apiUrl}/team/next-opponent`);
  }

  optimizeLineup(request: OptimizerRequest): Observable<OptimizerResponse> {
    return this.http.post<OptimizerResponse>(`${this.apiUrl}/optimizer/optimize`, request);
  }

  startOAuthFlow(): Observable<any> {
    return this.http.get<any>(`${this.apiUrl}/oauth/start`);
  }

  completeOAuthFlow(sessionId: string, verifier: string): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/oauth/complete`, { sessionId, verifier });
  }

  getOAuthStatus(sessionId: string): Observable<any> {
    return this.http.get<any>(`${this.apiUrl}/oauth/status/${sessionId}`);
  }

  getCurrentOAuth(): Observable<AuthInfo> {
    return this.http.get<AuthInfo>(`${this.apiUrl}/oauth/current`);
  }

  logoutOAuth(): Observable<any> {
    return this.http.post<any>(`${this.apiUrl}/oauth/logout`, {});
  }
}
