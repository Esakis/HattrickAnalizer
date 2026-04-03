import { Player } from './player.model';

export interface Team {
  teamId: number;
  teamName: string;
  players: Player[];
  ratings?: TeamRatings;
}

export interface TeamRatings {
  midfieldRating: number;
  rightDefenseRating: number;
  centralDefenseRating: number;
  leftDefenseRating: number;
  rightAttackRating: number;
  centralAttackRating: number;
  leftAttackRating: number;
}
