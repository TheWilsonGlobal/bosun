import { useSyncExternalStore } from 'react';
import type {
  Alarm,
  AlarmLevel,
  AlarmNotification,
  Connection,
  LinkName,
  LiveValue,
  MonitoringStatus,
  Navigation,
  Snapshot,
  ValuesBatch,
} from './types';

// The live state. The reducer is pure (and unit tested); the store around it lets widgets subscribe to ONE tag, so a
// 2 Hz values batch re-renders only the widgets whose tag actually changed, never the whole SVG.

export interface MonitoringState {
  status: MonitoringStatus | null;
  connections: Partial<Record<LinkName, Connection>>;
  values: Record<string, LiveValue>;
  navigation: Navigation | null;
  alarms: Record<number, Alarm>;
  /** The most severe OPEN alarm per tag (the one a widget is coloured by). */
  alarmByTag: Record<string, Alarm>;
  /** True once any snapshot / poll answered: distinguishes "no data yet" from "nothing is happening". */
  loaded: boolean;
}

export const initialMonitoringState: MonitoringState = {
  status: null,
  connections: {},
  values: {},
  navigation: null,
  alarms: {},
  alarmByTag: {},
  loaded: false,
};

export type MonitoringAction =
  | { type: 'snapshot'; snapshot: Snapshot }
  | { type: 'values'; batch: ValuesBatch }
  | { type: 'alarmEvent'; notification: AlarmNotification }
  | { type: 'connection'; connection: Connection }
  | { type: 'navigation'; navigation: Navigation }
  | { type: 'status'; status: MonitoringStatus }
  /** REST list of alarms in some state: upsert only (older history stays). */
  | { type: 'alarmList'; alarms: Alarm[] }
  /** The authoritative list of OPEN alarms (snapshot / poll): open alarms missing from it are gone. */
  | { type: 'openAlarms'; alarms: Alarm[] }
  | { type: 'reset' };

const KEEP_CLEARED = 200;

export const isOpen = (a: Alarm) => a.state !== 'cleared';
export const needsAck = (a: Alarm) =>
  a.state === 'unacknowledged' || a.state === 'returned_unacknowledged';
export const isCritical = (level: AlarmLevel) => level === 'HH' || level === 'LL';
const rank = (level: AlarmLevel) => (isCritical(level) ? 2 : 1);

/** The alarm that wins for a tag: higher severity, then unacknowledged before acknowledged, then newest. */
function better(a: Alarm, b: Alarm): boolean {
  if (rank(a.level) !== rank(b.level)) return rank(a.level) > rank(b.level);
  if (needsAck(a) !== needsAck(b)) return needsAck(a);
  return a.id > b.id;
}

function deriveByTag(alarms: Record<number, Alarm>, previous: Record<string, Alarm>) {
  const next: Record<string, Alarm> = {};
  for (const a of Object.values(alarms)) {
    if (!isOpen(a)) continue;
    const cur = next[a.tag];
    if (!cur || better(a, cur)) next[a.tag] = a;
  }
  // Keep the previous object when nothing changed so selectors stay stable.
  const same =
    Object.keys(next).length === Object.keys(previous).length &&
    Object.keys(next).every((t) => previous[t] === next[t]);
  return same ? previous : next;
}

function trim(alarms: Record<number, Alarm>): Record<number, Alarm> {
  const cleared = Object.values(alarms)
    .filter((a) => !isOpen(a))
    .sort((a, b) => b.id - a.id);
  if (cleared.length <= KEEP_CLEARED) return alarms;
  const next = { ...alarms };
  for (const a of cleared.slice(KEEP_CLEARED)) delete next[a.id];
  return next;
}

function withAlarms(state: MonitoringState, alarms: Record<number, Alarm>): MonitoringState {
  const trimmed = trim(alarms);
  return { ...state, alarms: trimmed, alarmByTag: deriveByTag(trimmed, state.alarmByTag) };
}

function mergeValues(
  current: Record<string, LiveValue>,
  incoming: readonly LiveValue[],
): Record<string, LiveValue> {
  let next = current;
  for (const v of incoming) {
    const old = current[v.tag];
    if (
      old &&
      old.value === v.value &&
      old.quality === v.quality &&
      old.timestamp === v.timestamp &&
      old.unit === v.unit
    )
      continue;
    if (next === current) next = { ...current };
    next[v.tag] = v;
  }
  return next;
}

