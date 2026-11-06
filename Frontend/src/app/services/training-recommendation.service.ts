import { Injectable } from '@angular/core';
import { Player } from '../models/player.model';
import { TrainingAssignment, TrainingMatch, trainingFactor } from './weekly-plan.service';
import { TRAINING_FORMATIONS } from './training-formations';

export { TRAINING_FORMATIONS } from './training-formations';

export type TrainingSkill = 'keeper' | 'defending' | 'playmaking' | 'winger' | 'passing' | 'scoring' | 'setPieces';
export interface TrainingOption { code: number; name: string; skill: TrainingSkill; }

/** Only training types with quantified primary positions in trainingFactor. */
export const TRAINING_OPTIONS: TrainingOption[] = [
  { code: 2, name: 'SetPieces', skill: 'setPieces' },
  { code: 3, name: 'Defending', skill: 'defending' },
  { code: 4, name: 'Scoring', skill: 'scoring' },
  { code: 5, name: 'Crossing', skill: 'winger' },
  { code: 7, name: 'ShortPasses', skill: 'passing' },
  { code: 8, name: 'Playmaking', skill: 'playmaking' },
  { code: 9, name: 'Goalkeeping', skill: 'keeper' },
  { code: 10, name: 'ThroughPasses', skill: 'passing' },
  { code: 12, name: 'WingAttacks', skill: 'winger' }
];

export interface RecommendedTrainee extends TrainingAssignment {
  playerName: string;
  age: number;
  skillValue: number;
  factor: 1 | 0.5;
  roleSkill: TrainingSkill;
  roleSkillValue: number | null;
  reasons: string[];
}

export interface FormationTrainingRecommendation {
  formation: string;
  trainingTypeCode: number;
  trainingTypeName: string;
  skill: TrainingSkill;
  fullSlotsPerMatch: number;
  halfSlotsPerMatch: number;
  fullCapacity: number;
  halfCapacity: number;
  fullPlayerCount: number;
  halfPlayerCount: number;
  trainees: RecommendedTrainee[];
}

const ROLE_SKILLS: TrainingSkill[] = ['keeper', 'defending', 'playmaking', 'winger', 'scoring'];

/** Prospective rotation suggestions, independent of this week's recorded minutes.
 * Counts are 90-minute training places, not a comparison of skill gain or value.
 * The age/role score is an explainable selection heuristic, not Hattrick's formula.
 */
