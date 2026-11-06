export interface TrainingFormation {
  name: string;
  slots: string[];
}

/** The legal position sets used by Backend/Services/FormationData.cs. */
export const TRAINING_FORMATIONS: TrainingFormation[] = [
  { name: '5-5-0', slots: 'GK RWB RCD CD LCD LWB RW RIM IM LIM LW'.split(' ') },
  { name: '5-4-1', slots: 'GK RWB RCD CD LCD LWB RW RIM LIM LW FW'.split(' ') },
  { name: '5-3-2', slots: 'GK RWB RCD CD LCD LWB RIM IM LIM RFW LFW'.split(' ') },
  { name: '4-5-1', slots: 'GK RWB RCD LCD LWB RW RIM IM LIM LW FW'.split(' ') },
  { name: '4-4-2', slots: 'GK RWB RCD LCD LWB RW RIM LIM LW RFW LFW'.split(' ') },
  { name: '4-3-3', slots: 'GK RWB RCD LCD LWB RW IM LW RFW FW LFW'.split(' ') },
  { name: '3-5-2', slots: 'GK RCD CD LCD RW RIM IM LIM LW RFW LFW'.split(' ') },
  { name: '3-4-3', slots: 'GK RCD CD LCD RW RIM LIM LW RFW FW LFW'.split(' ') },
  { name: '2-5-3', slots: 'GK RCD LCD RW RIM IM LIM LW RFW FW LFW'.split(' ') },
  { name: '5-2-3', slots: 'GK RWB RCD CD LCD LWB RW LW RFW FW LFW'.split(' ') }
];
