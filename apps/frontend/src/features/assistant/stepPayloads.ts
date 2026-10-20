import type { Violation } from '@/lib/api/types';

// Payload shapes of the persisted agent steps, as written by Bosun.Agent/AgentRunner.cs. The server stores them as free
// JSON, so every reader here is defensive: a missing or mistyped member renders as absent, never throws.

type Obj = Record<string, unknown>;
const obj = (v: unknown): Obj =>
  v && typeof v === 'object' && !Array.isArray(v) ? (v as Obj) : {};
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null);
const strings = (v: unknown): string[] =>
  Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [];

export interface PlanPayload {
  decision: string | null;
  message: string | null;
  title: string | null;
  steps: string[];
  affectedScope: string | null;
  risks: string[];
  model: string | null;
}
export const planOf = (p: unknown): PlanPayload => {
  const o = obj(p);
  return {
    decision: str(o.decision),
    message: str(o.message),
    title: str(o.title),
    steps: strings(o.steps),
    affectedScope: str(o.affected_scope) ?? str(o.affectedScope),
    risks: strings(o.risks),
    model: str(o.model),
  };
};

export interface ToolCallPayload {
  name: string;
  args: unknown;
}
export const toolCallOf = (p: unknown): ToolCallPayload => {
  const o = obj(p);
  return { name: str(o.name) ?? '(unknown tool)', args: o.args ?? null };
};

export interface ToolResultPayload {
  name: string;
  ok: boolean;
  response: unknown;
  /** The tool's error message when it failed (response.error.message). */
  errorMessage: string | null;
}
export const toolResultOf = (p: unknown): ToolResultPayload => {
  const o = obj(p);
  const response = o.response ?? null;
  const err = obj(obj(response).error);
  return {
    name: str(o.name) ?? '(unknown tool)',
    ok: o.ok === true,
    response,
    errorMessage: str(err.message) ?? str(err.code),
  };
};

const toViolation = (v: unknown): Violation => {
  const o = obj(v);
  const sev = str(o.severity);
  return {
    code: str(o.code) ?? 'unknown',
    severity: sev === 'error' || sev === 'warning' || sev === 'info' ? sev : 'info',
    tagName: str(o.tag_name) ?? str(o.tagName),
    field: str(o.field),
    message: str(o.message) ?? '',
  };
};
const violationsOf = (v: unknown): Violation[] => (Array.isArray(v) ? v.map(toViolation) : []);

export interface VerifyPayload {
  round: number;
  valid: boolean;
  errorCount: number;
  requiresSecondApprover: boolean;
  violations: Violation[];
}
export const verifyOf = (p: unknown): VerifyPayload => {
  const o = obj(p);
  return {
    round: typeof o.round === 'number' ? o.round : 0,
    valid: o.valid === true,
    errorCount: typeof o.error_count === 'number' ? o.error_count : 0,
    requiresSecondApprover: o.requires_second_approver === true,
    violations: violationsOf(o.violations),
  };
};

export interface FinalPayload {
  kind: string | null;
  status: string | null;
  message: string | null;
  changeSetId: number | null;
  opCount: number;
  errorCode: string | null;
  violations: Violation[];
}
export const finalOf = (p: unknown): FinalPayload => {
  const o = obj(p);
  return {
    kind: str(o.kind),
    status: str(o.status),
    message: str(o.message),
    changeSetId: typeof o.change_set_id === 'number' ? o.change_set_id : null,
    opCount: typeof o.op_count === 'number' ? o.op_count : 0,
    errorCode: str(o.error_code),
    violations: violationsOf(o.violations),
  };
};

export interface ErrorPayload {
  code: string | null;
  message: string | null;
}
export const errorOf = (p: unknown): ErrorPayload => {
  const o = obj(p);
  return { code: str(o.code), message: str(o.message) };
};

/** One-line preview of tool arguments for the collapsed row. */
export function previewArgs(args: unknown, max = 80): string {
  if (args === null || args === undefined) return '';
  const text = JSON.stringify(args);
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}
