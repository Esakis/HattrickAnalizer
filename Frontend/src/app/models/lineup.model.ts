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
  rating?: number;
  // Gracz z siniakiem (InjuryLevel == 0) — może grać, ale z ryzykiem.
  isBruised?: boolean;
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
  preferredFormation?: string;
  language: string;
  matchId?: number;
  isHomeMatch?: boolean;
  matchDate?: string;
}

// Pogoda regionu gospodarza (kody CHPP: 0=deszcz, 1=pochmurno, 2=częściowe zachmurzenie, 3=słońce).
export interface MatchWeather {
  regionId: number;
  regionName: string;
  weatherId: number;
  source: string;
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
  // Pochodzenie ocen przeciwnika: "lastMatch" | "default" | "mock"
  opponentRatingsSource?: string;
  opponentRatingsMatchId?: number;
  opponentRatingsMatchDate?: string;
  weather?: MatchWeather | null;
}

export interface TeamComparison {
  myTeamRatings: LineupRatings;
  opponentRatings: LineupRatings;
  strengths: string[];
  weaknesses: string[];
}
