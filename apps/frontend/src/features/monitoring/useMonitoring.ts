import { useEffect, useState } from 'react';
import { fetchAlarms, fetchNavigation, fetchStatus, fetchValues } from './api';
import { createSignalRMonitoringHub, type CreateMonitoringHub, type MonitoringHub } from './hub';
import type { MonitoringStore } from './store';

export type MonitoringTransport = 'connecting' | 'live' | 'polling';

export interface UseMonitoringOptions {
  createHub?: CreateMonitoringHub;
  /** Poll period while the hub is not connected. */
  pollMs?: number;
  /** First delay before the hub is retried after a failed start (doubles to 30 s). */
  retryMs?: number;
  /** Changes when the user signs in/out: the hub needs a session, so it is retried. */
  authKey?: string | null;
}

export interface UseMonitoring {
  transport: MonitoringTransport;
  /** The last failed poll (cleared by the next successful one). */
  error: unknown;
}

/**
 * Feeds the store. Prefers the SignalR hub (Snapshot on connect, then pushed events); while it is not connected,
 * or when it cannot be started (no session, no websocket proxy ...), status, values, navigation and open alarms are
 * polled over REST. The hub is retried with backoff in the background.
 */
export function useMonitoring(
  store: MonitoringStore,
  {
    createHub = createSignalRMonitoringHub,
    pollMs = 2000,
    retryMs = 3000,
    authKey = null,
  }: UseMonitoringOptions = {},
): UseMonitoring {
  const [transport, setTransport] = useState<MonitoringTransport>('connecting');
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    let stopped = false;
    let live = false;
    let hub: MonitoringHub | null = null;
    let pollTimer: ReturnType<typeof setTimeout> | undefined;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    let retryDelay = retryMs;
    let polling = false;
    const controller = new AbortController();

    const pollOnce = async () => {
      try {
        const [status, values, navigation, alarms] = await Promise.all([
          fetchStatus(controller.signal),
          fetchValues(controller.signal),
          fetchNavigation(controller.signal),
          fetchAlarms('active', 1000, controller.signal),
        ]);
        if (stopped || live) return;
        store.dispatch({ type: 'status', status });
        store.dispatch({ type: 'values', batch: values });
        store.dispatch({ type: 'navigation', navigation });
        store.dispatch({ type: 'openAlarms', alarms: alarms.items });
        setError(null);
      } catch (e) {
        if (stopped || (e instanceof DOMException && e.name === 'AbortError')) return;
        setError(e);
      }
    };

    const pollLoop = async () => {
      if (polling) return;
      polling = true;
      while (!stopped && !live) {
        await pollOnce();
        if (stopped || live) break;
        await new Promise<void>((resolve) => {
          pollTimer = setTimeout(resolve, pollMs);
        });
      }
      polling = false;
    };

    const goPolling = () => {
      live = false;
      setTransport('polling');
      void pollLoop();
    };

    const catchUp = (h: MonitoringHub) =>
      h.getSnapshot().then(
        (snapshot) => {
          if (!stopped) store.dispatch({ type: 'snapshot', snapshot });
        },
        () => undefined,
      );

    const scheduleRetry = () => {
      if (stopped) return;
      clearTimeout(retryTimer);
      retryTimer = setTimeout(() => void tryHub(), retryDelay);
      retryDelay = Math.min(retryDelay * 2, 30_000);
    };

    const tryHub = async () => {
      if (stopped) return;
      try {
        const h = await createHub({
          snapshot: (snapshot) => store.dispatch({ type: 'snapshot', snapshot }),
          values: (batch) => store.dispatch({ type: 'values', batch }),
          alarmEvent: (notification) => store.dispatch({ type: 'alarmEvent', notification }),
          connection: (connection) => store.dispatch({ type: 'connection', connection }),
          navigation: (navigation) => store.dispatch({ type: 'navigation', navigation }),
          reconnecting: () => goPolling(),
          reconnected: () => {
            live = true;
            setTransport('live');
            setError(null);
            // The server also pushes a Snapshot on connect; asking again makes the catch-up certain.
            if (hub) void catchUp(hub);
          },
          closed: () => {
            if (stopped) return;
            hub = null;
            goPolling();
            scheduleRetry();
          },
        });
        if (stopped) {
          void h.stop();
          return;
        }
        hub = h;
        live = true;
        retryDelay = retryMs;
        setTransport('live');
        setError(null);
        void catchUp(h);
      } catch {
        // Hub unreachable (401 without a session, no ws proxy, backend down): poll, and try again later.
        goPolling();
        scheduleRetry();
      }
    };

    void tryHub();

    return () => {
      stopped = true;
      controller.abort();
      clearTimeout(pollTimer);
      clearTimeout(retryTimer);
      void hub?.stop();
    };
  }, [store, createHub, pollMs, retryMs, authKey]);

  return { transport, error };
}
