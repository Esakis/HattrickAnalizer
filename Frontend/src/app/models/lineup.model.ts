import { Player } from './player.model';

export interface Lineup {
  positions: { [key: string]: LineupPosition };
  tacticType: string;
  tacticSkill: string;
  predictedRatings: LineupRatings;
  formation?: string;
}

export interface LineupPosition {
  position: string;
  player: Player | null;
  behavior: string;
}

export interface LineupRatings {
  midfield: number;
  rightDefense: number;
  centralDefense: number;
  leftDefense: number;
  rightAttack: number;
  centralAttack: number;
  leftAttack: number;
  overall: number;
}

export interface OptimizerRequest {
  myTeamId: number;
  opponentTeamId: number;
  preferredTactic: string;
  teamAttitude: string;
  focusAreas: string[];
  coachType: string;
  assistantManagerLevel: number;
  formationExperience: { [formation: string]: number };
}

export interface FormationAlternative {
  formation: string;
  tactic: string;
  attitude: string;
  winProbability: number;
  drawProbability: number;
  lossProbability: number;
  expectedGoalsFor: number;
  expectedGoalsAgainst: number;
  disorderRisk: number;
  ratings: LineupRatings;
}

export interface OptimizerResponse {
  optimalLineup: Lineup;
  recommendations: string[];
  comparison: TeamComparison;
  alternatives: FormationAlternative[];
}

export interface TeamComparison {
  myTeamRatings: LineupRatings;
  opponentRatings: LineupRatings;
  strengths: string[];
  weaknesses: string[];
}
