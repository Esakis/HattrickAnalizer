export interface Player {
  playerId: number;
  firstName: string;
  lastName: string;
  age: number;
  tsi: number;
  skills: PlayerSkills;
  form: number;
  stamina: number;
  experience: number;
  loyalty: number;
  leadership: number;
  specialty: string;
  injuryLevel: number;
  injuryStatusKnown?: boolean;
  skillsAvailable?: boolean;
  canOptimize?: boolean;
  isSuspended?: boolean;
  suspensionStatusKnown?: boolean;
  provenance?: PlayerDataProvenance;
  shirtNumber: number;
  // Rozszerzone statystyki
  matchStats?: PlayerMatchStats;
}

export interface PlayerMatchStats {
  totalMatches: number;
  goals: number;
  assists: number;
  yellowCards: number;
  redCards: number;
  averageRating: number;
  averageForm: number;
  goalsPerMatch: number;
  matchesPerGoal: number;
  minutesPlayed: number;
  // Oceny na różnych pozycjach
  positionRatings?: { [position: string]: number };
}

export interface PlayerSkills {
  keeper: number;
  keeperAvailable?: boolean;
  defending: number;
  defendingAvailable?: boolean;
  playmaking: number;
  playmakingAvailable?: boolean;
  winger: number;
  wingerAvailable?: boolean;
  passing: number;
  passingAvailable?: boolean;
  scoring: number;
  scoringAvailable?: boolean;
  setPieces: number;
  setPiecesAvailable?: boolean;
  hasAllSkills?: boolean;
}

export interface PlayerDataProvenance {
  source: string;
  retrievedAt?: string | null;
  warnings?: string[];
  sampleCount?: number;
}
