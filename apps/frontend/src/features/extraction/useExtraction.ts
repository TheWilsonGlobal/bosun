import { useQuery } from '@tanstack/react-query';
import { fetchExtraction } from './api';
import { isActive, type Extraction } from './types';

export const POLL_MS = 1500;

export const extractionKey = (id: number) => ['extraction', id] as const;

/** Poll interval for the data we hold: keep going while queued/running, stop on completed/partial/failed. */
export function pollInterval(data: Extraction | undefined, pollMs: number): number | false {
  return data && isActive(data.status) ? pollMs : false;
}

/**
 * Loads one extraction and polls it every ~1.5 s while it is queued or running. A failed poll (offline, 502 from
 * the proxy) keeps the last data and keeps polling, so the page resumes by itself when the backend is back; the
 * connection banner is driven by apiFetch. fetchExtraction goes through apiFetch, so res.ok/status are checked.
 */
export function useExtraction(id: number, pollMs: number = POLL_MS) {
  return useQuery({
    queryKey: extractionKey(id),
    queryFn: ({ signal }) => fetchExtraction(id, signal),
    enabled: Number.isInteger(id),
    refetchInterval: (query) => pollInterval(query.state.data, pollMs),
  });
}
