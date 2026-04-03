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
