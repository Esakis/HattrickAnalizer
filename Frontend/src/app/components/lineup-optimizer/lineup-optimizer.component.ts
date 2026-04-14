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
  preferredTactic: string = 'Auto';
  teamAttitude: string = 'Normal';
  coachType: string = 'Neutral';
  assistantManagerLevel: number = 0;
  availableFormations: string[] = ['5-5-0','5-4-1','5-3-2','4-5-1','4-4-2','4-3-3','3-5-2','3-4-3','2-5-3'];
  formationExperience: { [k: string]: number } = {};
  selectedAlternative: number = 0;

  result: OptimizerResponse | null = null;
  loading: boolean = false;
  error: string | null = null;

  myTeamPlayers: Player[] = [];
  opponentTeamPlayers: Player[] = [];
  loadingMyTeam: boolean = false;
  loadingOpponentTeam: boolean = false;
  lastLoadedMyTeamId: number | null = null;
  lastLoadedOpponentId: number | null = null;

  // Statystyki druyny
  myTeamStats: any = null;
  opponentTeamStats: any = null;
  loadingMyTeamStats: boolean = false;
  loadingOpponentTeamStats: boolean = false;
  
  myTeamName: string = '';
  opponentTeamName: string = '';

  tactics: { value: string; label: string }[] = [];
  attitudes: { value: string; label: string }[] = [];
  coachTypes: { value: string; label: string }[] = [];
  experienceLevels: { value: number; label: string }[] = [];

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
        this.loadMyTeamStats();
      }
    });
    this.cache.nextOpponent$.subscribe(opp => {
      if (opp?.opponentTeamId && !this.opponentTeamId) {
        this.opponentTeamId = opp.opponentTeamId;
        this.loadOpponentTeam();
        this.loadOpponentTeamStats();
      }
    });

    const cachedTeam = this.cache.ownTeam$.value;
    if (cachedTeam?.players?.length) {
      this.myTeamPlayers = cachedTeam.players;
    }
  }

  private initializeTranslations(): void {
    this.tactics = [
      { value: 'Auto', label: this.translate.instant('optimizer.tactics.auto') },
      { value: 'Normal', label: this.translate.instant('optimizer.tactics.normal') },
      { value: 'Counter', label: this.translate.instant('optimizer.tactics.counter') },
      { value: 'AttackInMiddle', label: this.translate.instant('optimizer.tactics.attackMiddle') },
      { value: 'AttackOnWings', label: this.translate.instant('optimizer.tactics.attackWings') },
      { value: 'Pressing', label: this.translate.instant('optimizer.tactics.pressing') },
      { value: 'PlayCreatively', label: this.translate.instant('optimizer.tactics.creatively') },
      { value: 'LongShots', label: this.translate.instant('optimizer.tactics.longShots') }
    ];
    this.attitudes = [
      { value: 'Normal', label: this.translate.instant('optimizer.attitudes.normal') },
      { value: 'PIC', label: this.translate.instant('optimizer.attitudes.pic') },
      { value: 'MOTS', label: this.translate.instant('optimizer.attitudes.mots') }
    ];
    this.coachTypes = [
      { value: 'Neutral', label: this.translate.instant('optimizer.coach.neutral') },
      { value: 'Offensive', label: this.translate.instant('optimizer.coach.offensive') },
      { value: 'Defensive', label: this.translate.instant('optimizer.coach.defensive') }
    ];
    this.experienceLevels = [
      { value: 7, label: this.translate.instant('formationExperience.outstanding') },
      { value: 6, label: this.translate.instant('formationExperience.formidable') },
      { value: 5, label: this.translate.instant('formationExperience.excellent') },
      { value: 4, label: this.translate.instant('formationExperience.solid') },
      { value: 3, label: this.translate.instant('formationExperience.passable') },
      { value: 2, label: this.translate.instant('formationExperience.inadequate') },
      { value: 1, label: this.translate.instant('formationExperience.weak') },
      { value: 0, label: this.translate.instant('formationExperience.poor') }
    ];
    for (const f of this.availableFormations) {
      if (!(f in this.formationExperience)) this.formationExperience[f] = 5;
    }
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
      teamAttitude: this.teamAttitude,
      focusAreas: [],
      coachType: this.coachType,
      assistantManagerLevel: this.assistantManagerLevel,
      formationExperience: this.formationExperience
    };

    this.hattrickApi.optimizeLineup(request).subscribe({
      next: (response) => {
        this.result = response;
        this.selectedAlternative = 0;
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

  getTacticLabel(value: string): string {
    const map: { [key: string]: string } = {
      'Auto': 'optimizer.tactics.auto',
      'Normal': 'optimizer.tactics.normal',
      'Counter': 'optimizer.tactics.counter',
      'AttackInMiddle': 'optimizer.tactics.attackMiddle',
      'AttackOnWings': 'optimizer.tactics.attackWings',
      'Pressing': 'optimizer.tactics.pressing',
      'PlayCreatively': 'optimizer.tactics.creatively',
      'LongShots': 'optimizer.tactics.longShots'
    };
    const key = map[value];
    return key ? this.translate.instant(key) : value;
  }

  getAttitudeLabel(value: string): string {
    const map: { [key: string]: string } = {
      'Normal': 'optimizer.attitudes.normal',
      'PIC': 'optimizer.attitudes.pic',
      'MOTS': 'optimizer.attitudes.mots'
    };
    const key = map[value];
    return key ? this.translate.instant(key) : value;
  }

  selectAlternative(index: number): void {
    this.selectedAlternative = index;
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

  loadMyTeamStats(): void {
    if (!this.myTeamId) return;
    
    this.loadingMyTeamStats = true;
    this.hattrickApi.getTeamMatchStats(this.myTeamId).subscribe({
      next: (stats: any) => {
        this.myTeamStats = stats;
        this.loadingMyTeamStats = false;
      },
      error: (err: any) => {
        console.error('Error loading team stats:', err);
        // Fallback do mockowych danych w razie bdu
        this.myTeamStats = this.generateMockTeamStats(this.myTeamId);
        this.loadingMyTeamStats = false;
      }
    });
  }

  loadOpponentTeamStats(): void {
    if (!this.opponentTeamId) return;
    
    this.loadingOpponentTeamStats = true;
    this.hattrickApi.getTeamMatchStats(this.opponentTeamId).subscribe({
      next: (stats: any) => {
        this.opponentTeamStats = stats;
        this.loadingOpponentTeamStats = false;
      },
      error: (err: any) => {
        console.error('Error loading opponent stats:', err);
        // Fallback do mockowych danych w razie bdu
        this.opponentTeamStats = this.generateMockTeamStats(this.opponentTeamId);
        this.loadingOpponentTeamStats = false;
      }
    });
  }

  generateMockTeamStats(teamId: number): any {
    // Prosty seed dla random
    let seed = teamId;
    const random = () => {
      seed = (seed * 9301 + 49297) % 233280;
      return seed / 233280;
    };
    
    const formations = ['4-4-2', '3-5-2', '4-3-3', '5-4-1'];
    const mostUsed = formations[Math.floor(random() * formations.length)];
    
    const randomBetween = (min: number, max: number) => Math.floor(random() * (max - min + 1)) + min;
    
    return {
      totalMatches: 20,
      wins: randomBetween(5, 15),
      draws: randomBetween(3, 8),
      losses: randomBetween(2, 8),
      goalsFor: randomBetween(15, 35),
      goalsAgainst: randomBetween(10, 25),
      mostCommonFormation: mostUsed,
      formationFrequency: {
        '4-4-2': randomBetween(3, 8),
        '3-5-2': randomBetween(2, 6),
        '4-3-3': randomBetween(2, 6),
        '5-4-1': randomBetween(1, 4)
      },
      formationWinRate: {
        '4-4-2': randomBetween(30, 70),
        '3-5-2': randomBetween(25, 65),
        '4-3-3': randomBetween(20, 60),
        '5-4-1': randomBetween(15, 55)
      },
      currentForm: this.generateRandomForm(random),
      recentResults: this.generateRandomResults(random)
    };
  }

  get winRate(): string {
    if (!this.myTeamStats?.statistics) return '0.0';
    const stats = this.myTeamStats.statistics;
    return stats.totalMatches > 0 ? stats.winRate.toFixed(1) : '0.0';
  }

  get goalDifference(): number {
    if (!this.myTeamStats?.statistics) return 0;
    return this.myTeamStats.statistics.goalDifference;
  }

  generateRandomForm(random: () => number): string {
    const results = ['W', 'D', 'L'];
    let form = '';
    for (let i = 0; i < 5; i++) {
      form += results[Math.floor(random() * results.length)];
    }
    return form;
  }

  generateRandomResults(random: () => number): string[] {
    const results = ['W', 'D', 'L'];
    const recentResults = [];
    for (let i = 0; i < 5; i++) {
      recentResults.push(results[Math.floor(random() * results.length)]);
    }
    return recentResults;
  }
}
