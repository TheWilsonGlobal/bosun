import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { CloudOff, DatabaseZap, RefreshCw } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { fetchHealth } from '@/lib/api/endpoints';
import { getConnection, setConnection, subscribeConnection, useConnection } from '@/lib/connection';

export const OFFLINE_RECHECK_MS = 4000;
export const ONLINE_RECHECK_MS = 8000;

/**
 * Backend-offline banner. Offline is detected two ways: any API call that comes back as a (resolved) proxy 502 or
 * a rejected fetch reports into the connection store, and this component polls GET /api/health. While offline it
 * re-checks every few seconds and refetches all queries when the backend returns.
 */
export function BackendBanner({
  offlineMs = OFFLINE_RECHECK_MS,
  onlineMs = ONLINE_RECHECK_MS,
}: {
  offlineMs?: number;
  onlineMs?: number;
}) {
  const qc = useQueryClient();
  const connection = useConnection();
  const [databaseOk, setDatabaseOk] = useState(true);
  const [checking, setChecking] = useState(false);
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();

    let inFlight = false;
    const probe = async () => {
      if (inFlight) return;
      inFlight = true;
      clearTimeout(timer);
      const wasOffline = getConnection() === 'offline';
      setChecking(true);
      try {
        const health = await fetchHealth(controller.signal);
        if (stopped) return;
        setDatabaseOk(health.databaseOk);
        setConnection('online');
        if (wasOffline) void qc.invalidateQueries();
      } catch {
        if (stopped) return;
        setConnection('offline');
      } finally {
        inFlight = false;
        if (!stopped) setChecking(false);
      }
      if (!stopped) {
        timer = setTimeout(
          () => void probe(),
          getConnection() === 'offline' ? offlineMs : onlineMs,
        );
      }
    };

    // Re-check right away when the tab becomes visible/focused, and when any API call reports the backend offline
    // (that also moves the schedule onto the short offline interval).
    const recheck = () => {
      if (document.visibilityState !== 'hidden') void probe();
    };
    const unsubscribe = subscribeConnection(() => {
      if (getConnection() === 'offline') void probe();
    });
    window.addEventListener('focus', recheck);
    document.addEventListener('visibilitychange', recheck);

    void probe();
    return () => {
      window.removeEventListener('focus', recheck);
      document.removeEventListener('visibilitychange', recheck);
      unsubscribe();
      stopped = true;
      controller.abort();
      clearTimeout(timer);
    };
  }, [qc, offlineMs, onlineMs, tick]);

  if (connection === 'offline') {
    return (
      <div
        role="alert"
        className="bg-destructive text-white flex flex-wrap items-center gap-3 px-4 py-2 text-sm"
      >
        <CloudOff className="size-4 shrink-0" aria-hidden />
        <span className="font-medium">Backend offline.</span>
        <span>
          The configurator cannot reach the Bosun API. Edits stay in this browser; checking again
          every {Math.round(offlineMs / 1000)} s.
        </span>
        <Button
          size="sm"
          variant="secondary"
          className="ml-auto"
          disabled={checking}
          onClick={() => setTick((t) => t + 1)}
        >
          <RefreshCw className={checking ? 'animate-spin' : ''} aria-hidden />
          Retry now
        </Button>
      </div>
    );
  }
  if (!databaseOk) {
    return (
      <div
        role="alert"
        className="bg-warning text-warning-foreground flex items-center gap-3 px-4 py-2 text-sm"
      >
        <DatabaseZap className="size-4 shrink-0" aria-hidden />
        <span className="font-medium">Backend is up but its database is unavailable.</span>
      </div>
    );
  }
  return null;
}
