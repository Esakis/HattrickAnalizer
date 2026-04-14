import { Component, OnInit } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { OptimizerRequest, OptimizerResponse } from '../../models/lineup.model';
import { TranslateService } from '@ngx-translate/core';
import { DataCacheService } from '../../services/data-cache.service';
import { Player } from '../../models/player.model';

@Component({
  selector: 'app-lineup-optimizer',
  templateUrl: './lineup-optimizer.component.html',
  styleUrls: ['./lineup-optimizer.component.scss']
})
export class LineupOptimizerComponent implements OnInit {
  myTeamId: number = 0;
  opponentTeamId: number = 0;
  preferredTactic: string = 'Normal';

  result: OptimizerResponse | null = null;
  loading: boolean = false;
  error: string | null = null;

  myTeamPlayers: Player[] = [];
  opponentTeamPlayers: Player[] = [];
  loadingMyTeam: boolean = false;
  loadingOpponentTeam: boolean = false;
  lastLoadedMyTeamId: number | null = null;
  lastLoadedOpponentId: number | null = null;
  
  myTeamName: string = '';
  opponentTeamName: string = '';

  tactics: { value: string; label: string }[] = [];

  constructor(
    private hattrickApi: HattrickApiService,
    private translate: TranslateService,
    private cache: DataCacheService
  ) {}

  ngOnInit(): void {
    this.initializeTranslations();
    this.translate.onLangChange.subscribe(() => this.initializeTranslations());
    this.translate.onTranslationChange.subscribe(() => this.initializeTranslations());

    this.cache.auth$.subscribe(auth => {
      if (auth.authorized && auth.ownTeamId && !this.myTeamId) {
        this.myTeamId = auth.ownTeamId;
        this.loadMyTeam();
      }
    });
    this.cache.nextOpponent$.subscribe(opp => {
      if (opp?.opponentTeamId && !this.opponentTeamId) {
        this.opponentTeamId = opp.opponentTeamId;
        this.loadOpponentTeam();
      }
    });

    const cachedTeam = this.cache.ownTeam$.value;
    if (cachedTeam?.players?.length) {
      this.myTeamPlayers = cachedTeam.players;
    }
  }

  private initializeTranslations(): void {
    this.tactics = [
      { value: 'Normal', label: this.translate.instant('optimizer.tactics.normal') },
      { value: 'Offensive', label: this.translate.instant('optimizer.tactics.offensive') },
      { value: 'Defensive', label: this.translate.instant('optimizer.tactics.defensive') },
      { value: 'Counter', label: this.translate.instant('optimizer.tactics.counter') },
      { value: 'AttackMiddle', label: this.translate.instant('optimizer.tactics.attackMiddle') },
      { value: 'AttackWings', label: this.translate.instant('optimizer.tactics.attackWings') }
    ];
  }

  optimizeLineup(): void {
    if (!this.myTeamId || !this.opponentTeamId) {
      this.error = this.translate.instant('optimizer.enterBothTeamIds');
      return;
    }

    this.loading = true;
    this.error = null;

    const request: OptimizerRequest = {
      myTeamId: this.myTeamId,
      opponentTeamId: this.opponentTeamId,
      preferredTactic: this.preferredTactic,
      focusAreas: []
    };

    this.hattrickApi.optimizeLineup(request).subscribe({
      next: (response) => {
        this.result = response;
        this.loading = false;
      },
      error: (err) => {
        this.error = this.translate.instant('optimizer.errorOptimizing') + err.message;
        this.loading = false;
      }
    });
  }

  getPositionKeys(): string[] {
    if (!this.result?.optimalLineup?.positions) return [];
    return Object.keys(this.result.optimalLineup.positions);
  }

  getPositionLabel(position: string): string {
    const labels: { [key: string]: string } = {
      'GK': this.translate.instant('optimizer.positions.GK'),
      'RWB': this.translate.instant('optimizer.positions.RWB'),
      'RCD': this.translate.instant('optimizer.positions.RCD'),
      'CD': this.translate.instant('optimizer.positions.CD'),
      'LCD': this.translate.instant('optimizer.positions.LCD'),
      'LWB': this.translate.instant('optimizer.positions.LWB'),
      'RW': this.translate.instant('optimizer.positions.RW'),
      'RIM': this.translate.instant('optimizer.positions.RIM'),
      'IM': this.translate.instant('optimizer.positions.IM'),
      'LIM': this.translate.instant('optimizer.positions.LIM'),
      'LW': this.translate.instant('optimizer.positions.LW'),
      'RFW': this.translate.instant('optimizer.positions.RFW'),
      'FW': this.translate.instant('optimizer.positions.FW'),
      'LFW': this.translate.instant('optimizer.positions.LFW'),
      // Dodatkowe pozycje z formacji
      'CDO': this.translate.instant('optimizer.positions.CDO') || 'CDO',
      'CDTW': this.translate.instant('optimizer.positions.CDTW') || 'CDTW',
      'WBD': this.translate.instant('optimizer.positions.WBD') || 'WBD',
      'WBN': this.translate.instant('optimizer.positions.WBN') || 'WBN',
      'WBO': this.translate.instant('optimizer.positions.WBO') || 'WBO',
      'WBTM': this.translate.instant('optimizer.positions.WBTM') || 'WBTM',
      'WO': this.translate.instant('optimizer.positions.WO') || 'WO',
      'WD': this.translate.instant('optimizer.positions.WD') || 'WD',
      'WTM': this.translate.instant('optimizer.positions.WTM') || 'WTM',
      'FTW': this.translate.instant('optimizer.positions.FTW') || 'FTW',
      'DF': this.translate.instant('optimizer.positions.DF') || 'DF'
    };
    return labels[position] || position;
  }

