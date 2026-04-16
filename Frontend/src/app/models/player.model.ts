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
  defending: number;
  playmaking: number;
  winger: number;
  passing: number;
  scoring: number;
  setPieces: number;
}
