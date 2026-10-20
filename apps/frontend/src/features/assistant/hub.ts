import type { AgentDoneEvent, AgentRun, AgentStatusEvent, AgentStepEvent } from './types';

// A thin seam over SignalR so the run hook can be driven by a scripted fake in tests. The real connection is imported
// lazily: the SignalR client is only downloaded when a run is actually watched.

export interface HubHandlers {
  step: (e: AgentStepEvent) => void;
  status: (e: AgentStatusEvent) => void;
  done: (e: AgentDoneEvent) => void;
  /** The transport dropped and came back; the group membership is gone, so the caller must JoinRun again. */
  reconnected: () => void;
  /** The connection is gone for good (no more automatic retries). */
  closed: () => void;
}

export interface AgentHub {
  /** JoinRun: returns the run so far, with its steps. */
  join: (runId: number) => Promise<AgentRun>;
  stop: () => Promise<void>;
}

/** Rejects when the connection cannot be established. */
export type CreateHub = (handlers: HubHandlers) => Promise<AgentHub>;

export const HUB_PATH = '/hubs/agent';

export const createSignalRHub: CreateHub = async (handlers) => {
  const { HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
  const connection = new HubConnectionBuilder()
    .withUrl(HUB_PATH)
    .withAutomaticReconnect([0, 1000, 3000, 10000])
    .configureLogging(LogLevel.None)
    .build();
  connection.on('step', handlers.step);
  connection.on('status', handlers.status);
  connection.on('done', handlers.done);
  connection.onreconnected(() => handlers.reconnected());
  connection.onclose(() => handlers.closed());
  await connection.start();
  return {
    join: (runId) => connection.invoke<AgentRun>('JoinRun', runId),
    stop: () => connection.stop(),
  };
};