const stateOrder = (a: Alarm) =>
  a.state === 'cleared'
    ? 3
    : a.state === 'returned_unacknowledged'
      ? 2
      : a.state === 'acknowledged'
        ? 1
        : 0;

function upsertAlarms(state: MonitoringState, list: readonly Alarm[]): MonitoringState {
  const alarms = { ...state.alarms };
  for (const a of list) {
    // Never let an older answer move an alarm back to an earlier lifecycle state. (An acknowledged alarm that
    // returned goes straight to cleared, so the order unacknowledged < acknowledged < returned < cleared holds only
    // for the forward steps; unacknowledged -> acknowledged and unacknowledged -> returned both move forward.)
    const old = alarms[a.id];
    alarms[a.id] = old && stateOrder(old) > stateOrder(a) ? old : a;
  }
  return withAlarms(state, alarms);
}

export function monitoringReducer(
  state: MonitoringState,
  action: MonitoringAction,
): MonitoringState {
  switch (action.type) {
    case 'reset':
      return initialMonitoringState;
    case 'snapshot': {
      const { snapshot } = action;
      const s: MonitoringState = {
        ...state,
        status: snapshot.status,
        connections: {
          ...state.connections,
          modbus: snapshot.status.modbus,
          nmea: snapshot.status.nmea,
        },
        values: mergeValues(state.values, snapshot.values),
        navigation: snapshot.navigation ?? state.navigation,
        loaded: true,
      };
      return monitoringReducer(s, { type: 'openAlarms', alarms: snapshot.alarms });
    }
    case 'status':
      return {
        ...state,
        status: action.status,
        connections: {
          ...state.connections,
          modbus: action.status.modbus,
          nmea: action.status.nmea,
        },
        loaded: true,
      };
    case 'values':
      return { ...state, values: mergeValues(state.values, action.batch.values), loaded: true };
    case 'navigation':
      return { ...state, navigation: action.navigation };
    case 'connection':
      return {
        ...state,
        connections: { ...state.connections, [action.connection.name]: action.connection },
      };
    case 'alarmEvent':
      return upsertAlarms(state, [action.notification.alarm]);
    case 'alarmList':
      return upsertAlarms(state, action.alarms);
    case 'openAlarms': {
      const incoming = new Set(action.alarms.map((a) => a.id));
      const alarms: Record<number, Alarm> = {};
      for (const a of Object.values(state.alarms)) {
        if (!isOpen(a) || incoming.has(a.id)) alarms[a.id] = a;
      }
      return upsertAlarms({ ...state, alarms, loaded: true }, action.alarms);
    }
  }
}

// ---- derived views (pure) ----

export interface AlarmSummary {
  open: number;
  unacknowledged: number;
  /** Highest severity among all open alarms / among those that still need an acknowledgement. */
  highest: AlarmLevel | null;
  highestUnacked: AlarmLevel | null;
}

export function alarmSummary(alarms: Record<number, Alarm>): AlarmSummary {
  const out: AlarmSummary = { open: 0, unacknowledged: 0, highest: null, highestUnacked: null };
  for (const a of Object.values(alarms)) {
    if (!isOpen(a)) continue;
    out.open += 1;
    if (!out.highest || rank(a.level) > rank(out.highest)) out.highest = a.level;
    if (needsAck(a)) {
      out.unacknowledged += 1;
      if (!out.highestUnacked || rank(a.level) > rank(out.highestUnacked))
        out.highestUnacked = a.level;
    }
  }
  return out;
}

// ---- the store ----

export interface MonitoringStore {
  getState: () => MonitoringState;
  dispatch: (action: MonitoringAction) => void;
  subscribe: (listener: () => void) => () => void;
}

export function createMonitoringStore(): MonitoringStore {
  let state = initialMonitoringState;
  const listeners = new Set<() => void>();
  return {
    getState: () => state,
    dispatch: (action) => {
      const next = monitoringReducer(state, action);
      if (next === state) return;
      state = next;
      listeners.forEach((l) => l());
    },
    subscribe: (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
  };
}

/** Subscribe to a slice. `select` must return a stable reference for unchanged data (the reducer guarantees it). */
export function useMonitoringSelector<T>(
  store: MonitoringStore,
  select: (s: MonitoringState) => T,
): T {
  return useSyncExternalStore(
    store.subscribe,
    () => select(store.getState()),
    () => select(initialMonitoringState),
  );
}
