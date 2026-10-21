import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import type { AlarmLevel, AlarmState, Connection, LinkName, MonitoringStatus } from './types';

export const ALARM_STATE_LABELS: Record<AlarmState, string> = {
  unacknowledged: 'Unacknowledged',
  acknowledged: 'Acknowledged',
  returned_unacknowledged: 'Returned to normal, unacknowledged',
  cleared: 'Cleared',
};

export const ALARM_STATE_SHORT: Record<AlarmState, string> = {
  unacknowledged: 'Unacknowledged',
  acknowledged: 'Acknowledged',
  returned_unacknowledged: 'Returned, unack.',
  cleared: 'Cleared',
};

export const LEVEL_LABELS: Record<AlarmLevel, string> = {
  LL: 'Low-low',
  L: 'Low',
  H: 'High',
  HH: 'High-high',
};

export const LINK_LABELS: Record<LinkName, string> = { modbus: 'Modbus', nmea: 'NMEA' };

export interface AckMessage {
  title: string;
  message: string;
  /** 401: the caller opens the login dialog. */
  needsLogin: boolean;
  /** The alarm changed under the operator: the list should be refreshed. */
  refresh: boolean;
}

/** Specific wording for every error POST /api/monitoring/alarms/{id}/ack can return. */
export function describeAckError(error: unknown): AckMessage {
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'unauthorized':
        return {
          title: 'Sign-in required',
          message: 'Sign in to acknowledge alarms. Your session may have expired.',
          needsLogin: true,
          refresh: false,
        };
      case 'already_acknowledged':
        return {
          title: 'Already acknowledged',
          message:
            'Someone else acknowledged this alarm a moment ago. The list has been refreshed.',
          needsLogin: false,
          refresh: true,
        };
      case 'alarm_closed':
        return {
          title: 'Alarm already closed',
          message: 'This alarm has cleared in the meantime and no longer needs acknowledging.',
          needsLogin: false,
          refresh: true,
        };
      case 'not_found':
        return {
          title: 'Alarm not found',
          message: 'This alarm is no longer known to the backend (it may have been restarted).',
          needsLogin: false,
          refresh: true,
        };
      case 'invalid_request':
        return {
          title: 'Invalid request',
          message: error.detail || 'The acknowledgement was rejected as malformed.',
          needsLogin: false,
          refresh: false,
        };
    }
    if (error.status === 401) return describeAckError(new ApiError(401, 'unauthorized', ''));
  }
  const m = describeError(error);
  return { ...m, needsLogin: false, refresh: false };
}

export type SimulatorState =
  /** No status yet. */
  | 'unknown'
  | 'ok'
  /** Monitoring is switched off on the backend (Monitoring:Enabled=false). */
  | 'backend_disabled'
  /** Neither the Modbus nor the NMEA link is up: the simulator (or real source) is not running. */
  | 'down'
  /** One link is up, the other is not. */
  | 'partial'
  /** Both links are up but one refuses a register range (state "degraded"). */
  | 'degraded';

// A degraded link is up (the TCP/serial link works) but incomplete; it is not a dead source.
export const isLinkUp = (c?: Connection) => c?.state === 'connected' || c?.state === 'degraded';
const up = isLinkUp;
const trying = (c?: Connection) => c?.state === 'connecting';

export function simulatorState(
  status: MonitoringStatus | null,
  connections: Partial<Record<LinkName, Connection>>,
): SimulatorState {
  const { modbus, nmea } = connections;
  if (!modbus && !nmea) return 'unknown';
  if (status && !status.enabled) return 'backend_disabled';
  if (up(modbus) && up(nmea))
    return modbus?.state === 'degraded' || nmea?.state === 'degraded' ? 'degraded' : 'ok';
  if (up(modbus) || up(nmea)) return 'partial';
  // Neither is up. A first connection attempt is not yet "down".
  if (trying(modbus) && !modbus?.lastError && trying(nmea) && !nmea?.lastError) return 'unknown';
  return 'down';
}

export const SIMULATOR_DOWN_TITLE = 'Simulator not running';

export function describeSimulatorState(
  state: SimulatorState,
  connections: Partial<Record<LinkName, Connection>>,
): { title: string; message: string } | null {
  const endpoints = (['modbus', 'nmea'] as const)
    .map((n) => connections[n]?.endpoint)
    .filter((e): e is string => !!e)
    .join(' and ');
  switch (state) {
    case 'down':
      return {
        title: SIMULATOR_DOWN_TITLE,
        message: `Neither the Modbus nor the NMEA source is reachable${endpoints ? ` (${endpoints})` : ''}, so there are no live values. The last known values are shown in grey. Start the simulator with "pnpm run sim"; the backend reconnects by itself.`,
      };
    case 'backend_disabled':
      return {
        title: 'Monitoring is disabled on the backend',
        message:
          'The backend runs with Monitoring:Enabled=false, so it does not read the Modbus or NMEA sources. Enable it in the backend configuration and restart it.',
      };
    case 'partial': {
      const down = (['modbus', 'nmea'] as const).find((n) => !up(connections[n]));
      return down === 'modbus'
        ? {
            title: 'Modbus link is down',
            message:
              'Process values are not updating and are shown in grey. Position and heading from NMEA still update.',
          }
        : {
            title: 'NMEA link is down',
            message:
              'Process values still update. Position, speed and heading are not available until the NMEA source returns.',
          };
    }
    case 'degraded': {
      const d = (['modbus', 'nmea'] as const).filter((n) => connections[n]?.state === 'degraded');
      const detail = d
        .map(
          (n) => `${LINK_LABELS[n]}: ${connections[n]?.lastError ?? 'a register range is refused'}`,
        )
        .join('; ');
      return {
        title: 'Source link degraded',
        message: `The link is up but not every value can be read, so some values stay grey. ${detail}`,
      };
    }
    default:
      return null;
  }
}
