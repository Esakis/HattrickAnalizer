import { Component, OnInit, isDevMode } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { Player } from '../../models/player.model';
import { TranslateService } from '@ngx-translate/core';
import { DataCacheService } from '../../services/data-cache.service';
import { LoadStatusService } from '../../services/load-status.service';
import { PlayerHistoryService, PlayerChangeResult } from '../../services/player-history.service';
import {
  SkillLevel,
  FormLevel,
  LeaderLevel,
  FormationExperienceLevel,
  SponsorLevel,
  FanMoodLevel,
  MatchExpectationLevel,
  SeasonExpectationLevel,
  PersonalityLevel,
  HonestyLevel,
  AggressivenessLevel,
  TeamMoraleLevel,
  TeamConfidenceLevel
} from '../../enums/skill-levels.enum';

@Component({
  selector: 'app-players',
  templateUrl: './players.component.html',
  styleUrls: ['./players.component.scss']
})
export class PlayersComponent implements OnInit {
  teamId: number | null = null;
  players: Player[] = [];
  loading: boolean = false;
  error: string | null = null;
  sessionId: string | null = null;

  sortBy: string = 'name';
  filterPosition: string = 'all';

  viewMode: 'table' | 'cards' = 'table';
  playerSortColumn: string = 'form';
  playerSortDirection: 'asc' | 'desc' = 'desc';

  isDebug = isDevMode();
  selectedPlayerId: number | null = null;
  selectedPlayerName: string = '';

  savingHistory = false;
  saveResults: PlayerChangeResult[] | null = null;
  saveResultsVisible = false;

  positions: { value: string; label: string }[] = [];
  sortOptions: { value: string; label: string }[] = [];

  constructor(
    private hattrickApi: HattrickApiService,
    private translate: TranslateService,
    private cache: DataCacheService,
    private loadStatus: LoadStatusService,
    private playerHistory: PlayerHistoryService
  ) {}

  ngOnInit(): void {
    this.sessionId = localStorage.getItem('hattrick_session_id');
    this.initializeTranslations();

    const cachedTeam = this.cache.ownTeam$.value;
    if (cachedTeam?.players?.length) {
      this.teamId = cachedTeam.teamId;
      this.players = cachedTeam.players;
      return;
    }

    const auth = this.cache.auth$.value;
    if (auth.authorized && auth.ownTeamId) {
      this.teamId = auth.ownTeamId;
      this.loading = true;
    }

    this.cache.ownTeam$.subscribe(team => {
      if (team?.players?.length) {
        this.teamId = team.teamId;
        this.players = team.players;
        this.loading = false;
      }
    });

    this.cache.auth$.subscribe(a => {
      if (a.authorized && a.ownTeamId && !this.teamId) {
        this.teamId = a.ownTeamId;
        this.loadPlayers();
      }
    });
  }

  private initializeTranslations(): void {
    this.positions = [
      { value: 'all', label: 'players.allPositions' },
      { value: 'GK', label: 'players.goalkeeper' },
      { value: 'DEF', label: 'players.defender' },
      { value: 'MID', label: 'players.midfielder' },
      { value: 'FW', label: 'players.forward' }
    ];

    this.sortOptions = [
      { value: 'name', label: 'players.sortByName' },
      { value: 'age', label: 'players.sortByAge' },
      { value: 'form', label: 'players.sortByForm' },
      { value: 'stamina', label: 'players.sortByStamina' }
    ];
  }

  loadPlayers(): void {
    if (!this.teamId) {
      this.error = this.translate.instant('players.enterTeamId');
      return;
    }

    this.loading = true;
    this.error = null;

    this.hattrickApi.getPlayers(this.teamId).subscribe({
      next: (players) => {
        this.players = players;
        this.loading = false;
      },
      error: (err) => {
        this.error = this.translate.instant('players.errorLoadingPlayers') + err.message;
        this.loading = false;
      }
    });
  }

  get filteredPlayers(): Player[] {
    let filtered = [...this.players];

    if (this.filterPosition !== 'all') {
      filtered = filtered.filter(p => this.getPlayerPosition(p) === this.filterPosition);
    }

    filtered.sort((a, b) => {
      switch (this.sortBy) {
        case 'name':
          return `${a.lastName} ${a.firstName}`.localeCompare(`${b.lastName} ${b.firstName}`);
        case 'age':
          return a.age - b.age;
        case 'form':
          return b.form - a.form;
        case 'stamina':
          return b.stamina - a.stamina;
        default:
          return 0;
      }
    });

    return filtered;
  }

