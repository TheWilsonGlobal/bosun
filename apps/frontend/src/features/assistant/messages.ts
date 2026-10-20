import type { AgentRun } from './types';

// Plan s7.8: every terminal status and every stable error code has its own message, never a generic failure.

export type Tone = 'success' | 'info' | 'warning' | 'error';

export interface Outcome {
  tone: Tone;
  title: string;
  message: string;
}

/** Messages for run `errorCode`s (budget_* belong to the budget_exceeded status, the rest to `failed`). */
export const ERROR_MESSAGES: Record<string, Outcome> = {
  budget_tool_calls: {
    tone: 'warning',
    title: 'Tool-call budget used up',
    message:
      'The assistant used all of its tool calls before it finished. Narrow the request (one system or one kind of change at a time) and run it again.',
  },
  budget_wall_clock: {
    tone: 'warning',
    title: 'Time budget used up',
    message:
      'The assistant ran out of its 60 second working time. Narrow the request and run it again.',
  },
  budget_tokens: {
    tone: 'warning',
    title: 'Token budget used up',
    message:
      'The assistant used its whole token allowance. Narrow the request or split it into smaller ones.',
  },
  llm_rate_limited: {
    tone: 'error',
    title: 'Model is rate limited',
    message:
      'The language model refused the call because the quota or rate limit is exhausted. Wait a minute and run the request again.',
  },
  llm_auth_failed: {
    tone: 'error',
    title: 'Model rejected the API key',
    message:
      'The language model did not accept the configured API key. An administrator must check the backend configuration; retrying will not help.',
  },
  llm_safety_blocked: {
    tone: 'error',
    title: 'Blocked by the model safety filter',
    message:
      'The language model refused to process this request or its content. Rephrase the request without unusual wording and try again.',
  },
  llm_malformed_output: {
    tone: 'error',
    title: 'Model answered in an unusable format',
    message:
      'The model returned output the assistant could not read, even after a repair attempt. Running the request again usually works.',
  },
  llm_timeout: {
    tone: 'error',
    title: 'Model timed out',
    message: 'The language model did not answer in time. Try again in a moment.',
  },
  llm_unavailable: {
    tone: 'error',
    title: 'Model unavailable',
    message:
      'The language model service could not be reached or reported an outage. Try again later.',
  },
  llm_truncated: {
    tone: 'error',
    title: 'Model answer was cut off',
    message:
      'The model hit its output limit mid-answer. Ask for a smaller change (fewer tags at a time).',
  },
  llm_bad_request: {
    tone: 'error',
    title: 'Model rejected the request',
    message:
      'The language model reported the request as invalid. This is a bug in the assistant, not in your text; report it with the run number.',
  },
  llm_not_configured: {
    tone: 'error',
    title: 'Assistant is not configured',
    message:
      'No language model is configured on the backend (LLM provider or API key missing). An administrator must set it up.',
  },
  internal_error: {
    tone: 'error',
    title: 'Assistant crashed',
    message:
      'The run failed unexpectedly on the server. It was logged there; nothing was applied. Try again.',
  },
  server_restarted: {
    tone: 'error',
    title: 'Server restarted during the run',
    message:
      'The backend restarted while this run was in progress, so it was stopped. Nothing was applied; run the request again.',
  },
};

export const ERROR_CODES = Object.keys(ERROR_MESSAGES);

export function describeErrorCode(code: string | null, detail: string | null = null): Outcome {
  if (code && ERROR_MESSAGES[code]) return ERROR_MESSAGES[code];
  return {
    tone: 'error',
    title: `Run failed (${code ?? 'no error code'})`,
    message: detail || 'The backend reported an error code this version of the UI does not know.',
  };
}

/** The headline for a terminal run. `running` has none. */
export function describeOutcome(run: AgentRun): Outcome | null {
  const hasDraft = run.changeSetId !== null;
  const detail = run.resultMessage?.trim() || null;
  switch (run.status) {
    case 'running':
      return null;
    case 'awaiting_review':
      return {
        tone: 'success',
        title: 'Draft ready for review',
        message: `${detail ?? 'The assistant built a draft change set.'} Nothing has been applied: a person must review and approve it.`,
      };
    case 'needs_clarification':
      return {
        tone: 'info',
        title: 'The assistant needs more information',
        message: `${detail ?? 'The request was not specific enough.'} Answer by sending a new request that includes the missing detail.`,
      };
    case 'refused':
      return run.resultKind === 'no_changes'
        ? {
            tone: 'info',
            title: 'Nothing to change',
            message: `${detail ?? 'The assistant found nothing to change.'} No draft was created.`,
          }
        : {
            tone: 'warning',
            title: 'The assistant declined this request',
            message: detail ?? 'The request was refused as impossible or not allowed.',
          };
    case 'budget_exceeded': {
      const base = describeErrorCode(run.errorCode ?? 'budget_tokens', run.errorMessage);
      return {
        ...base,
        message: `${base.message} ${
          hasDraft
            ? 'A partial draft was kept; review it carefully, it may be incomplete.'
            : 'No draft was created.'
        }`,
      };
    }
    case 'failed':
      return describeErrorCode(run.errorCode, run.errorMessage);
    case 'cancelled':
      return {
        tone: 'info',
        title: 'Run cancelled',
        message: hasDraft
          ? 'You stopped the run. A partial draft was kept; review it carefully, it may be incomplete.'
          : 'You stopped the run. No draft was created.',
      };
  }
}

export const PHASE_LABELS: Record<string, string> = {
  planning: 'Planning',
  acting: 'Working (calling tools)',
  verifying: 'Verifying the draft against the rules',
};

export const STATUS_LABELS: Record<string, string> = {
  running: 'Running',
  awaiting_review: 'Awaiting review',
  needs_clarification: 'Needs clarification',
  refused: 'Refused',
  budget_exceeded: 'Budget exceeded',
  failed: 'Failed',
  cancelled: 'Cancelled',
};
