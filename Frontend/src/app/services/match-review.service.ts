import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { CalibrationMatchContext, CalibrationReport, CalibrationSnapshot, CaptureResult } from '../models/match-review.model';

@Injectable({ providedIn: 'root' })
export class MatchReviewService {
  private readonly url = `${environment.apiUrl}/calibration`;
  constructor(private http: HttpClient) {}
  report(count = 10): Observable<CalibrationReport> {
    return this.http.get<CalibrationReport>(`${this.url}/own-matches`, { params: { count } });
  }
  snapshots(): Observable<CalibrationSnapshot[]> {
    return this.http.get<CalibrationSnapshot[]>(`${this.url}/snapshots`);
  }
  capture(context: CalibrationMatchContext): Observable<CaptureResult> {
    return this.http.post<CaptureResult>(`${this.url}/snapshots/capture`, context);
  }
}
