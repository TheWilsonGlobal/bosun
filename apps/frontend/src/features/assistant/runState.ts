import type {
  AgentDoneEvent,
  AgentRun,
  AgentStatusEvent,
  AgentStep,
  AgentStepEvent,
} from './types';
import { isTerminal } from './types';

// The live state of one run, fed by the hub snapshot (JoinRun), live events, and the polling fallback. Steps are keyed by
// `seq`: the snapshot and the events overlap, so every source can deliver the same step and it still shows once.

export interface RunState {
  run: AgentRun | null;
  steps: AgentStep[];
}

export type RunAction =
  | { type: 'reset' }
  | { type: 'snapshot'; run: AgentRun }
  | { type: 'step'; event: AgentStepEvent }
  | { type: 'status'; event: AgentStatusEvent }
  | { type: 'done'; event: AgentDoneEvent };

export const initialRunState: RunState = { run: null, steps: [] };

function mergeSteps(existing: AgentStep[], incoming: readonly AgentStep[]): AgentStep[] {
  const bySeq = new Map(existing.map((s) => [s.seq, s]));
  let changed = false;
  for (const s of incoming) {
    if (!bySeq.has(s.seq)) {
      const { seq, kind, payload, tokens, latencyMs, createdAt } = s;
      bySeq.set(seq, { seq, kind, payload, tokens, latencyMs, createdAt });
      changed = true;
    }
  }
  return changed ? [...bySeq.values()].sort((a, b) => a.seq - b.seq) : existing;
}

export function runReducer(state: RunState, action: RunAction): RunState {
  switch (action.type) {
    case 'reset':
      return initialRunState;
    case 'snapshot': {
      const { steps = [], ...rest } = action.run;
      const merged = mergeSteps(state.steps, steps);
      // A late or stale snapshot must never un-finish a run that a 'done' event already closed.
      if (state.run && isTerminal(state.run.status) && !isTerminal(rest.status)) {
        return { run: state.run, steps: merged };
      }
      return { run: rest, steps: merged };
    }
    case 'step':
      if (state.steps.some((s) => s.seq === action.event.seq)) return state;
      return { ...state, steps: mergeSteps(state.steps, [action.event]) };
    case 'status':
      if (!state.run || isTerminal(state.run.status)) return state;
      return {
        ...state,
        run: { ...state.run, status: action.event.status, phase: action.event.phase },
      };
    case 'done': {
      if (!state.run) return state;
      const e = action.event;
      return {
        ...state,
        run: {
          ...state.run,
          status: e.status,
          phase: null,
          changeSetId: e.changeSetId,
          resultKind: e.resultKind,
          resultMessage: e.resultMessage,
          errorCode: e.errorCode,
          errorMessage: e.errorMessage,
        },
      };
    }
  }
}

const isToolCall = (s: AgentStep) =>
  s.kind === 'tool_call' && (s.payload as { name?: unknown } | null)?.name !== 'finish_task';

/** Budgets as far as the stream knows them: the larger of the server's counters and what the steps add up to. */
export function liveBudgets(state: RunState) {
  const run = state.run;
  return {
    toolCalls: Math.max(run?.toolCalls ?? 0, state.steps.filter(isToolCall).length),
    maxToolCalls: run?.maxToolCalls ?? 12,
    tokens: Math.max(
      run?.tokens.total ?? 0,
      state.steps.reduce((sum, s) => sum + s.tokens, 0),
    ),
    maxTokens: run?.maxTotalTokens ?? 0,
  };
}
