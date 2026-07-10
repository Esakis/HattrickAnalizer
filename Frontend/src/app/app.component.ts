import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { TranslationService } from './services/translation.service';
import { HattrickApiService, AccountTeam } from './services/hattrick-api.service';
import { LoadStatusService } from './services/load-status.service';
import { DataCacheService } from './services/data-cache.service';
import { TranslateService } from '@ngx-translate/core';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss']
})
export class AppComponent implements OnInit {
  title = 'Frontend';
  showAuthBanner = false;
  showMockBanner = false;

  // Drużyny konta (np. męska i kobieca) — przełącznik w nagłówku.
  accountTeams: AccountTeam[] = [];
  currentTeamId: number = 0;
  switchingTeam = false;

  constructor(
    public translationService: TranslationService,
    private translate: TranslateService,
    private api: HattrickApiService,
    private loadStatus: LoadStatusService,
    private cache: DataCacheService,
    private router: Router
  ) {
    this.loadStatus.register('auth', 'loadStatus.auth');
    this.loadStatus.register('ownTeam', 'loadStatus.ownTeam');
    this.loadStatus.register('nextOpponent', 'loadStatus.nextOpponent');
    this.loadStatus.register('opponentTeam', 'loadStatus.opponentTeam');
  }

  ngOnInit(): void {
    this.runStartupLoad();
  }

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
    this.api.getMyTeams().subscribe({
      next: (res) => {
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
    this.loadStatus.set('auth', 'loading');
    let auth;
    try {
      auth = await firstValueFrom(this.api.getCurrentOAuth());
    } catch (err: any) {
      this.loadStatus.set('auth', 'error', err?.message ?? 'network error');
      return;
    }

    this.cache.auth$.next(auth);
    this.showMockBanner = !!auth.mockMode;

    if (!auth.authorized) {
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
      this.cache.ownTeam$.next(team);
      this.loadStatus.set('ownTeam', 'success', `${team.teamName} · ${team.players.length} zaw.`);
    } catch (err: any) {
      this.loadStatus.set('ownTeam', 'error', err?.message ?? 'error');
    }

    this.loadStatus.set('nextOpponent', 'loading');
    let opponent;
    try {
      opponent = await firstValueFrom(this.api.getNextOpponent());
      this.cache.nextOpponent$.next(opponent);
      this.loadStatus.set('nextOpponent', 'success', opponent.opponentTeamName);
    } catch (err: any) {
      this.loadStatus.set('nextOpponent', 'error', err?.error?.error ?? err?.message ?? 'error');
      return;
    }

    this.loadStatus.set('opponentTeam', 'loading');
    try {
      const opponentTeam = await firstValueFrom(this.api.getTeam(opponent.opponentTeamId));
      this.cache.opponentTeam$.next(opponentTeam);
      this.loadStatus.set('opponentTeam', 'success', `${opponentTeam.teamName} · ${opponentTeam.players.length} zaw.`);
    } catch (err: any) {
      this.loadStatus.set('opponentTeam', 'error', err?.message ?? 'error');
    }
  }
}
