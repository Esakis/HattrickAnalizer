import { Component, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom, Subject, takeUntil } from 'rxjs';
import { TranslationService } from './services/translation.service';
import { HattrickApiService, AccountTeam } from './services/hattrick-api.service';
import { LoadStatusService } from './services/load-status.service';
import { DataCacheService } from './services/data-cache.service';
import { TranslateService } from '@ngx-translate/core';
import { PlayerHistoryService } from './services/player-history.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss']
})
export class AppComponent implements OnInit, OnDestroy {
  private readonly destroy$ = new Subject<void>();
  private reloadRequested = false;
  private sessionVersion = 0;
  private wasAuthorized = false;
  title = 'Frontend';
  showAuthBanner = false;
  showMockBanner = false;

  // Drużyny konta (np. męska i kobieca) — przełącznik w nagłówku.
  accountTeams: AccountTeam[] = [];
  currentTeamId: number = 0;
  switchingTeam = false;
  syncing = false;
  lastSyncedAt: string | null = null;

  constructor(
    public translationService: TranslationService,
    private translate: TranslateService,
    private api: HattrickApiService,
    private loadStatus: LoadStatusService,
    private cache: DataCacheService,
    private router: Router,
    private history: PlayerHistoryService
  ) {
    this.loadStatus.register('auth', 'loadStatus.auth');
    this.loadStatus.register('ownTeam', 'loadStatus.ownTeam');
    this.loadStatus.register('nextOpponent', 'loadStatus.nextOpponent');
    this.loadStatus.register('opponentTeam', 'loadStatus.opponentTeam');
    this.loadStatus.register('history', 'workspaceNav.history');
  }

  ngOnInit(): void {
    this.api.sessionChanged$.pipe(takeUntil(this.destroy$)).subscribe(() => {
      this.sessionVersion++;
      this.cache.clear();
      this.accountTeams = [];
      this.currentTeamId = 0;
      this.lastSyncedAt = null;
      this.loadStatus.reset();
      this.runStartupLoad();
    });
    this.cache.auth$.pipe(takeUntil(this.destroy$)).subscribe(auth => {
      if (this.wasAuthorized && !auth.authorized) {
        this.sessionVersion++;
        this.accountTeams = [];
        this.currentTeamId = 0;
        this.lastSyncedAt = null;
        this.loadStatus.reset();
      }
      this.wasAuthorized = auth.authorized;
      this.showAuthBanner = !auth.authorized;
      this.showMockBanner = !!auth.mockMode;
    });
    this.runStartupLoad();
  }

  ngOnDestroy(): void { this.sessionVersion++; this.destroy$.next(); this.destroy$.complete(); }

  switchLanguage(lang: string): void {
    this.translationService.switchLanguage(lang);
  }

  getCurrentLanguage(): string {
    return this.translationService.getCurrentLanguage();
  }

  goToOAuth(): void {
    this.router.navigateByUrl('/oauth-setup');
  }

  loadAccountTeams(): void {
    const version = this.sessionVersion;
    this.api.getMyTeams().pipe(takeUntil(this.destroy$)).subscribe({
      next: (res) => {
        if (version !== this.sessionVersion) return;
        this.accountTeams = res.teams;
        this.currentTeamId = res.currentTeamId;
      },
      error: (err) => console.error('Error loading account teams:', err)
    });
  }

  switchTeam(teamId: number): void {
    if (teamId === this.currentTeamId || this.switchingTeam) return;
    this.switchingTeam = true;
    this.api.selectTeam(teamId).subscribe({
      next: () => {
        // Cały stan aplikacji (cache, komponenty) jest budowany wokół jednej drużyny —
        // pełne przeładowanie to najprostszy sposób na czysty kontekst nowej drużyny.
        window.location.reload();
      },
      error: (err) => {
        console.error('Error switching team:', err);
        this.switchingTeam = false;
      }
    });
  }

  async runStartupLoad(): Promise<void> {
    if (this.syncing) { this.reloadRequested = true; return; }
    this.syncing = true;
    try {
      do {
        this.reloadRequested = false;
        await this.loadStartup();
      } while (this.reloadRequested);
    }
    finally { this.syncing = false; }
  }

  private async loadStartup(): Promise<void> {
    const version = this.sessionVersion;
    this.loadStatus.set('auth', 'loading');
    let auth;
    try {
      auth = await firstValueFrom(this.api.getCurrentOAuth());
      if (version !== this.sessionVersion) return;
    } catch (err: any) {
      if (version !== this.sessionVersion) return;
      this.loadStatus.set('auth', 'error', err?.message ?? 'network error');
      return;
    }

    this.cache.auth$.next(auth);
    this.showMockBanner = !!auth.mockMode;

    if (!auth.authorized) {
      this.cache.clear();
      this.accountTeams = [];
      this.currentTeamId = 0;
      this.lastSyncedAt = null;
      this.loadStatus.set('auth', 'error', this.translate.instant('loadStatus.notAuthorized'));
      this.showAuthBanner = true;
      return;
    }

    this.loadStatus.set('auth', 'success', auth.ownTeamName);
    this.showAuthBanner = false;
    this.loadAccountTeams();

    const teamId = auth.ownTeamId!;

    this.loadStatus.set('ownTeam', 'loading');
    try {
      const team = await firstValueFrom(this.api.getTeam(teamId));
      if (version !== this.sessionVersion) return;
      this.cache.ownTeam$.next(team);
      this.lastSyncedAt = new Date().toISOString();
      this.loadStatus.set('ownTeam', 'success', `${team.teamName} · ${team.players.length} zaw.`);
      this.loadStatus.set('history', 'loading');
      this.history.sync().pipe(takeUntil(this.destroy$)).subscribe({
        next: () => { if (version === this.sessionVersion) this.loadStatus.set('history', 'success'); },
        error: err => { if (version === this.sessionVersion) this.loadStatus.set('history', 'error', err?.error?.error ?? err.message); }
      });
    } catch (err: any) {
      if (version !== this.sessionVersion) return;
      this.loadStatus.set('ownTeam', 'error', err?.message ?? 'error');
    }

    this.loadStatus.set('nextOpponent', 'loading');
    let opponent;
    try {
      opponent = await firstValueFrom(this.api.getNextOpponent());
      if (version !== this.sessionVersion) return;
      this.cache.nextOpponent$.next(opponent);
      this.loadStatus.set('nextOpponent', 'success', opponent.opponentTeamName);
    } catch (err: any) {
      if (version !== this.sessionVersion) return;
      this.cache.nextOpponent$.next(null);
      this.cache.opponentTeam$.next(null);
      this.loadStatus.set('nextOpponent', 'error', err?.error?.error ?? err?.message ?? 'error');
      return;
    }

    this.loadStatus.set('opponentTeam', 'loading');
    try {
      const opponentTeam = await firstValueFrom(this.api.getTeam(opponent.opponentTeamId));
      if (version !== this.sessionVersion) return;
      this.cache.opponentTeam$.next(opponentTeam);
      this.loadStatus.set('opponentTeam', 'success', `${opponentTeam.teamName} · ${opponentTeam.players.length} zaw.`);
    } catch (err: any) {
      if (version !== this.sessionVersion) return;
      this.cache.opponentTeam$.next(null);
      this.loadStatus.set('opponentTeam', 'error', err?.message ?? 'error');
    }
  }
}
