import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Team } from '../models/team.model';
import { Player } from '../models/player.model';
import { OptimizerRequest, OptimizerResponse } from '../models/lineup.model';

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
}
