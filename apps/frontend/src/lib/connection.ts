import { useSyncExternalStore } from 'react';
import { setOfflineListener } from './api/client';

// Tiny external store for "is the backend reachable". The API wrapper reports into it on every call, and the
// banner polls GET /api/health while offline so the UI recovers without a reload.

export type ConnectionState = 'unknown' | 'online' | 'offline';

let state: ConnectionState = 'unknown';
const listeners = new Set<() => void>();

export function setConnection(next: ConnectionState) {
  if (next === state) return;
  state = next;
  listeners.forEach((l) => l());
}

export const getConnection = () => state;

export function useConnection(): ConnectionState {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => state,
    () => 'unknown' as ConnectionState,
  );
}

setOfflineListener((offline) => setConnection(offline ? 'offline' : 'online'));

/** Test helper. */
export function resetConnection() {
  state = 'unknown';
}