  getPositionDescription(position: string): string {
    const descriptions: { [key: string]: string } = {
      'GK': 'Bramkarz',
      'RWB': 'Prawy Boczny Obroca',
      'RCD': 'Prawy rodkowy Obroca',
      'CD': 'rodkowy Obroca',
      'LCD': 'Lewy rodkowy Obroca',
      'LWB': 'Lewy Boczny Obroca',
      'RW': 'Prawy Pomocnik',
      'RIM': 'Prawy Wewntrzny Pomocnik',
      'IM': 'rodkowy Pomocnik',
      'LIM': 'Lewy Wewntrzny Pomocnik',
      'LW': 'Lewy Pomocnik',
      'RFW': 'Prawy Napastnik',
      'FW': 'rodkowy Napastnik',
      'LFW': 'Lewy Napastnik',
      'CDO': 'rodkowy Obroca Ofensywny',
      'CDTW': 'rodkowy Obroca do Skrzyda',
      'WBD': 'Prawy Boczny Obroca Defensywny',
      'WBN': 'Prawy Boczny Obroca Normalny',
      'WBO': 'Prawy Boczny Obroca Ofensywny',
      'WBTM': 'Prawy Boczny Obroca do rodka',
      'WO': 'Prawy Skrzydowy Ofensywny',
      'WD': 'Prawy Skrzydowy Defensywny',
      'WTM': 'Prawy Skrzydowy do rodka',
      'FTW': 'Napastnik do Skrzyda',
      'DF': 'Napastnik Defensywny'
    };
    return descriptions[position] || position;
  }

  loadMyTeam(): void {
    if (!this.myTeamId) return;
    
    // Nie pobieraj ponownie jeśli dane są już załadowane dla tego samego ID
    if (this.myTeamPlayers.length > 0 && this.lastLoadedMyTeamId === this.myTeamId) {
      return;
    }
    
    this.loadingMyTeam = true;
    this.hattrickApi.getTeam(this.myTeamId).subscribe({
      next: (team) => {
        this.myTeamPlayers = team.players;
        this.myTeamName = team.teamName;
        this.lastLoadedMyTeamId = this.myTeamId;
        this.loadingMyTeam = false;
      },
      error: () => {
        this.loadingMyTeam = false;
      }
    });
  }

  loadOpponentTeam(): void {
    if (!this.opponentTeamId) return;
    
    // Nie pobieraj ponownie jeśli dane są już załadowane dla tego samego ID
    if (this.opponentTeamPlayers.length > 0 && this.lastLoadedOpponentId === this.opponentTeamId) {
      return;
    }
    
    this.loadingOpponentTeam = true;
    this.hattrickApi.getTeam(this.opponentTeamId).subscribe({
      next: (team) => {
        this.opponentTeamPlayers = team.players;
        this.opponentTeamName = team.teamName;
        this.lastLoadedOpponentId = this.opponentTeamId;
        this.loadingOpponentTeam = false;
      },
      error: () => {
        this.loadingOpponentTeam = false;
      }
    });
  }

  onMyTeamIdChange(): void {
    if (this.myTeamId) {
      // Wyczyść dane jeśli ID się zmieniło
      if (this.lastLoadedMyTeamId !== this.myTeamId) {
        this.myTeamPlayers = [];
        this.myTeamName = '';
      }
      this.loadMyTeam();
    }
  }

  onOpponentTeamIdChange(): void {
    if (this.opponentTeamId) {
      // Wyczyść dane jeśli ID się zmieniło
      if (this.lastLoadedOpponentId !== this.opponentTeamId) {
        this.opponentTeamPlayers = [];
        this.opponentTeamName = '';
      }
      this.loadOpponentTeam();
    }
  }

  get myTeamAverageAge(): string {
    if (this.myTeamPlayers.length === 0) return '0.0';
    const sum = this.myTeamPlayers.reduce((acc, p) => acc + p.age, 0);
    return (sum / this.myTeamPlayers.length).toFixed(1);
  }

  get myTeamAverageForm(): string {
    if (this.myTeamPlayers.length === 0) return '0.0';
    const sum = this.myTeamPlayers.reduce((acc, p) => acc + p.form, 0);
    return (sum / this.myTeamPlayers.length).toFixed(1);
  }

  get opponentTeamAverageAge(): string {
    if (this.opponentTeamPlayers.length === 0) return '0.0';
    const sum = this.opponentTeamPlayers.reduce((acc, p) => acc + p.age, 0);
    return (sum / this.opponentTeamPlayers.length).toFixed(1);
  }

  get opponentTeamAverageForm(): string {
    if (this.opponentTeamPlayers.length === 0) return '0.0';
    const sum = this.opponentTeamPlayers.reduce((acc, p) => acc + p.form, 0);
    return (sum / this.opponentTeamPlayers.length).toFixed(1);
  }
}