  get averageAge(): string {
    if (this.filteredPlayers.length === 0) return '0.0';
    const sum = this.filteredPlayers.reduce((acc, p) => acc + p.age, 0);
    return (sum / this.filteredPlayers.length).toFixed(1);
  }

  get averageForm(): string {
    if (this.filteredPlayers.length === 0) return '0.0';
    const sum = this.filteredPlayers.reduce((acc, p) => acc + p.form, 0);
    return (sum / this.filteredPlayers.length).toFixed(1);
  }

  getPlayerPosition(player: Player): string {
    const skills = player.skills;
    if (skills.keeper > 5) return 'GK';
    if (skills.defending > skills.playmaking && skills.defending > skills.scoring) return 'DEF';
    if (skills.playmaking > skills.scoring) return 'MID';
    return 'FW';
  }

  getPositionLabel(position: string): string {
    const pos = this.positions.find(p => p.value === position);
    return pos ? this.translate.instant(pos.label) : position;
  }

  getSkillLevel(value: number): string {
    return this.translate.instant(`playerAbilities.${value}`);
  }

  getFormLevel(value: number): string {
    return this.translate.instant(`form.${value}`);
  }

  getLeaderLevel(value: number): string {
    return this.translate.instant(`leadership.${value}`);
  }

  getFormationExperienceLevel(value: number): string {
    return this.translate.instant(`formationExperience.${value}`);
  }

  getSponsorLevel(value: number): string {
    return this.translate.instant(`sponsors.${value}`);
  }

  getFanMoodLevel(value: number): string {
    return this.translate.instant(`fanMood.${value}`);
  }

  getMatchExpectationLevel(value: number): string {
    return this.translate.instant(`fanMatchExpectations.${value}`);
  }

  getSeasonExpectationLevel(value: number): string {
    return this.translate.instant(`fanSeasonExpectations.${value}`);
  }

  getPersonalityLevel(value: number): string {
    return this.translate.instant(`agreeability.${value}`);
  }

  getHonestyLevel(value: number): string {
    return this.translate.instant(`honesty.${value}`);
  }

  getAggressivenessLevel(value: number): string {
    return this.translate.instant(`aggressiveness.${value}`);
  }

  getTeamMoraleLevel(value: number): string {
    return this.translate.instant(`teamSpirit.${value}`);
  }

  getTeamConfidenceLevel(value: number): string {
    return this.translate.instant(`teamConfidence.${value}`);
  }

  getFormClass(form: number): string {
    if (form >= 7) return 'excellent';
    if (form >= 5) return 'good';
    if (form >= 3) return 'average';
    return 'poor';
  }

  sortTable(column: string): void {
    if (this.playerSortColumn === column) {
      this.playerSortDirection = this.playerSortDirection === 'asc' ? 'desc' : 'asc';
    } else {
      this.playerSortColumn = column;
      this.playerSortDirection = 'desc';
    }
  }

  get sortedPlayers(): Player[] {
    if (!this.players || this.players.length === 0) return [];
    return [...this.players].sort((a, b) => {
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

  checkAndSaveHistory(): void {
    if (!this.teamId || this.players.length === 0) return;
    this.savingHistory = true;
    this.saveResults = null;
    this.playerHistory.checkAndSave(this.players, this.teamId).subscribe({
      next: (results) => {
        this.saveResults = results;
        this.saveResultsVisible = true;
        this.savingHistory = false;
      },
      error: () => {
        this.savingHistory = false;
      }
    });
  }

  get saveChangedCount(): number {
    return this.saveResults?.filter(r => r.changed).length ?? 0;
  }

  openPlayerHistory(player: Player): void {
    if (!this.isDebug) return;
    this.selectedPlayerId = player.playerId;
    this.selectedPlayerName = `${player.firstName} ${player.lastName}`;
  }

  closePlayerHistory(): void {
    this.selectedPlayerId = null;
    this.selectedPlayerName = '';
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
}
