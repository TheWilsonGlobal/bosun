import { useEffect, useReducer, useState } from 'react';
import { ApiError } from '@/lib/api/client';
import { fetchRun } from './api';
import { createSignalRHub, type AgentHub, type CreateHub } from './hub';
import { initialRunState, runReducer, type RunState } from './runState';
import { isTerminal } from './types';

export type Transport = 'idle' | 'connecting' | 'live' | 'polling';

export interface UseAgentRunOptions {
  createHub?: CreateHub;
  pollMs?: number;
}

export interface UseAgentRun {
  state: RunState;
  transport: Transport;
  /** The last failed poll, if any (shown as "connection problem"; polling keeps going). */
  error: unknown;
  loading: boolean;
}

/**
 * Follows one run. A live run is joined over SignalR (snapshot + events); if the hub cannot be reached, or drops for
 * good, the run is polled with GET /api/agent/runs/{id}. A finished run is fetched once.
 */
export function useAgentRun(
  runId: number | null,
  live: boolean,
  { createHub = createSignalRHub, pollMs = 1500 }: UseAgentRunOptions = {},
): UseAgentRun {
  const [state, dispatch] = useReducer(runReducer, initialRunState);
  const [transport, setTransport] = useState<Transport>('idle');
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    dispatch({ type: 'reset' });
    if (runId === null) return;

    let stopped = false;
    let finished = false;
    let hub: AgentHub | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let polling = false;

    const snapshot = async () => {
      const run = await fetchRun(runId);
      if (stopped) return;
      finished = isTerminal(run.status);
      dispatch({ type: 'snapshot', run });
      setError(null);
    };

    const poll = async () => {
      try {
        await snapshot();
      } catch (e) {
        if (stopped) return;
        setError(e);
        if (e instanceof ApiError && e.status === 404) return; // the run does not exist: nothing to wait for
      }
      if (!stopped && !finished) timer = setTimeout(() => void poll(), pollMs);
    };

    const startPolling = () => {
      if (polling || stopped || finished) return;
      polling = true;
      setTransport('polling');
      void poll();
    };

    const join = async (h: AgentHub) => {
      const run = await h.join(runId);
      if (stopped) return;
      finished = isTerminal(run.status);
      dispatch({ type: 'snapshot', run });
    };

    const start = async () => {
      setError(null);
      if (!live) {
        setTransport('idle');
        try {
          await snapshot();
        } catch (e) {
          if (!stopped) setError(e);
        }
        return;
      }
      setTransport('connecting');
      try {
        const h = await createHub({
          step: (e) => e.runId === runId && dispatch({ type: 'step', event: e }),
          status: (e) => e.runId === runId && dispatch({ type: 'status', event: e }),
          done: (e) => {
            if (e.runId !== runId) return;
            finished = true;
            dispatch({ type: 'done', event: e });
            // Best effort: the final counters (tool calls, tokens, elapsed) are only in the REST representation.
            void fetchRun(runId)
              .then((run) => !stopped && dispatch({ type: 'snapshot', run }))
              .catch(() => undefined);
          },
          reconnected: () => {
            if (hub) join(hub).catch(startPolling);
          },
          closed: () => startPolling(),
        });
        if (stopped) {
          void h.stop();
          return;
        }
        hub = h;
        await join(h);
        if (!stopped) setTransport(finished ? 'idle' : 'live');
      } catch {
        // Hub unreachable (proxy without ws, 401 on negotiate, run unknown to the hub ...): fall back to polling.
        void hub?.stop();
        hub = null;
        startPolling();
      }
    };

    void start();
    return () => {
      stopped = true;
      clearTimeout(timer);
      void hub?.stop();
    };
  }, [runId, live, createHub, pollMs]);

  const loading = runId !== null && state.run === null && error === null;
  return { state, transport: runId === null ? 'idle' : transport, error, loading };
}
