import { NO_ERRORS_SCHEMA } from '@angular/core';
import { TestBed, fakeAsync, flushMicrotasks } from '@angular/core/testing';
import { RouterTestingModule } from '@angular/router/testing';
import { TranslateModule } from '@ngx-translate/core';
import { of, Subject } from 'rxjs';
import { AppComponent } from './app.component';
import { DataCacheService } from './services/data-cache.service';
import { HattrickApiService } from './services/hattrick-api.service';
import { LoadStatusService } from './services/load-status.service';
import { TranslationService } from './services/translation.service';
import { PlayerHistoryService } from './services/player-history.service';

describe('AppComponent', () => {
  let api: any;
  beforeEach(() => {
    api = {
      sessionChanged$: new Subject<void>(),
      getCurrentOAuth: jasmine.createSpy().and.returnValue(of({ authorized: false })),
      getMyTeams: jasmine.createSpy().and.returnValue(of({ currentTeamId: 1, teams: [] })),
      getTeam: jasmine.createSpy().and.callFake((teamId: number) => of({ teamId, teamName: 'Team', players: [] })),
      getNextOpponent: jasmine.createSpy().and.returnValue(of({ matchId: 10, opponentTeamId: 2, opponentTeamName: 'Opponent' }))
    };
  });
  beforeEach(() => TestBed.configureTestingModule({
    imports: [RouterTestingModule, TranslateModule.forRoot()],
    declarations: [AppComponent],
    schemas: [NO_ERRORS_SCHEMA],
    providers: [
      { provide: PlayerHistoryService, useValue: { sync: () => of([]) } },
      { provide: TranslationService, useValue: { switchLanguage: jasmine.createSpy('switchLanguage'), getCurrentLanguage: () => 'en' } },
      { provide: HattrickApiService, useFactory: () => api },
      LoadStatusService,
      DataCacheService
    ]
  }));

  it('creates the app with its required service providers', () => {
    const fixture = TestBed.createComponent(AppComponent);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('loads the workspace immediately after OAuth without a page reload', fakeAsync(() => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    flushMicrotasks();
    expect(api.getTeam).not.toHaveBeenCalled();
    api.getCurrentOAuth.and.returnValue(of({ authorized: true, ownTeamId: 1 }));
    api.sessionChanged$.next();
    flushMicrotasks();
    const cache = TestBed.inject(DataCacheService);
    expect(cache.ownTeam$.value?.teamId).toBe(1);
    expect(cache.opponentTeam$.value?.teamId).toBe(2);
    expect(fixture.componentInstance.showAuthBanner).toBeFalse();
  }));

  it('queues an authorization change while the initial status check is still in flight', fakeAsync(() => {
    const initial = new Subject<any>();
    api.getCurrentOAuth.and.returnValues(initial, of({ authorized: true, ownTeamId: 1 }));
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    api.sessionChanged$.next();
    initial.next({ authorized: false });
    flushMicrotasks();
    expect(TestBed.inject(DataCacheService).ownTeam$.value?.teamId).toBe(1);
    expect(fixture.componentInstance.syncing).toBeFalse();
  }));

  it('does not restore private data from an in-flight response after logout', fakeAsync(() => {
    const teamResponse = new Subject<any>();
    api.getCurrentOAuth.and.returnValue(of({ authorized: true, ownTeamId: 1 }));
    api.getTeam.and.returnValue(teamResponse);
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    flushMicrotasks();
    api.getCurrentOAuth.and.returnValue(of({ authorized: false }));
    api.sessionChanged$.next();
    teamResponse.next({ teamId: 1, teamName: 'Old team', players: [] });
    flushMicrotasks();
    const cache = TestBed.inject(DataCacheService);
    expect(cache.ownTeam$.value).toBeNull();
    expect(cache.auth$.value.authorized).toBeFalse();
    expect(api.getNextOpponent).not.toHaveBeenCalled();
  }));
});
