export interface TrainingPlayerEntry {
  playerId: number;
  playerName: string;
  age: number;
  slot: string;
  fullTraining: boolean;
  trainedSkillValue: number;
  estimatedWeeksToNextLevel: number | null;
}

export interface TrainingSummary {
  teamId: number;
  trainingTypeCode: number;
  trainingTypeName: string;
  trainedSkill: string;
  trainingLevel: number;
  staminaTrainingPart: number;
  trainerName: string;
  lastMatchId: number;
  lastMatchDate: string | null;
  players: TrainingPlayerEntry[];
}
