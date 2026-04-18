import { Component, OnInit } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { OptimizerRequest, OptimizerResponse, LineupPosition, Lineup } from '../../models/lineup.model';
import { TranslateService } from '@ngx-translate/core';
import { DataCacheService } from '../../services/data-cache.service';
import { Player, PlayerMatchStats } from '../../models/player.model';

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
  availableFormations: string[] = ['5-5-0','5-4-1','5-3-2','5-2-3','4-5-1','4-4-2','4-3-3','3-5-2','3-4-3','2-5-3'];
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

  // Wybór formacji i taktyki dla mojej drużyny
  selectedMyFormation: string = 'Auto';
  selectedMyTactic: string = 'Auto';
  
  // Wybór formacji i taktyki dla przeciwnika
  selectedOpponentFormation: string = 'Auto';
  selectedOpponentTactic: string = 'Auto';
  
  // Optimal Lineup dla przeciwnika
  opponentOptimalLineup: Lineup | null = null;
  
  // Sortowanie tabeli graczy
  playerSortColumn: string = 'form';
  playerSortDirection: 'asc' | 'desc' = 'desc';
  
  // Trener i sztab
  trainerLevel: number = 0;
  assistantCoachLevel: number = 0;
  formCoachLevel: number = 0;
  
  // Kolumny do sortowania
  sortableColumns = [
    { key: 'name', label: 'Imię' },
    { key: 'age', label: 'Wiek' },
    { key: 'form', label: 'Forma' },
    { key: 'stamina', label: 'Kondycja' },
    { key: 'keeper', label: 'Bramkarz' },
    { key: 'defending', label: 'Obrona' },
    { key: 'playmaking', label: 'Rozgrywanie' },
    { key: 'winger', label: 'Skrzydło' },
    { key: 'passing', label: 'Podania' },
    { key: 'scoring', label: 'Skuteczność' },
    { key: 'setPieces', label: 'Stałe fragmenty' },
    { key: 'goals', label: 'Bramki' },
    { key: 'assists', label: 'Asysty' },
    { key: 'avgForm', label: 'Śr. Forma' },
    { key: 'goalsPerMatch', label: 'Bramki/Mecz' },
    { key: 'matchesPerGoal', label: 'Mecze/Bramka' }
  ];

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
      { value: 10, label: this.translate.instant('formationExperience.10') },
      { value: 9, label: this.translate.instant('formationExperience.9') },
      { value: 8, label: this.translate.instant('formationExperience.8') },
      { value: 7, label: this.translate.instant('formationExperience.7') },
      { value: 6, label: this.translate.instant('formationExperience.6') },
      { value: 5, label: this.translate.instant('formationExperience.5') },
      { value: 4, label: this.translate.instant('formationExperience.4') },
      { value: 3, label: this.translate.instant('formationExperience.3') }
    ];
    for (const f of this.availableFormations) {
      if (!(f in this.formationExperience)) this.formationExperience[f] = 6;
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
      formationExperience: this.formationExperience,
      preferredFormation: this.selectedMyFormation,
      language: this.translate.currentLang || 'pl'
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
        // Generuj statystyki dla graczy
        this.generatePlayerStats();
        // Pobierz doświadczenie formacji
        this.loadFormationExperience();
      },
      error: () => {
        this.loadingMyTeam = false;
      }
    });
  }

  loadFormationExperience(): void {
    if (!this.myTeamId) return;
    
    this.hattrickApi.getFormationExperience(this.myTeamId).subscribe({
      next: (experience) => {
        // Aktualizuj doświadczenie formacji z danych API
        for (const formation of this.availableFormations) {
          if (experience[formation] !== undefined) {
            this.formationExperience[formation] = experience[formation];
          }
        }
      },
      error: (err) => {
        console.error('Error loading formation experience:', err);
        // W przypadku błędu zachowaj domyślne wartości
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
    
    // Najpierw pobierz podstawowe info o drużynie (nazwa)
    this.hattrickApi.getTeam(this.opponentTeamId).subscribe({
      next: (team) => {
        this.opponentTeamName = team.teamName;
      },
      error: () => {}
    });
    
    // Następnie pobierz graczy z wzbogaconymi statystykami
    this.hattrickApi.getPlayers(this.opponentTeamId).subscribe({
      next: (players) => {
        this.opponentTeamPlayers = players;
        this.lastLoadedOpponentId = this.opponentTeamId;
        this.loadingOpponentTeam = false;
        // Generuj statystyki dla graczy przeciwnika (tylko jeśli brak danych z API)
        this.generatePlayerStats();
        // Zbuduj optymalny skład dla przeciwnika
        this.buildOpponentOptimalLineup();
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
        this.myTeamStats = null;
      }
      this.loadMyTeam();
      this.loadMyTeamStats();
    }
  }

  onOpponentTeamIdChange(): void {
    if (this.opponentTeamId) {
      // Wyczyść dane jeśli ID się zmieniło
      if (this.lastLoadedOpponentId !== this.opponentTeamId) {
        this.opponentTeamPlayers = [];
        this.opponentTeamName = '';
        this.opponentTeamStats = null;
      }
      this.loadOpponentTeam();
      this.loadOpponentTeamStats();
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
        // Przebuduj skład przeciwnika z poprawną formacją
        if (this.opponentTeamPlayers.length > 0) {
          this.buildOpponentOptimalLineup();
        }
      },
      error: (err: any) => {
        console.error('Error loading opponent stats:', err);
        // Fallback do mockowych danych w razie bdu
        this.opponentTeamStats = this.generateMockTeamStats(this.opponentTeamId);
        this.loadingOpponentTeamStats = false;
        // Przebuduj skład przeciwnika z poprawną formacją
        if (this.opponentTeamPlayers.length > 0) {
          this.buildOpponentOptimalLineup();
        }
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

  // ==================== SORTOWANIE GRACZY ====================
  
  sortPlayers(column: string): void {
    if (this.playerSortColumn === column) {
      this.playerSortDirection = this.playerSortDirection === 'asc' ? 'desc' : 'asc';
    } else {
      this.playerSortColumn = column;
      this.playerSortDirection = 'desc';
    }
  }

  get sortedMyTeamPlayers(): Player[] {
    if (!this.myTeamPlayers || this.myTeamPlayers.length === 0) return [];
    
    return [...this.myTeamPlayers].sort((a, b) => {
      let valueA = this.getPlayerSortValue(a, this.playerSortColumn);
      let valueB = this.getPlayerSortValue(b, this.playerSortColumn);
      
      if (typeof valueA === 'string') {
        valueA = valueA.toLowerCase();
        valueB = (valueB as string).toLowerCase();
      }
      
      if (valueA < valueB) return this.playerSortDirection === 'asc' ? -1 : 1;
      if (valueA > valueB) return this.playerSortDirection === 'asc' ? 1 : -1;
      return 0;
    });
  }

  getPlayerSortValue(player: Player, column: string): any {
    switch (column) {
      case 'name': return `${player.firstName} ${player.lastName}`;
      case 'age': return player.age;
      case 'form': return player.form;
      case 'stamina': return player.stamina;
      case 'keeper': return player.skills?.keeper || 0;
      case 'defending': return player.skills?.defending || 0;
      case 'playmaking': return player.skills?.playmaking || 0;
      case 'winger': return player.skills?.winger || 0;
      case 'passing': return player.skills?.passing || 0;
      case 'scoring': return player.skills?.scoring || 0;
      case 'setPieces': return player.skills?.setPieces || 0;
      case 'goals': return player.matchStats?.goals || 0;
      case 'assists': return player.matchStats?.assists || 0;
      case 'avgForm': return player.matchStats?.averageForm || player.form;
      case 'goalsPerMatch': return player.matchStats?.goalsPerMatch || 0;
      case 'matchesPerGoal': return player.matchStats?.matchesPerGoal || 999;
      default: return 0;
    }
  }

  // ==================== ZMIANA FORMACJI I TAKTYKI ====================
  
  onMyFormationChange(): void {
    // Po zmianie formacji natychmiast przelicz sklad z ograniczeniem do tej formacji.
    if (this.myTeamId && this.opponentTeamId && this.myTeamPlayers.length >= 11) {
      this.optimizeLineup();
    }
  }

  onMyTacticChange(): void {
    this.preferredTactic = this.selectedMyTactic;
    if (this.result && this.myTeamId && this.opponentTeamId) {
      this.optimizeLineup();
    }
  }

  onOpponentFormationChange(): void {
    if (this.selectedOpponentFormation !== 'Auto' && this.opponentTeamPlayers.length > 0) {
      this.buildOpponentOptimalLineup();
    }
  }

  onOpponentTacticChange(): void {
    if (this.opponentTeamPlayers.length > 0) {
      this.buildOpponentOptimalLineup();
    }
  }

  // ==================== OPTIMAL LINEUP DLA PRZECIWNIKA ====================

  buildOpponentOptimalLineup(): void {
    if (this.opponentTeamPlayers.length < 11) return;
    
    // Użyj najczęstszej formacji z ostatnich 5 meczów przeciwnika
    const formation = this.selectedOpponentFormation === 'Auto' 
      ? (this.opponentTeamStats?.statistics?.mostCommonFormation || this.getBestFormationForTeam(this.opponentTeamPlayers))
      : this.selectedOpponentFormation;
    
    const positions = this.getFormationPositions(formation);
    const lineup: Lineup = {
      positions: {},
      tacticType: this.selectedOpponentTactic === 'Auto' ? 'Normal' : this.selectedOpponentTactic,
      tacticSkill: '',
      predictedRatings: {
        midfield: 0, rightDefense: 0, centralDefense: 0, leftDefense: 0,
        rightAttack: 0, centralAttack: 0, leftAttack: 0, overall: 0
      },
      formation: formation
    };

    const availablePlayers = [...this.opponentTeamPlayers].filter(p => p.injuryLevel <= 0);
    const usedPlayers = new Set<number>();

    for (const pos of positions) {
      const bestPlayer = this.findBestPlayerForPosition(pos, availablePlayers, usedPlayers);
      if (bestPlayer) {
        lineup.positions[pos] = {
          position: pos,
          player: bestPlayer,
          behavior: 'Normal',
          rating: this.calculateSkillBasedRating(bestPlayer, pos)
        };
        usedPlayers.add(bestPlayer.playerId);
      }
    }

    this.opponentOptimalLineup = lineup;
  }

  getFormationPositions(formation: string): string[] {
    const formationMap: { [key: string]: string[] } = {
      '5-5-0': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'LW'],
      '5-4-1': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'RIM', 'LIM', 'LW', 'FW'],
      '5-3-2': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RIM', 'IM', 'LIM', 'RFW', 'LFW'],
      '5-2-3': ['GK', 'RWB', 'RCD', 'CD', 'LCD', 'LWB', 'RW', 'LW', 'RFW', 'FW', 'LFW'],
      '4-5-1': ['GK', 'RWB', 'RCD', 'LCD', 'LWB', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'FW'],
      '4-4-2': ['GK', 'RWB', 'RCD', 'LCD', 'LWB', 'RW', 'RIM', 'LIM', 'LW', 'RFW', 'LFW'],
      '4-3-3': ['GK', 'RWB', 'RCD', 'LCD', 'LWB', 'RW', 'IM', 'LW', 'RFW', 'FW', 'LFW'],
      '3-5-2': ['GK', 'RCD', 'CD', 'LCD', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'LFW'],
      '3-4-3': ['GK', 'RCD', 'CD', 'LCD', 'RW', 'RIM', 'LIM', 'LW', 'RFW', 'FW', 'LFW'],
      '2-5-3': ['GK', 'RCD', 'LCD', 'RW', 'RIM', 'IM', 'LIM', 'LW', 'RFW', 'FW', 'LFW']
    };
    return formationMap[formation] || formationMap['4-4-2'];
  }

  findBestPlayerForPosition(position: string, players: Player[], usedPlayers: Set<number>): Player | null {
    const available = players.filter(p => !usedPlayers.has(p.playerId));
    if (available.length === 0) return null;

    return available.reduce((best, player) => {
      const bestScore = this.getPositionScore(best, position);
      const playerScore = this.getPositionScore(player, position);
      return playerScore > bestScore ? player : best;
    });
  }

  getPositionScore(player: Player, position: string): number {
    // Sprawdź czy gracz ma ocenę na tej pozycji z poprzednich meczów
    if (player.matchStats?.positionRatings?.[position]) {
      return player.matchStats.positionRatings[position];
    }

    // Jeśli nie ma rzeczywistych ocen, użyj calculateSkillBasedRating
    return this.calculateSkillBasedRating(player, position);
  }

  getBestFormationForTeam(players: Player[]): string {
    // Analiza składu i wybór najlepszej formacji
    const defenders = players.filter(p => p.skills.defending > 10).length;
    const midfielders = players.filter(p => p.skills.playmaking > 10).length;
    const forwards = players.filter(p => p.skills.scoring > 10).length;
    const wingers = players.filter(p => p.skills.winger > 10).length;

    if (defenders >= 5 && midfielders >= 4) return '5-4-1';
    if (midfielders >= 5) return '4-5-1';
    if (forwards >= 3 && wingers >= 2) return '4-3-3';
    if (defenders >= 4 && forwards >= 2) return '4-4-2';
    if (midfielders >= 5 && forwards >= 2) return '3-5-2';
    
    return '4-4-2'; // Domyślna
  }

  assignPlayersToFormation(formation: string, players: Player[]): void {
    // Ta metoda może być używana do wizualizacji przypisania
    // W praktyce optymalizator robi to automatycznie
  }

  // ==================== GENEROWANIE STATYSTYK GRACZY ====================

  generatePlayerStats(): void {
    // Nie generujemy mockowych ocen - używamy tylko rzeczywistych danych z API
    this.myTeamPlayers = this.myTeamPlayers.map(player => {
      if (!player.matchStats) {
        player.matchStats = {
          totalMatches: 0,
          goals: 0,
          assists: 0,
          yellowCards: 0,
          redCards: 0,
          averageRating: 0,
          averageForm: player.form,
          goalsPerMatch: 0,
          matchesPerGoal: 0,
          minutesPlayed: 0,
          positionRatings: {}
        };
      }
      return player;
    });
    
    this.opponentTeamPlayers = this.opponentTeamPlayers.map(player => {
      if (!player.matchStats) {
        player.matchStats = {
          totalMatches: 0,
          goals: 0,
          assists: 0,
          yellowCards: 0,
          redCards: 0,
          averageRating: 0,
          averageForm: player.form,
          goalsPerMatch: 0,
          matchesPerGoal: 0,
          minutesPlayed: 0,
          positionRatings: {}
        };
      }
      return player;
    });
  }

  generateMockPlayerStats(player: Player): PlayerMatchStats {
    // Generuj realistyczne statystyki na podstawie umiejętności gracza
    let seed = player.playerId;
    const random = () => {
      seed = (seed * 9301 + 49297) % 233280;
      return seed / 233280;
    };

    const isForward = player.skills.scoring > 10;
    const isMidfielder = player.skills.playmaking > 10;
    const isDefender = player.skills.defending > 10;
    const isKeeper = player.skills.keeper > 10;

    const totalMatches = Math.floor(random() * 20) + 10;
    let goals = 0;
    let assists = 0;

    if (isForward) {
      goals = Math.floor(random() * totalMatches * 0.6) + Math.floor(player.skills.scoring / 3);
      assists = Math.floor(random() * totalMatches * 0.2);
    } else if (isMidfielder) {
      goals = Math.floor(random() * totalMatches * 0.2);
      assists = Math.floor(random() * totalMatches * 0.4) + Math.floor(player.skills.passing / 4);
    } else if (isDefender) {
      goals = Math.floor(random() * 3);
      assists = Math.floor(random() * 5);
    } else if (isKeeper) {
      goals = 0;
      assists = 0;
    }

    const goalsPerMatch = totalMatches > 0 ? goals / totalMatches : 0;
    const matchesPerGoal = goals > 0 ? totalMatches / goals : 0;

    return {
      totalMatches,
      goals,
      assists,
      yellowCards: Math.floor(random() * 5),
      redCards: Math.floor(random() * 2),
      averageRating: 5 + random() * 4,
      averageForm: player.form - 1 + random() * 2,
      goalsPerMatch,
      matchesPerGoal,
      minutesPlayed: totalMatches * 90 - Math.floor(random() * totalMatches * 20),
      positionRatings: this.generatePositionRatings(player, random)
    };
  }

  generatePositionRatings(player: Player, random: () => number): { [position: string]: number } {
    const ratings: { [position: string]: number } = {};
    const positions = ['GK', 'RWB', 'LWB', 'RCD', 'LCD', 'CD', 'RW', 'LW', 'RIM', 'LIM', 'IM', 'RFW', 'LFW', 'FW'];
    
    for (const pos of positions) {
      // Oblicz bazową ocenę na podstawie umiejętności gracza (skala Hattrick 0-20)
      const skillScore = this.calculateSkillBasedRating(player, pos);
      // Dodaj losową wariację ±1.5 żeby symulować różnice między meczami
      const variation = (random() - 0.5) * 3;
      ratings[pos] = Math.max(1, Math.min(20, skillScore + variation));
    }
    
    return ratings;
  }

  calculateSkillBasedRating(player: Player, position: string): number {
    const skills = player.skills;
    // Skala Hattrick 0-20 (ocena meczowa). Bramkarz magiczny (19) powinien dawac ~9 na start.
    // Forma 1-9 -> mnoznik 0.7-1.1, kondycja 1-9 -> 0.9-1.05.
    const formMult = 0.6 + (player.form / 9) * 0.5;
    const staminaMult = 0.9 + (player.stamina / 9) * 0.15;
    const eff = formMult * staminaMult;

    let main = 0;
    switch (position) {
      case 'GK':
        main = skills.keeper;
        break;
      case 'RWB':
      case 'LWB':
        main = 0.7 * skills.defending + 0.3 * skills.winger;
        break;
      case 'RCD':
      case 'LCD':
      case 'CD':
        main = skills.defending;
        break;
      case 'RW':
      case 'LW':
        main = 0.6 * skills.winger + 0.4 * skills.playmaking;
        break;
      case 'RIM':
      case 'LIM':
      case 'IM':
        main = skills.playmaking;
        break;
      case 'RFW':
      case 'LFW':
      case 'FW':
        main = skills.scoring;
        break;
      default:
        main = skills.playmaking;
    }

    // Wspolczynnik 0.4 kalibruje wynik do rzeczywistych ocen meczowych Hattrick.
    return Math.max(0, Math.min(20, main * 0.4 * eff));
  }

  getPlayerPositionRating(player: Player, position: string, backendRating?: number): string {
    // Priorytet 1: rzeczywista ocena z ostatnich meczow CHPP (positionRatings na danej pozycji).
    const real = player.matchStats?.positionRatings?.[position];
    if (real !== undefined && real > 0) {
      return real.toFixed(1);
    }
    // Priorytet 2: rating wyliczony przez backend (uwzglednia forme/XP/lojalnosc).
    if (backendRating !== undefined && backendRating > 0) {
      return backendRating.toFixed(1);
    }
    // Fallback: frontendowe oszacowanie na podstawie umiejetnosci.
    return this.calculateSkillBasedRating(player, position).toFixed(1);
  }

  // ==================== POMOCNICZE ====================

  getOpponentPositionKeys(): string[] {
    if (!this.opponentOptimalLineup?.positions) return [];
    return Object.keys(this.opponentOptimalLineup.positions);
  }

  // Sektory uzywane w wykresie porownania (7 aspektow Hattrick)
  comparisonSectors: { key: keyof import('../../models/lineup.model').LineupRatings; label: string }[] = [
    { key: 'midfield', label: 'optimizer.comparison.midfield' },
    { key: 'leftDefense', label: 'optimizer.comparison.leftDefense' },
    { key: 'centralDefense', label: 'optimizer.comparison.centralDefense' },
    { key: 'rightDefense', label: 'optimizer.comparison.rightDefense' },
    { key: 'leftAttack', label: 'optimizer.comparison.leftAttack' },
    { key: 'centralAttack', label: 'optimizer.comparison.centralAttack' },
    { key: 'rightAttack', label: 'optimizer.comparison.rightAttack' }
  ];

  localizeText(text: string): string {
    const parts = text.split(' / ');
    if (parts.length < 2) return text;
    return this.translate.currentLang === 'en' ? parts[1].trim() : parts[0].trim();
  }

  getSectorBarWidth(myValue: number, oppValue: number, side: 'my' | 'opp'): number {
    const total = (myValue || 0) + (oppValue || 0);
    if (total <= 0) return 50;
    const pct = ((side === 'my' ? myValue : oppValue) / total) * 100;
    return Math.max(5, Math.min(95, pct));
  }

  getSkillLevel(value: number): string {
    const levels = ['beznadziejny', 'fatalny', 'nędzny', 'kiepski', 'słaby', 'przeciętny',
                    'zadowalający', 'solidny', 'znakomity', 'fantastyczny', 'olśniewający',
                    'błyskotliwy', 'mistrzowski', 'światowej klasy', 'nadprzyrodzony', 'tytaniczny',
                    'nieziemski', 'mityczny', 'magiczny', 'utopijny', 'boski'];
    return levels[Math.min(value, levels.length - 1)] || `${value}`;
  }
}
