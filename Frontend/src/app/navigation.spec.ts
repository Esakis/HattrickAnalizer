import { NgZone } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TranslateLoader } from '@ngx-translate/core';
import { of, Subject } from 'rxjs';
import { AppModule } from './app.module';
import { AppComponent } from './app.component';
import { HattrickApiService } from './services/hattrick-api.service';
import { PlayerHistoryService } from './services/player-history.service';
import { MatchReviewService } from './services/match-review.service';

// Render the real routes, modules and templates in headless Chrome. All service
// responses below belong only to this test; no live account or match orders are used.
describe('Workspace navigation', () => {
  beforeEach(async () => {
    const next = { matchId: 10, opponentTeamId: 2, opponentTeamName: 'Opponent', matchDate: '2099-01-01T12:00:00Z', matchType: '1' };
    await TestBed.configureTestingModule({ imports: [AppModule] })
      .overrideProvider(TranslateLoader, { useValue: { getTranslation: () => of({}) } })
      .overrideProvider(HattrickApiService, { useValue: {
        sessionChanged$: new Subject<void>(),
        getCurrentOAuth: () => of({ authorized: true, mockMode: false, ownTeamId: 1, ownTeamName: 'Test team' }),
        getMyTeams: () => of({ currentTeamId: 1, teams: [{ teamId: 1, teamName: 'Test team' }] }),
        getTeam: (teamId: number) => of({ teamId, teamName: 'Test team', players: [] }),
        getPlayers: () => of([]), getTeamMatchStats: () => of({}), getFormationExperience: () => of({}),
        getNextOpponent: () => of(next), getUpcomingMatches: () => of([next]),
        getOpponentScout: () => of({ teamId: 2, matchesAnalyzed: 0, likelyStarters: [], matches: [] }),
        getLeagueSimulation: () => of({ teams: [], leagueName: 'Test league', remainingMatches: 0 }),
        getTrainingSummary: () => of({ teamId: 1, trainingTypeCode: 3, trainingTypeName: 'Playmaking', players: [], warnings: [] })
      } })
      .overrideProvider(PlayerHistoryService, { useValue: {
        changed$: new Subject<void>(), sync: () => of([]), recent: () => of([]), getPlayerHistory: () => of([])
      } })
      .overrideProvider(MatchReviewService, { useValue: {
        report: () => of({ matches: [], modelVersions: [], exclusionReasons: [], trainingSectorSampleCounts: {}, heldOutSectorSampleCounts: {} }),
        snapshots: () => of([])
      } }).compileComponents();
  });

  for (const [path, selector] of [
    ['/', 'app-week-dashboard'], ['/optimizer', 'app-lineup-optimizer'], ['/players', 'app-players'],
    ['/scout', 'app-scout-view'], ['/league', 'app-league-table'], ['/training', 'app-training-view'],
    ['/review', 'app-match-review'], ['/oauth-setup', 'app-oauth-setup']
  ]) {
    it(`renders ${path} with an authorized session`, async () => {
      const fixture = TestBed.createComponent(AppComponent);
      fixture.detectChanges();
      await fixture.whenStable();
      const router = TestBed.inject(Router);
      await TestBed.inject(NgZone).run(() => router.navigateByUrl(path));
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(router.url).toBe(path);
      expect(fixture.nativeElement.querySelector(selector)).not.toBeNull();
      expect(fixture.nativeElement.querySelector('.mock-banner')).toBeNull();
    });
  }
});
