export interface ScoutTeamRatings {
  midfieldRating: number;
  rightDefenseRating: number;
  centralDefenseRating: number;
  leftDefenseRating: number;
  rightAttackRating: number;
  centralAttackRating: number;
  leftAttackRating: number;
  indirectSetPiecesAttRating: number;
  indirectSetPiecesDefRating: number;
}

export interface ScoutMatchSummary {
  matchId: number;
  matchDate: string | null;
  isHomeMatch: boolean;
  opponent: string;
  goalsFor: number;
  goalsAgainst: number;
  formation: string;
  tactic: string;
  ratings: ScoutTeamRatings;
}

export interface ScoutLikelyStarter {
  slot: string;
  playerId: number;
  playerName: string;
  appearances: number;
}

export interface OpponentScoutReport {
  teamId: number;
  matchesAnalyzed: number;
  mostCommonFormation: string;
  formationCounts: { [formation: string]: number };
  mostCommonTactic: string;
  tacticCounts: { [tactic: string]: number };
  weightedRatings: ScoutTeamRatings;
  matches: ScoutMatchSummary[];
  likelyStarters: ScoutLikelyStarter[];
}
