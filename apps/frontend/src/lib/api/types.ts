// Wire types for the Bosun backend (camelCase JSON, ISO UTC timestamps). Mirrors Bosun.Application DTOs.

export type SignalType = 'AI' | 'AO' | 'DI' | 'DO';
export type SafetyClass = 'NONE' | 'SIL' | 'ESD';
export type AlarmLevel = 'LL' | 'L' | 'H' | 'HH';
export const ALARM_LEVELS: AlarmLevel[] = ['HH', 'H', 'L', 'LL'];
export const SAFETY_CLASSES: SafetyClass[] = ['NONE', 'SIL', 'ESD'];
export const SIGNAL_TYPES: SignalType[] = ['AI', 'AO', 'DI', 'DO'];

export type ChangeSetStatus = 'draft' | 'validated' | 'approved' | 'applied' | 'rejected';
export type ChangeSetSource = 'manual' | 'extraction' | 'agent';
export type Severity = 'info' | 'warning' | 'error';

export interface Session {
  username: string;
  roles: string[];
}

export interface Health {
  status: string;
  environment?: string;
  databaseProvider?: string;
  databaseOk: boolean;
  version?: string;
}

export interface Project {
  id: number;
  vesselId: number;
  name: string;
  vesselName: string;
  imoNumber: string | null;
  description: string | null;
  tagCount: number;
  version: number;
}

export interface AlarmLimit {
  level: AlarmLevel;
  value: number;
  delayS: number;
  hysteresis: number;
}

export interface AlarmLimitValue {
  value: number;
  delayS: number;
  hysteresis: number;
}

export interface Tag {
  id: number;
  projectId: number;
  tagName: string;
  description: string | null;
  system: string;
  equipment: string;
  signalType: SignalType;
  unit: string | null;
  rangeLow: number | null;
  rangeHigh: number | null;
  safetyClass: SafetyClass;
  alarmGroupId: number | null;
  alarmGroup: string | null;
  channelId: number | null;
  channel: string | null;
  alarmLimits: AlarmLimit[];
  hasAlarm: boolean;
  version: number;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface TagFacets {
  systems: string[];
  units: string[];
  signalTypes: SignalType[];
  safetyClasses: SafetyClass[];
  alarmGroups: string[];
}

export interface AlarmGroup {
  id: number;
  name: string;
  description: string | null;
  tagCount: number;
}

export type UpdateField =
  'description' | 'unit' | 'signalType' | 'rangeLow' | 'rangeHigh' | 'safetyClass';

export interface UpdateTagOp {
  type: 'updateTag';
  tagName: string;
  field: UpdateField;
  old?: string | null;
  new: string | null;
}
export interface SetAlarmLimitOp {
  type: 'setAlarmLimit';
  tagName: string;
  level: AlarmLevel;
  old?: AlarmLimitValue | null;
  new: AlarmLimitValue | null;
}
export interface AssignGroupOp {
  type: 'assignGroup';
  tagName: string;
  old?: string | null;
  new: string | null;
}
export interface CreateTagOp {
  type: 'createTag';
  tagName: string;
  description?: string | null;
  signalType: SignalType;
  unit?: string | null;
  rangeLow?: number | null;
  rangeHigh?: number | null;
  safetyClass?: SafetyClass;
  alarmGroup?: string | null;
}
export interface BindChannelOp {
  type: 'bindChannel';
  tagName: string;
  old?: number | null;
  new: number | null;
}
export interface DeleteTagOp {
  type: 'deleteTag';
  tagName: string;
  old?: unknown;
}
export type ChangeOp =
  UpdateTagOp | SetAlarmLimitOp | AssignGroupOp | CreateTagOp | BindChannelOp | DeleteTagOp;

export interface ChangeSetSummary {
  id: number;
  projectId: number;
  title: string;
  source: ChangeSetSource;
  status: ChangeSetStatus;
  createdBy: string;
  createdAt: string;
  opCount: number;
  requiresSecondApprover: boolean;
  approvedBy: string | null;
  secondApprovedBy: string | null;
  appliedAt: string | null;
  version: number;
}

export interface ChangeSet {
  id: number;
  projectId: number;
  title: string;
  source: ChangeSetSource;
  status: ChangeSetStatus;
  createdBy: string;
  createdAt: string;
  ops: ChangeOp[];
  requiresSecondApprover: boolean;
  pendingSecondApproval: boolean;
  approvedBy: string | null;
  approvedAt: string | null;
  secondApprovedBy: string | null;
  secondApprovedAt: string | null;
  appliedBy: string | null;
  appliedAt: string | null;
  rejectedBy: string | null;
  rejectedAt: string | null;
  rejectReason: string | null;
  model: string | null;
  promptVersion: string | null;
  agentRunId: number | null;
  version: number;
}

export interface Violation {
  code: string;
  severity: Severity;
  tagName: string | null;
  field: string | null;
  message: string;
}

export interface ValidationResult {
  changeSetId: number;
  status: ChangeSetStatus;
  valid: boolean;
  requiresSecondApprover: boolean;
  violations: Violation[];
}

export interface DiffRow {
  index: number;
  type: string;
  tagName: string;
  field: string | null;
  old: string | null;
  new: string | null;
  op: ChangeOp;
}

export interface Diff {
  changeSetId: number;
  status: ChangeSetStatus;
  requiresSecondApprover: boolean;
  rows: DiffRow[];
  violations: Violation[];
}

export interface AuditEntry {
  id: number;
  projectId: number;
  /** Always 'user' (the human on whose authority it was written); see proposedBy for the agent. */
  actorType: 'user' | 'agent';
  proposedBy: 'user' | 'agent';
  actor: string;
  approvedBy: string | null;
  model: string | null;
  promptVersion: string | null;
  agentRunId: number | null;
  changeSetId: number | null;
  tagName: string | null;
  opType: string;
  diff: unknown;
  timestamp: string;
}
