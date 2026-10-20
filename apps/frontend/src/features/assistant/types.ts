// Wire types of the agent API (camelCase JSON; enums are the stable snake_case strings of the backend).

export type AgentRunStatus =
  | 'running'
  | 'awaiting_review'
  | 'needs_clarification'
  | 'refused'
  | 'budget_exceeded'
  | 'failed'
  | 'cancelled';

export type AgentStepKind = 'plan' | 'tool_call' | 'tool_result' | 'verify' | 'final' | 'error';

export type AgentResultKind =
  'draft' | 'clarification' | 'refusal' | 'no_changes' | 'budget_exceeded' | 'cancelled';

export interface AgentStep {
  seq: number;
  kind: AgentStepKind;
  /** Shape depends on `kind` (see stepPayloads.ts); the server stores it as free JSON. */
  payload: unknown;
  tokens: number;
  latencyMs: number;
  createdAt: string;
}

export interface AgentTokens {
  prompt: number;
  candidate: number;
  thought: number;
  total: number;
}

export interface AgentRun {
  id: number;
  projectId: number;
  message: string;
  status: AgentRunStatus;
  phase: string | null;
  createdBy: string;
  model: string | null;
  promptVersion: string | null;
  startedAt: string;
  finishedAt: string | null;
  toolCalls: number;
  maxToolCalls: number;
  elapsedMs: number;
  tokens: AgentTokens;
  maxTotalTokens: number;
  changeSetId: number | null;
  resultKind: AgentResultKind | null;
  resultMessage: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  steps?: AgentStep[];
}

export interface AgentStepEvent extends AgentStep {
  runId: number;
}
export interface AgentStatusEvent {
  runId: number;
  status: AgentRunStatus;
  phase: string | null;
}
export interface AgentDoneEvent {
  runId: number;
  changeSetId: number | null;
  status: AgentRunStatus;
  resultKind: AgentResultKind | null;
  resultMessage: string | null;
  errorCode: string | null;
  errorMessage: string | null;
}

export const isTerminal = (s: AgentRunStatus) => s !== 'running';

export const MAX_MESSAGE_LENGTH = 2000;

export const DEMO_INSTRUCTION =
  'Add high-temperature alarms at 95 °C with a 10 s delay to all main engine exhaust gas temperature sensors, and put them in alarm group ME-EXH.';
