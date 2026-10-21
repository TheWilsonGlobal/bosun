import type { AlarmNotification, Connection, Navigation, Snapshot, ValuesBatch } from './types';

// Thin seam over SignalR so tests drive the hook with a scripted fake. @microsoft/signalr is imported lazily (like the
// agent hub), so the page chunk only downloads it when the live screen is opened.

export interface MonitoringHandlers {
  snapshot: (s: Snapshot) => void;
  values: (b: ValuesBatch) => void;
  alarmEvent: (n: AlarmNotification) => void;
  connection: (c: Connection) => void;
  navigation: (n: Navigation) => void;
  /** The transport dropped; SignalR is retrying. */
  reconnecting: () => void;
  /** The transport came back (the server sends a fresh Snapshot on connect). */
  reconnected: () => void;
  /** Retries are exhausted or the connection was stopped. */
  closed: () => void;
}

export interface MonitoringHub {
  getSnapshot: () => Promise<Snapshot>;
  stop: () => Promise<void>;
}

export type CreateMonitoringHub = (handlers: MonitoringHandlers) => Promise<MonitoringHub>;

export const MONITORING_HUB_PATH = '/hubs/monitoring';

/** 0, 1, 2, 4, 8, then 10 s forever: the connection never gives up on its own while the page is open. */
export const reconnectDelay = (previousRetryCount: number): number =>
  previousRetryCount === 0 ? 0 : Math.min(10_000, 1000 * 2 ** (previousRetryCount - 1));

export const createSignalRMonitoringHub: CreateMonitoringHub = async (handlers) => {
  const { HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
  const connection = new HubConnectionBuilder()
    .withUrl(MONITORING_HUB_PATH)
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => reconnectDelay(ctx.previousRetryCount),
    })
    .configureLogging(LogLevel.None)
    .build();
  // Method names are matched case-insensitively by the client.
  connection.on('snapshot', handlers.snapshot);
  connection.on('values', handlers.values);
  connection.on('alarmevent', handlers.alarmEvent);
  connection.on('connection', handlers.connection);
  connection.on('navigation', handlers.navigation);
  connection.onreconnecting(() => handlers.reconnecting());
  connection.onreconnected(() => handlers.reconnected());
  connection.onclose(() => handlers.closed());
  await connection.start();
  return {
    getSnapshot: () => connection.invoke<Snapshot>('GetSnapshot'),
    stop: () => connection.stop(),
  };
};
