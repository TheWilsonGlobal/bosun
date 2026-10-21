// Wire types of the monitoring API and /hubs/monitoring (camelCase JSON, stable snake_case enum strings).

export type Quality = 'good' | 'stale';
export type AlarmLevel = 'LL' | 'L' | 'H' | 'HH';
export type AlarmState = 'unacknowledged' | 'acknowledged' | 'returned_unacknowledged' | 'cleared';
export type AlarmEventKind = 'raised' | 'acknowledged' | 'returned_to_normal' | 'cleared';
export type LinkName = 'modbus' | 'nmea';
export type LinkState = 'connected' | 'connecting' | 'disconnected' | 'disabled';
export type AlarmFilter = 'active' | 'all' | 'cleared';

export interface LiveValue {
  tag: string;
  value: number;
  unit: string | null;
  quality: Quality;
  timestamp: string;
}

export interface ValuesBatch {
  timestamp: string;
  values: LiveValue[];
}

export interface Alarm {
  id: number;
  projectId: number;
  tag: string;
  level: AlarmLevel;
  limitValue: number;
  delayS: number;
  hysteresis: number;
  unit: string | null;
  description: string | null;
  state: AlarmState;
  raisedAt: string;
  raisedValue: number;
  peakValue: number;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  returnedAt: string | null;
  returnedValue: number | null;
  clearedAt: string | null;
}

export interface AlarmNotification {
  kind: AlarmEventKind;
  alarm: Alarm;
  at: string;
  value: number | null;
}

export interface Connection {
  name: LinkName;
  state: LinkState;
  since: string;
  lastError: string | null;
  attempts: number;
  endpoint: string | null;
}

export interface Navigation {
  valid: boolean;
  latitude: number | null;
  longitude: number | null;
  speedKnots: number | null;
  courseDeg: number | null;
  headingDeg: number | null;
  timestamp: string | null;
}

export interface MonitoringStatus {
  enabled: boolean;
  projectId: number;
  modbus: Connection;
  nmea: Connection;
  tagCount: number;
  activeAlarms: number;
  unacknowledgedAlarms: number;
  lastValueAt: string | null;
}

export interface Snapshot {
  status: MonitoringStatus;
  values: LiveValue[];
  navigation: Navigation | null;
  alarms: Alarm[];
}

export interface AlarmList {
  items: Alarm[];
  active: number;
  unacknowledged: number;
}

// ---- mimic document (GET /api/projects/{id}/mimic) ----

export type ShapeRole =
  'frame' | 'panel' | 'equipment' | 'cylinder' | 'exhaust' | 'pipe' | 'caption' | 'title' | 'label';

export interface MimicShape {
  id: string;
  type: 'line' | 'rect' | 'text' | 'circle';
  role: ShapeRole;
  label?: string;
  text?: string;
  fontSize?: number;
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  rx?: number;
  x1?: number;
  y1?: number;
  x2?: number;
  y2?: number;
  cx?: number;
  cy?: number;
  r?: number;
}

export type BindingKind = 'value' | 'bar' | 'tank' | 'navigation';

export interface MimicBinding {
  id: string;
  kind: BindingKind;
  tag?: string;
  label: string;
  x: number;
  y: number;
  width: number;
  height: number;
  decimals?: number;
  group?: string;
  showLimits?: boolean;
  orientation?: 'horizontal' | 'vertical';
  source?: string;
}

export interface MimicTagInfo {
  unit: string | null;
  rangeLow: number | null;
  rangeHigh: number | null;
  description: string | null;
  safetyClass: string;
}

export interface MimicDoc {
  version: number;
  title: string;
  description?: string;
  viewBox: { width: number; height: number };
  shapes: MimicShape[];
  bindings: MimicBinding[];
  projectId: number;
  tags: Record<string, MimicTagInfo>;
  missingTags: string[];
}
