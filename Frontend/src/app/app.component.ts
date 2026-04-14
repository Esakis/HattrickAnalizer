import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { TranslationService } from './services/translation.service';
import { HattrickApiService } from './services/hattrick-api.service';
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

    if (!auth.authorized) {
      this.loadStatus.set('auth', 'error', this.translate.instant('loadStatus.notAuthorized'));
      this.showAuthBanner = true;
      return;
    }

    this.loadStatus.set('auth', 'success', auth.ownTeamName);
    this.showAuthBanner = false;

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
