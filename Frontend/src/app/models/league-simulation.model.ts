export interface LeagueTeamForecast {
  teamId: number;
  teamName: string;
  isOwnTeam: boolean;
  currentPosition: number;
  matches: number;
  goalsFor: number;
  goalsAgainst: number;
  points: number;
  expectedPoints: number;
  expectedPosition: number;
  winLeagueProbability: number;
  positionProbabilities: number[];
  ratingsSource: string;
}

export interface LeagueSimulationReport {
  leagueLevelUnitId: number;
  leagueName: string;
  remainingMatches: number;
  iterations: number;
  fromFirstRound: boolean;
  teams: LeagueTeamForecast[];
}
