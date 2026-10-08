export interface TrainingPlayerEntry {
  playerId: number;
  playerName: string;
  age: number;
  slot: string;
  fullTraining: boolean;
  trainedSkillValue: number;
  trainedSkillAvailable?: boolean;
  estimatedWeeksToNextLevel: number | null;
  coverageMinutes?: number;
  observedFullMinutes?: number;
  observedHalfMinutes?: number;
  fullTrainingMinutes?: number;
  halfTrainingMinutes?: number;
  smallEffectMinutes?: number;
  verySmallEffectMinutes?: number;
  setPiecesBonusFullMinutes?: number;
  setPiecesBonusHalfMinutes?: number;
  setPiecesBonusUnknownMinutes?: number;
  setPiecesSpecialBonusMinutes?: number;
  effectiveTrainingMinutes?: number;
  trainingFraction?: number;
  staminaTrainingFraction?: number | null;
  isEstimate?: boolean;
  warnings?: string[];
}

export interface TrainingSummary {
  teamId: number;
  trainingTypeCode: number;
  trainingTypeName: string;
  trainingTypeKnown?: boolean;
  trainedSkill: string;
  trainingLevel: number;
  staminaTrainingPart: number;
  trainerName: string;
  lastMatchId: number;
  lastMatchDate: string | null;
  players: TrainingPlayerEntry[];
  weekStart?: string | null;
  weekEnd?: string | null;
  trainingIntensityPercent?: number | null;
  staminaTrainingSharePercent?: number | null;
  staminaTrainingPartKnown?: boolean;
  /** Raw CHPP coach value; the contract does not confirm its scale. */
  coachSkillRaw?: number | null;
  coachSkillSource?: string | null;
  coachType?: string | null;
  coachTypeSource?: string | null;
  trainerId?: number | null;
  trainerIdentitySource?: string | null;
  assistantSkillTotal?: number | null;
  assistantSkillSource?: string | null;
  coachSpeedMultiplier?: number | null;
  approximateTrainingSpeedMultiplier?: number | null;
  intensityAndStaminaMultiplier?: number | null;
  assistantSpeedMultiplier?: number | null;
  trainingSpeedMethod?: string;
  warnings?: string[];
  source?: string;
  isEstimate?: boolean;
}
