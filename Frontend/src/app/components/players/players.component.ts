import { Component, OnInit } from '@angular/core';
import { HattrickApiService } from '../../services/hattrick-api.service';
import { Player } from '../../models/player.model';
import { TranslateService } from '@ngx-translate/core';
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

  positions: { value: string; label: string }[] = [];
  sortOptions: { value: string; label: string }[] = [];

  constructor(private hattrickApi: HattrickApiService, private translate: TranslateService) {}

  ngOnInit(): void {
    this.sessionId = localStorage.getItem('hattrick_session_id');
    this.initializeTranslations();
  }

  private initializeTranslations(): void {
    this.positions = [
      { value: 'all', label: this.translate.instant('players.allPositions') },
      { value: 'GK', label: this.translate.instant('players.goalkeeper') },
      { value: 'DEF', label: this.translate.instant('players.defender') },
      { value: 'MID', label: this.translate.instant('players.midfielder') },
      { value: 'FW', label: this.translate.instant('players.forward') }
    ];

    this.sortOptions = [
      { value: 'name', label: this.translate.instant('players.sortByName') },
      { value: 'age', label: this.translate.instant('players.sortByAge') },
      { value: 'form', label: this.translate.instant('players.sortByForm') },
      { value: 'stamina', label: this.translate.instant('players.sortByStamina') }
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
    return pos ? pos.label : position;
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
}