@Injectable({ providedIn: 'root' })
export class TrainingRecommendationService {
  recommend(formationName: string, roster: Player[], matches: TrainingMatch[] = ['league', 'friendly']): FormationTrainingRecommendation[] {
    const formation = TRAINING_FORMATIONS.find(item => item.name === formationName);
    if (!formation) return [];
    const games = [...new Set(matches)].filter(match => match === 'league' || match === 'friendly');
    const eligible = [...new Map(roster.filter(player => player.playerId > 0 && Number.isInteger(player.playerId)
      && !(player.injuryLevel > 0) && !player.isSuspended && player.canOptimize !== false
      && player.skillsAvailable !== false).map(player => [player.playerId, player])).values()];

    return TRAINING_OPTIONS.map(option => {
      const fullSlots = formation.slots.filter(slot => trainingFactor(option.code, slot) === 1);
      const halfSlots = formation.slots.filter(slot => trainingFactor(option.code, slot) === 0.5);
      const remaining = eligible.filter(player => this.skillValue(player, option.skill) !== null);
      const trainees: RecommendedTrainee[] = [];
      // Allocate all 100% places across both games before any 50% places.
      for (const factor of [1, 0.5] as const) {
        const slots = factor === 1 ? fullSlots : halfSlots;
        const places = games.flatMap(match => slots.map(slot => ({ match, slot })));
        while (places.length && remaining.length) {
          let bestPlayerIndex = 0, bestPlaceIndex = 0, bestScore = -Infinity;
          for (let placeIndex = 0; placeIndex < places.length; placeIndex++) {
            for (let playerIndex = 0; playerIndex < remaining.length; playerIndex++) {
              const player = remaining[playerIndex];
              const score = this.selectionScore(player, places[placeIndex].slot);
              if (score > bestScore || (score === bestScore && player.playerId < remaining[bestPlayerIndex].playerId)) {
                bestPlayerIndex = playerIndex; bestPlaceIndex = placeIndex; bestScore = score;
              }
            }
          }
          const player = remaining.splice(bestPlayerIndex, 1)[0];
          const place = places.splice(bestPlaceIndex, 1)[0];
          const roleSkill = this.roleSkill(place.slot);
          const roleSkillValue = this.skillValue(player, roleSkill);
          const reasons = ['developSkill'];
          if (Number.isFinite(player.age) && player.age >= 17 && player.age <= 21) reasons.push('young');
          if (this.roleFit(player, roleSkill) >= 0.8) reasons.push('roleFit');
          if (player.injuryStatusKnown !== true || player.suspensionStatusKnown !== true) reasons.push('unknownAvailability');
          trainees.push({ ...place, playerId: player.playerId, startMinute: 0, minutes: 90,
            playerName: `${player.firstName} ${player.lastName}`.trim(), age: player.age,
            skillValue: this.skillValue(player, option.skill)!, factor, roleSkill, roleSkillValue, reasons });
        }
      }
      return {
        formation: formation.name, trainingTypeCode: option.code, trainingTypeName: option.name, skill: option.skill,
        fullSlotsPerMatch: fullSlots.length, halfSlotsPerMatch: halfSlots.length,
        fullCapacity: fullSlots.length * games.length, halfCapacity: halfSlots.length * games.length,
        fullPlayerCount: trainees.filter(player => player.factor === 1).length,
        halfPlayerCount: trainees.filter(player => player.factor === 0.5).length,
        trainees: trainees.sort((a, b) => games.indexOf(a.match) - games.indexOf(b.match)
          || b.factor - a.factor || formation.slots.indexOf(a.slot) - formation.slots.indexOf(b.slot))
      };
    }).sort((a, b) => b.fullPlayerCount - a.fullPlayerCount || b.halfPlayerCount - a.halfPlayerCount
      || b.fullCapacity - a.fullCapacity || b.halfCapacity - a.halfCapacity || a.trainingTypeCode - b.trainingTypeCode);
  }

  private skillValue(player: Player, skill: TrainingSkill): number | null {
    const value = player.skills?.[skill];
    return player.skills?.[`${skill}Available`] !== false && Number.isFinite(value) && value >= 0 && value <= 20
      ? value : null;
  }

  private roleSkill(slot: string): TrainingSkill {
    if (slot === 'GK') return 'keeper';
    if (['RWB', 'LWB', 'RCD', 'CD', 'LCD'].includes(slot)) return 'defending';
    if (['RW', 'LW'].includes(slot)) return 'winger';
    if (['RIM', 'IM', 'LIM'].includes(slot)) return 'playmaking';
    return 'scoring';
  }

  private roleFit(player: Player, roleSkill: TrainingSkill): number {
    const values = ROLE_SKILLS.map(skill => this.skillValue(player, skill));
    const role = this.skillValue(player, roleSkill);
    // Do not claim this is a player's strongest role when comparison skills are missing.
    if (role === null || values.some(value => value === null)) return 0;
    return role / Math.max(1, ...values as number[]);
  }

  private selectionScore(player: Player, slot: string): number {
    const role = this.roleSkill(slot);
    const agePreference = Number.isFinite(player.age) && player.age >= 17 ? Math.max(0, 30 - player.age) : 0;
    return this.roleFit(player, role) * 12 + agePreference * 1.5 + (this.skillValue(player, role) ?? 0) * 0.5;
  }
}
