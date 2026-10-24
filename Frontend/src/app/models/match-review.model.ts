import { LineupRatings } from './lineup.model';

export type Sector = 'midfield' | 'rightDefense' | 'centralDefense' | 'leftDefense' | 'rightAttack' | 'centralAttack' | 'leftAttack';
export interface CalibrationMatchContext {
  matchId?: number | null;
  matchDate?: string | null;
  isHomeMatch?: boolean | null;
  tactic?: string | null;
  attitude?: string | null;
  coachType?: string | null;
  teamSpiritLevel?: number | null;
  confidenceLevel?: number | null;
  formation?: string | null;
  formationExperience?: number | null;
  weatherId?: number | null;
  assistantManagerLevel?: number | null;
  playerIdsBySlot: Record<string, number>;
  behavioursBySlot: Record<string, string>;
  predictedRatings?: LineupRatings | null;
  modelVersion?: string | null;
}
export interface CalibrationSnapshot {
  teamId: number;
  recordedAt: string;
  source: string;
  context: CalibrationMatchContext;
  warnings: string[];
  isComplete: boolean;
  missingFields: string[];
  predictedRatings?: LineupRatings | null;
  modelVersion?: string | null;
  predictionSource: string;
}
export interface CaptureResult {
  saved: boolean;
  complete: boolean;
  missingFields: string[];
  warnings: string[];
  snapshot: CalibrationSnapshot | null;
}
export interface CalibrationMatch {
  matchId: number;
  matchDate: string | null;
  isHomeMatch: boolean;
  tactic: string;
  playersMatched: number;
  predicted: LineupRatings;
  actual: LineupRatings;
  recordedAt: string;
  modelVersion: string | null;
  predictionSource: string;
}
export interface CalibrationReport {
  teamId: number;
  matches: CalibrationMatch[];
  evaluationStatus: string;
  excludedMatches: number;
  trainingSampleCount: number;
  heldOutSampleCount: number;
  savedForecastCount: number;
  replayedForecastCount: number;
  modelVersions: string[];
  meanAbsoluteError: LineupRatings | null;
  heldOutMeanAbsoluteError: LineupRatings | null;
  meanBias: LineupRatings | null;
  heldOutMeanBias: LineupRatings | null;
  trainingSectorSampleCounts: Record<string, number>;
  heldOutSectorSampleCounts: Record<string, number>;
  exclusionReasons: string[];
}
