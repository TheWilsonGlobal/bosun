import { Profiler, type ReactNode } from 'react';
import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/lib/api/client';
import { json, makeSession, mockFetch, problem, project, proxy502, renderAt } from '@/test/utils';
import { mimicFixture } from '@/test/mimicFixture';
import { AlarmBanner, AlarmList, ConnectionBadges, filterAlarms } from './AlarmPanels';
import type { CreateMonitoringHub, MonitoringHandlers } from './hub';
import { reconnectDelay } from './hub';
import {
  ALARM_STATE_LABELS,
  describeAckError,
  describeSimulatorState,
  simulatorState,
} from './messages';
import { MimicView } from './MimicView';
import { MonitoringPage } from './MonitoringPage';
import {
  alarmSummary,
  createMonitoringStore,
  initialMonitoringState,
  monitoringReducer,
} from './store';
import type {
  Alarm,
  AlarmEventKind,
  Connection,
  LiveValue,
  MonitoringStatus,
  Navigation,
  Snapshot,
} from './types';
import { useMonitoring } from './useMonitoring';
import { ValueWidget } from './Widgets';
import {
  appearanceOf,
  formatLatitude,
  formatLongitude,
  fraction,
  navigationText,
} from './widgetLogic';

afterEach(() => vi.restoreAllMocks());

// ------------------------------------------------------------------ factories

const T0 = '2026-10-21T10:00:00Z';

const conn = (
  name: 'modbus' | 'nmea',
  state: Connection['state'],
  over: Partial<Connection> = {},
) =>
  ({
    name,
    state,
    since: T0,
    lastError: null,
    attempts: 0,
    endpoint: name === 'modbus' ? '127.0.0.1:5483' : '127.0.0.1:5484',
    ...over,
  }) satisfies Connection;

const status = (over: Partial<MonitoringStatus> = {}): MonitoringStatus => ({
  enabled: true,
  projectId: 1,
  modbus: conn('modbus', 'connected'),
  nmea: conn('nmea', 'connected'),
  tagCount: 40,
  activeAlarms: 0,
  unacknowledgedAlarms: 0,
  lastValueAt: T0,
  ...over,
});

const alarm = (over: Partial<Alarm> = {}): Alarm => ({
  id: 1,
  projectId: 1,
  tag: 'ME-EXH-TT01',
  level: 'H',
  limitValue: 95,
  delayS: 10,
  hysteresis: 2,
  unit: 'degC',
  description: 'Exhaust gas temperature cyl 1',
  state: 'unacknowledged',
  raisedAt: T0,
  raisedValue: 96,
  peakValue: 97,
  acknowledgedAt: null,
  acknowledgedBy: null,
  returnedAt: null,
  returnedValue: null,
  clearedAt: null,
  ...over,
});

const value = (tag: string, v: number, over: Partial<LiveValue> = {}): LiveValue => ({
  tag,
  value: v,
  unit: 'degC',
  quality: 'good',
  timestamp: T0,
  ...over,
});

const snapshot = (over: Partial<Snapshot> = {}): Snapshot => ({
  status: status(),
  values: [],
  navigation: null,
  alarms: [],
  ...over,
});

const event = (kind: AlarmEventKind, a: Alarm) => ({
  type: 'alarmEvent' as const,
  notification: { kind, alarm: a, at: T0, value: null },
});

const run = (actions: Parameters<typeof monitoringReducer>[1][], from = initialMonitoringState) =>
  actions.reduce(monitoringReducer, from);

function seeded(actions: Parameters<typeof monitoringReducer>[1][]) {
  const store = createMonitoringStore();
  act(() => actions.forEach((a) => store.dispatch(a)));
  return store;
}

const svgWrap = (children: ReactNode) => <svg>{children}</svg>;

// ------------------------------------------------------------------ reducer

describe('monitoring reducer', () => {
  it('Snapshot loads status, links, values, navigation and open alarms', () => {
    const nav: Navigation = {
      valid: true,
      latitude: 20.85,
      longitude: 106.68,
      speedKnots: 12.3,
      courseDeg: 90,
      headingDeg: 91,
      timestamp: T0,
    };
    const s = run([
      {
        type: 'snapshot',
        snapshot: snapshot({
          values: [value('A', 1), value('B', 2)],
          navigation: nav,
          alarms: [alarm({ id: 4 })],
        }),
      },
    ]);
    expect(s.loaded).toBe(true);
    expect(Object.keys(s.values)).toEqual(['A', 'B']);
    expect(s.navigation).toEqual(nav);
    expect(s.connections.modbus?.state).toBe('connected');
    expect(s.alarmByTag['ME-EXH-TT01'].id).toBe(4);
  });

  it('Values change only the tags in the batch and keep every other entry by identity', () => {
    const s1 = run([
      { type: 'snapshot', snapshot: snapshot({ values: [value('A', 1), value('B', 2)] }) },
    ]);
    const s2 = run([{ type: 'values', batch: { timestamp: T0, values: [value('A', 5)] } }], s1);
    expect(s2.values.A.value).toBe(5);
    expect(s2.values.B).toBe(s1.values.B);
    // an identical value does not even produce a new state object for the values map
    const s3 = run([{ type: 'values', batch: { timestamp: T0, values: [value('A', 5)] } }], s2);
    expect(s3.values).toBe(s2.values);
  });

  it('follows an alarm through raised, acknowledged, returned_to_normal and cleared', () => {
    const raised = alarm();
    const acked = alarm({ state: 'acknowledged', acknowledgedBy: 'alice', acknowledgedAt: T0 });
    const cleared = alarm({
      state: 'cleared',
      acknowledgedBy: 'alice',
      returnedAt: T0,
      returnedValue: 90,
      clearedAt: T0,
    });
    let s = run([event('raised', raised)]);
    expect(s.alarmByTag['ME-EXH-TT01'].state).toBe('unacknowledged');
    s = run([event('acknowledged', acked)], s);
    expect(s.alarmByTag['ME-EXH-TT01'].state).toBe('acknowledged');
    s = run([event('cleared', cleared)], s);
    expect(s.alarmByTag['ME-EXH-TT01']).toBeUndefined();
    expect(s.alarms[1].state).toBe('cleared'); // kept for the Cleared list
  });

  it('a condition that returns before anyone acknowledged stays open as returned_unacknowledged', () => {
    const returned = alarm({ state: 'returned_unacknowledged', returnedAt: T0, returnedValue: 90 });
    let s = run([event('raised', alarm()), event('returned_to_normal', returned)]);
    expect(s.alarmByTag['ME-EXH-TT01'].state).toBe('returned_unacknowledged');
    expect(alarmSummary(s.alarms)).toMatchObject({ open: 1, unacknowledged: 1 });
    s = run([event('cleared', { ...returned, state: 'cleared', clearedAt: T0 })], s);
    expect(alarmSummary(s.alarms).open).toBe(0);
  });

  it('a re-raise arrives with a NEW id and the old one stays in history', () => {
    const first = alarm({ id: 1, state: 'cleared', clearedAt: T0 });
    const second = alarm({ id: 2, raisedAt: '2026-10-21T10:05:00Z' });
    const s = run([event('cleared', first), event('raised', second)]);
    expect(s.alarmByTag['ME-EXH-TT01'].id).toBe(2);
    expect(Object.keys(s.alarms).sort()).toEqual(['1', '2']);
    expect(filterAlarms(s.alarms, 'active').map((a) => a.id)).toEqual([2]);
    expect(filterAlarms(s.alarms, 'cleared').map((a) => a.id)).toEqual([1]);
    expect(filterAlarms(s.alarms, 'all').map((a) => a.id)).toEqual([2, 1]);
  });

  it('an older answer cannot move an alarm back to an earlier state', () => {
    const s = run([
      event('acknowledged', alarm({ state: 'acknowledged' })),
      { type: 'alarmList', alarms: [alarm({ state: 'unacknowledged' })] },
    ]);
    expect(s.alarms[1].state).toBe('acknowledged');
  });

  it('the most severe open alarm of a tag wins (HH over H)', () => {
    const s = run([
      event('raised', alarm({ id: 1, level: 'H' })),
      event('raised', alarm({ id: 2, level: 'HH', limitValue: 110 })),
    ]);
    expect(s.alarmByTag['ME-EXH-TT01'].level).toBe('HH');
  });

  it('openAlarms (snapshot / poll) drops open alarms the server no longer lists but keeps history', () => {
    const s = run([
      event('raised', alarm({ id: 1 })),
      event('cleared', alarm({ id: 7, tag: 'X', state: 'cleared' })),
      { type: 'snapshot', snapshot: snapshot({ alarms: [alarm({ id: 2, tag: 'Y' })] }) },
    ]);
    expect(Object.keys(s.alarms).sort()).toEqual(['2', '7']);
  });

  it('Connection and Navigation events replace their slice', () => {
    const nav: Navigation = {
      valid: false,
      latitude: null,
      longitude: null,
      speedKnots: null,
      courseDeg: null,
      headingDeg: null,
      timestamp: null,
    };
    const s = run([
      { type: 'snapshot', snapshot: snapshot() },
      {
        type: 'connection',
        connection: conn('modbus', 'disconnected', { attempts: 3, lastError: 'refused' }),
      },
      { type: 'navigation', navigation: nav },
    ]);
    expect(s.connections.modbus).toMatchObject({ state: 'disconnected', attempts: 3 });
    expect(s.connections.nmea?.state).toBe('connected');
    expect(s.navigation).toEqual(nav);
  });

  it('a full sequence of events leaves the expected summary', () => {
    const s = run([
      { type: 'snapshot', snapshot: snapshot() },
      { type: 'values', batch: { timestamp: T0, values: [value('ME-EXH-TT01', 96)] } },
      event('raised', alarm({ id: 1 })),
      event('raised', alarm({ id: 2, tag: 'ME-EXH-TT02', level: 'HH' })),
      event('acknowledged', alarm({ id: 1, state: 'acknowledged' })),
    ]);
    expect(alarmSummary(s.alarms)).toEqual({
      open: 2,
      unacknowledged: 1,
      highest: 'HH',
      highestUnacked: 'HH',
    });
  });
});

// ------------------------------------------------------------------ appearance + widgets

describe('widget appearance', () => {
  const v = value('T', 90);
  it('is normal without an alarm', () => {
    expect(appearanceOf(v, undefined, true)).toMatchObject({
      tone: 'normal',
      blink: false,
      marker: null,
    });
  });
  it('colours H/L amber and HH/LL red, with a text marker that carries the state', () => {
    expect(appearanceOf(v, alarm({ level: 'H' }), true)).toMatchObject({
      tone: 'warning',
      severity: 'warning',
      marker: 'H UNACK',
      blink: true,
    });
    expect(appearanceOf(v, alarm({ level: 'L', state: 'acknowledged' }), true)).toMatchObject({
      tone: 'warning',
      marker: 'L ACK',
      blink: false,
    });
    expect(appearanceOf(v, alarm({ level: 'HH' }), true)).toMatchObject({ tone: 'critical' });
    expect(appearanceOf(v, alarm({ level: 'LL' }), true)).toMatchObject({ tone: 'critical' });
  });
  it('a returned-unacknowledged alarm keeps its marker but does not blink', () => {
    expect(
      appearanceOf(v, alarm({ level: 'HH', state: 'returned_unacknowledged' }), true),
    ).toMatchObject({ marker: 'HH RTN', blink: false });
  });
  it('greys on stale quality or a down Modbus link, even with an alarm (marker stays)', () => {
    expect(appearanceOf(value('T', 90, { quality: 'stale' }), undefined, true)).toMatchObject({
      tone: 'stale',
      stale: true,
    });
    const down = appearanceOf(v, alarm({ level: 'HH' }), false);
    expect(down).toMatchObject({ tone: 'stale', blink: false, marker: 'HH UNACK' });
  });
  it('formats numbers, fractions and positions', () => {
    expect(fraction(50, 0, 100)).toBe(0.5);
    expect(fraction(500, 0, 100)).toBe(1);
    expect(fraction(undefined, 0, 100)).toBe(0);
    expect(formatLatitude(20.85)).toBe("20°51.000'N");
    expect(formatLongitude(-106.5)).toBe("106°30.000'W");
    expect(navigationText(null, true).unavailable).toMatch(/waiting/i);
    expect(navigationText(null, false).unavailable).toMatch(/not connected/i);
    expect(
      navigationText(
        {
          valid: false,
          latitude: null,
          longitude: null,
          speedKnots: null,
          courseDeg: null,
          headingDeg: 12,
          timestamp: null,
        },
        true,
      ),
    ).toMatchObject({ unavailable: 'No position fix', heading: '12°' });
  });
});

describe('ValueWidget', () => {
  const binding = mimicFixture.bindings[0];
  const info = mimicFixture.tags[binding.tag!];

  it('shows value and unit, then updates when its tag changes', () => {
    const store = seeded([
      { type: 'snapshot', snapshot: snapshot({ values: [value('ME-EXH-TT01', 412.3)] }) },
    ]);
    render(svgWrap(<ValueWidget store={store} binding={binding} info={info} />));
    const w = screen.getByTestId(`widget-${binding.id}`);
    expect(w).toHaveAttribute('data-tone', 'normal');
    expect(w).toHaveTextContent('412.3');
    expect(w).toHaveTextContent('degC');
    act(() =>
      store.dispatch({
        type: 'values',
        batch: { timestamp: T0, values: [value('ME-EXH-TT01', 420.0)] },
      }),
    );
    expect(w).toHaveTextContent('420.0');
  });

  it('shows -- before any value exists', () => {
    const store = seeded([{ type: 'snapshot', snapshot: snapshot() }]);
    render(svgWrap(<ValueWidget store={store} binding={binding} info={info} />));
    expect(screen.getByTestId(`widget-${binding.id}`)).toHaveTextContent('--');
  });

  it('colours by the tag alarm: amber for H with a visible marker, red for HH, blinking while unacknowledged', () => {
    const store = seeded([
      { type: 'snapshot', snapshot: snapshot({ values: [value('ME-EXH-TT01', 96)] }) },
      event('raised', alarm({ level: 'H' })),
    ]);
    render(svgWrap(<ValueWidget store={store} binding={binding} info={info} />));
    const w = screen.getByTestId(`widget-${binding.id}`);
    expect(w).toHaveAttribute('data-tone', 'warning');
    expect(w).toHaveAttribute('data-blink', 'true');
    expect(within(w).getByTestId('alarm-marker')).toBeInTheDocument();
    expect(w).toHaveTextContent('H 95.0');
    expect(w).toHaveTextContent('UNACK');
    act(() => store.dispatch(event('raised', alarm({ id: 2, level: 'HH', limitValue: 110 }))));
    expect(w).toHaveAttribute('data-tone', 'critical');
    act(() =>
      store.dispatch(event('acknowledged', alarm({ id: 2, level: 'HH', state: 'acknowledged' }))),
    );
    expect(w).toHaveAttribute('data-tone', 'critical');
    expect(w).toHaveAttribute('data-blink', 'false');
    expect(w).toHaveTextContent('ACK');
  });

  it('greys when the value quality is stale', () => {
    const store = seeded([
      {
        type: 'snapshot',
        snapshot: snapshot({ values: [value('ME-EXH-TT01', 400, { quality: 'stale' })] }),
      },
    ]);
    render(svgWrap(<ValueWidget store={store} binding={binding} info={info} />));
    expect(screen.getByTestId(`widget-${binding.id}`)).toHaveAttribute('data-tone', 'stale');
  });

  it('greys when the Modbus link goes down, and recovers when it returns', () => {
    const store = seeded([
      { type: 'snapshot', snapshot: snapshot({ values: [value('ME-EXH-TT01', 400)] }) },
    ]);
    render(svgWrap(<ValueWidget store={store} binding={binding} info={info} />));
    const w = screen.getByTestId(`widget-${binding.id}`);
    expect(w).toHaveAttribute('data-tone', 'normal');
    act(() => store.dispatch({ type: 'connection', connection: conn('modbus', 'disconnected') }));
    expect(w).toHaveAttribute('data-tone', 'stale');
    act(() => store.dispatch({ type: 'connection', connection: conn('modbus', 'connected') }));
    expect(w).toHaveAttribute('data-tone', 'normal');
  });

  it('a values batch for another tag does not re-render the widget (selector subscription)', () => {
    const store = seeded([
      {
        type: 'snapshot',
        snapshot: snapshot({ values: [value('ME-EXH-TT01', 1), value('ME-EXH-TT02', 2)] }),
      },
    ]);
    const renders = { a: 0, b: 0 };
    const b2 = mimicFixture.bindings[1];
    render(
      svgWrap(
        <>
          <Profiler id="a" onRender={() => (renders.a += 1)}>
            <ValueWidget store={store} binding={binding} info={info} />
          </Profiler>
          <Profiler id="b" onRender={() => (renders.b += 1)}>
            <ValueWidget store={store} binding={b2} info={mimicFixture.tags[b2.tag!]} />
          </Profiler>
        </>,
      ),
    );
    const before = { ...renders };
    act(() =>
      store.dispatch({
        type: 'values',
        batch: { timestamp: T0, values: [value('ME-EXH-TT02', 3)] },
      }),
    );
    expect(renders.a).toBe(before.a);
    expect(renders.b).toBe(before.b + 1);
  });
});

// ------------------------------------------------------------------ mimic

describe('MimicView from the mimic document', () => {
  it('draws every shape by role and one widget per binding, including the navigation block', () => {
    const store = seeded([
      {
        type: 'snapshot',
        snapshot: snapshot({
          values: [value('TANK-HFO1-LT01', 62)],
          navigation: {
            valid: true,
            latitude: 20.85,
            longitude: 106.68,
            speedKnots: 12.3,
            courseDeg: 91,
            headingDeg: 92,
            timestamp: T0,
          },
        }),
      },
    ]);
    const { container } = render(<MimicView doc={mimicFixture} store={store} />);
    const svg = screen.getByTestId('mimic-svg');
    expect(svg).toHaveAttribute('viewBox', '0 0 1200 760');
    for (const s of mimicFixture.shapes) {
      const el = container.querySelector(`[data-shape="${s.id}"]`);
      expect(el, s.id).not.toBeNull();
      expect(el).toHaveAttribute('data-role', s.role);
    }
    for (const b of mimicFixture.bindings) {
      expect(screen.getByTestId(`widget-${b.id}`), b.id).toBeInTheDocument();
    }
    expect(screen.getByTestId('widget-b-navigation')).toHaveTextContent('12.3 kn');
    expect(screen.getByTestId('widget-b-navigation')).toHaveTextContent("20°51.000'N");
    expect(screen.getByTestId('tank-fill-b-tank-hfo1-lt01')).toBeInTheDocument();
    expect(
      screen.getByText('MV Ha Long - Engine room mimic', { selector: 'text' }),
    ).toBeInTheDocument();
  });

  it('greys the navigation block when the NMEA link is down and says why', () => {
    const store = seeded([
      {
        type: 'snapshot',
        snapshot: snapshot({ status: status({ nmea: conn('nmea', 'disconnected') }) }),
      },
    ]);
    render(<MimicView doc={mimicFixture} store={store} />);
    const nav = screen.getByTestId('widget-b-navigation');
    expect(nav).toHaveAttribute('data-tone', 'stale');
    expect(nav).toHaveTextContent('NMEA not connected');
  });
});

// ------------------------------------------------------------------ banner, badges, simulator

describe('alarm banner and connection badges', () => {
  it('counts unacknowledged alarms and names the highest severity', () => {
    const store = seeded([
      { type: 'snapshot', snapshot: snapshot() },
      event('raised', alarm({ id: 1, level: 'H' })),
      event('raised', alarm({ id: 2, tag: 'B', level: 'HH' })),
      event('raised', alarm({ id: 3, tag: 'C', level: 'L', state: 'acknowledged' })),
    ]);
    render(<AlarmBanner store={store} />);
    const banner = screen.getByTestId('alarm-banner');
    expect(banner).toHaveAttribute('role', 'alert');
    expect(banner).toHaveAttribute('data-severity', 'critical');
    expect(banner).toHaveTextContent('2 unacknowledged alarms');
    expect(banner).toHaveTextContent('3 active');
    expect(banner).toHaveTextContent('HH');
  });

  it('says so when everything is acknowledged, and when there are no alarms', () => {
    const store = seeded([
      { type: 'snapshot', snapshot: snapshot() },
      event('raised', alarm({ state: 'acknowledged' })),
    ]);
    const { unmount } = render(<AlarmBanner store={store} />);
    expect(screen.getByTestId('alarm-banner')).toHaveTextContent('All active alarms acknowledged');
    expect(screen.getByTestId('alarm-banner')).toHaveAttribute('role', 'status');
    unmount();
    render(<AlarmBanner store={seeded([{ type: 'snapshot', snapshot: snapshot() }])} />);
    expect(screen.getByTestId('alarm-banner')).toHaveTextContent('No active alarms');
  });

  it('shows state, endpoint, since, attempts and last error per link', () => {
    const store = seeded([
      {
        type: 'snapshot',
        snapshot: snapshot({
          status: status({
            modbus: conn('modbus', 'disconnected', {
              attempts: 4,
              lastError: 'connection refused',
            }),
          }),
        }),
      },
    ]);
    render(<ConnectionBadges store={store} />);
    const modbus = screen.getByTestId('link-modbus');
    expect(modbus).toHaveAttribute('data-state', 'disconnected');
    expect(modbus).toHaveTextContent('Disconnected');
    expect(modbus).toHaveTextContent('127.0.0.1:5483');
    expect(modbus).toHaveTextContent('4 failed attempts');
    expect(modbus).toHaveTextContent('Last error: connection refused');
    expect(screen.getByTestId('link-nmea')).toHaveTextContent('Connected');
  });

  it('simulator state: ok, partial, down, backend-disabled, and a first connect attempt is not "down"', () => {
    const c = (m: Connection['state'], n: Connection['state']) => ({
      modbus: conn('modbus', m),
      nmea: conn('nmea', n),
    });
    expect(simulatorState(status(), c('connected', 'connected'))).toBe('ok');
    expect(simulatorState(status(), c('connected', 'disconnected'))).toBe('partial');
    expect(simulatorState(status(), c('disconnected', 'disconnected'))).toBe('down');
    expect(simulatorState(status(), c('disabled', 'disabled'))).toBe('down');
    expect(simulatorState(status({ enabled: false }), c('disabled', 'disabled'))).toBe(
      'backend_disabled',
    );
    expect(simulatorState(status(), c('connecting', 'connecting'))).toBe('unknown');
    expect(simulatorState(null, {})).toBe('unknown');
    expect(describeSimulatorState('down', c('disconnected', 'disconnected'))?.title).toBe(
      'Simulator not running',
    );
    expect(describeSimulatorState('partial', c('disconnected', 'connected'))?.title).toBe(
      'Modbus link is down',
    );
    expect(describeSimulatorState('partial', c('connected', 'disconnected'))?.title).toBe(
      'NMEA link is down',
    );
    expect(describeSimulatorState('backend_disabled', {})?.title).toMatch(/disabled/i);
    expect(describeSimulatorState('ok', {})).toBeNull();
  });
});

// ------------------------------------------------------------------ alarm list + ack

describe('alarm states and ack error mapping', () => {
  it('has a distinct label for each alarm state', () => {
    expect(ALARM_STATE_LABELS).toEqual({
      unacknowledged: 'Unacknowledged',
      acknowledged: 'Acknowledged',
      returned_unacknowledged: 'Returned to normal, unacknowledged',
      cleared: 'Cleared',
    });
  });

  const err = (status: number, code: string, detail = '') => new ApiError(status, code, detail);
  it('maps every documented ack error to its own message', () => {
    expect(describeAckError(err(401, 'unauthorized'))).toMatchObject({ needsLogin: true });
    expect(describeAckError(err(409, 'already_acknowledged'))).toMatchObject({
      title: 'Already acknowledged',
      refresh: true,
      needsLogin: false,
    });
    expect(describeAckError(err(409, 'alarm_closed'))).toMatchObject({
      title: 'Alarm already closed',
      refresh: true,
    });
    expect(describeAckError(err(404, 'not_found')).title).toBe('Alarm not found');
    expect(describeAckError(err(400, 'invalid_request', 'bad id')).message).toBe('bad id');
    expect(describeAckError(err(401, 'something_else')).needsLogin).toBe(true);
    expect(describeAckError(err(0, 'backend_offline')).title).toBe('Backend offline');
    const titles = new Set(
      ['already_acknowledged', 'alarm_closed', 'not_found', 'unauthorized', 'invalid_request'].map(
        (c) => describeAckError(err(409, c)).title,
      ),
    );
    expect(titles.size).toBe(5);
  });
});

describe('AlarmList', () => {
  const all = [
    alarm({ id: 1, tag: 'T1', state: 'unacknowledged' }),
    alarm({ id: 2, tag: 'T2', state: 'acknowledged', acknowledgedBy: 'bob', acknowledgedAt: T0 }),
    alarm({
      id: 3,
      tag: 'T3',
      state: 'returned_unacknowledged',
      returnedAt: T0,
      returnedValue: 90,
    }),
    alarm({ id: 4, tag: 'T4', state: 'cleared', clearedAt: T0 }),
  ];

  function mount(session: boolean, routes: Parameters<typeof mockFetch>[0] = {}) {
    const store = seeded([
      { type: 'snapshot', snapshot: snapshot({ alarms: all.slice(0, 3) }) },
      { type: 'alarmList', alarms: [all[3]] },
    ]);
    const m = mockFetch({
      'GET /api/auth/me': () =>
        session ? json(makeSession('alice', ['admin'])) : problem(401, 'unauthorized', 'no'),
      'GET /api/projects': () => json([project]),
      ...routes,
    });
    renderAt('/', '/', <AlarmList store={store} />);
    return { store, ...m };
  }

  it('lists active alarms with a state label each, and an Ack button only where one is needed', async () => {
    mount(true);
    const rows = await screen.findAllByTestId(/alarm-row-/);
    expect(rows.map((r) => r.getAttribute('data-state'))).toEqual([
      'returned_unacknowledged',
      'acknowledged',
      'unacknowledged',
    ]);
    expect(within(rows[0]).getByText('Returned to normal, unacknowledged')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Acknowledged')).toBeInTheDocument();
    expect(within(rows[1]).getByText(/by bob/)).toBeInTheDocument();
    expect(within(rows[2]).getByText('Unacknowledged')).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getAllByRole('button', { name: /^Acknowledge / })).toHaveLength(2),
    );
    expect(within(rows[1]).queryByRole('button')).toBeNull();
    expect(screen.queryByTestId('alarm-row-4')).toBeNull();
  });

  it('shows "Sign in to ack" without a session and opens the login dialog', async () => {
    mount(false);
    const user = userEvent.setup();
    const buttons = await screen.findAllByRole('button', { name: /sign in to acknowledge/i });
    expect(buttons).toHaveLength(2);
    await user.click(buttons[0]);
    expect(await screen.findByRole('dialog')).toHaveTextContent('Sign in');
  });

  it('acknowledges: POSTs the id, then shows the new state', async () => {
    const { calls } = mount(true, {
      'POST /api/monitoring/alarms/1/ack': () =>
        json(
          alarm({
            id: 1,
            tag: 'T1',
            state: 'acknowledged',
            acknowledgedBy: 'alice',
            acknowledgedAt: T0,
          }),
        ),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Acknowledge T1 H' }));
    await waitFor(() =>
      expect(screen.getByTestId('alarm-row-1')).toHaveAttribute('data-state', 'acknowledged'),
    );
    expect(
      calls.some((c) => c.method === 'POST' && c.path === '/api/monitoring/alarms/1/ack'),
    ).toBe(true);
    expect(within(screen.getByTestId('alarm-row-1')).queryByRole('button')).toBeNull();
  });

  it('409 already_acknowledged: specific message, and the list is refreshed from the server', async () => {
    const { calls } = mount(true, {
      'POST /api/monitoring/alarms/1/ack': () =>
        problem(409, 'already_acknowledged', 'Already acknowledged.'),
      'GET /api/monitoring/alarms': () =>
        json({
          items: [
            alarm({ id: 1, tag: 'T1', state: 'acknowledged', acknowledgedBy: 'bob' }),
            all[1],
            all[2],
          ],
          active: 3,
          unacknowledged: 1,
        }),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Acknowledge T1 H' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Someone else acknowledged');
    await waitFor(() =>
      expect(screen.getByTestId('alarm-row-1')).toHaveAttribute('data-state', 'acknowledged'),
    );
    expect(calls.some((c) => c.path === '/api/monitoring/alarms')).toBe(true);
  });

  it('409 alarm_closed names a different reason', async () => {
    mount(true, {
      'POST /api/monitoring/alarms/3/ack': () => problem(409, 'alarm_closed', 'closed'),
      'GET /api/monitoring/alarms': () => json({ items: [], active: 0, unacknowledged: 0 }),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Acknowledge T3 H' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('has cleared in the meantime');
  });

  it('401 on ack opens the login dialog', async () => {
    mount(true, {
      'POST /api/monitoring/alarms/1/ack': () => problem(401, 'unauthorized', 'expired'),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Acknowledge T1 H' }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('Sign-in required')).toBeInTheDocument();
  });

  it('the state filter switches between Active, All and Cleared (history is fetched once)', async () => {
    const { calls } = mount(true, {
      'GET /api/monitoring/alarms': () => json({ items: all, active: 3, unacknowledged: 2 }),
    });
    const user = userEvent.setup();
    await screen.findAllByTestId(/alarm-row-/);
    await user.click(screen.getByRole('combobox'));
    await user.click(await screen.findByRole('option', { name: 'Cleared' }));
    await waitFor(() => expect(screen.getAllByTestId(/alarm-row-/)).toHaveLength(1));
    expect(screen.getByTestId('alarm-row-4')).toHaveTextContent('Cleared');
    expect(calls.filter((c) => c.path === '/api/monitoring/alarms').length).toBeGreaterThanOrEqual(
      1,
    );
    await user.click(screen.getByRole('combobox'));
    await user.click(await screen.findByRole('option', { name: 'All' }));
    await waitFor(() => expect(screen.getAllByTestId(/alarm-row-/)).toHaveLength(4));
  });
});

// ------------------------------------------------------------------ transports

function fakeHub(initial: Snapshot) {
  let handlers!: MonitoringHandlers;
  const stop = vi.fn(async () => undefined);
  const getSnapshot = vi.fn(async () => initial);
  const createHub: CreateMonitoringHub = async (h) => {
    handlers = h;
    return { getSnapshot, stop };
  };
  return { createHub, stop, getSnapshot, emit: () => handlers };
}

const restRoutes = (over: Parameters<typeof mockFetch>[0] = {}) => ({
  'GET /api/monitoring/status': () => json(status()),
  'GET /api/monitoring/values': () => json({ timestamp: T0, values: [value('ME-EXH-TT01', 300)] }),
  'GET /api/monitoring/navigation': () =>
    json({
      valid: false,
      latitude: null,
      longitude: null,
      speedKnots: null,
      courseDeg: null,
      headingDeg: null,
      timestamp: null,
    }),
  'GET /api/monitoring/alarms': () => json({ items: [alarm()], active: 1, unacknowledged: 1 }),
  ...over,
});

describe('useMonitoring transports', () => {
  it('uses the hub: catches up with GetSnapshot and applies pushed events', async () => {
    const hub = fakeHub(snapshot({ values: [value('A', 1)] }));
    const store = createMonitoringStore();
    const { result, unmount } = renderHook(() =>
      useMonitoring(store, { createHub: hub.createHub }),
    );
    await waitFor(() => expect(result.current.transport).toBe('live'));
    await waitFor(() => expect(store.getState().values.A?.value).toBe(1));
    act(() => {
      hub.emit().values({ timestamp: T0, values: [value('A', 2)] });
      hub.emit().alarmEvent({ kind: 'raised', alarm: alarm(), at: T0, value: 96 });
      hub.emit().connection(conn('modbus', 'disconnected'));
    });
    expect(store.getState().values.A.value).toBe(2);
    expect(store.getState().alarmByTag['ME-EXH-TT01']).toBeDefined();
    expect(store.getState().connections.modbus?.state).toBe('disconnected');
    unmount();
    expect(hub.stop).toHaveBeenCalled();
  });

  it('falls back to polling status, values, navigation and alarms when the hub cannot start (checking res.ok)', async () => {
    const { calls } = mockFetch(restRoutes());
    const store = createMonitoringStore();
    const createHub: CreateMonitoringHub = () => Promise.reject(new Error('401'));
    const { result, unmount } = renderHook(() =>
      useMonitoring(store, { createHub, pollMs: 20, retryMs: 60_000 }),
    );
    await waitFor(() => expect(store.getState().alarmByTag['ME-EXH-TT01']).toBeDefined());
    expect(result.current.transport).toBe('polling');
    expect(store.getState().values['ME-EXH-TT01'].value).toBe(300);
    expect(store.getState().status?.tagCount).toBe(40);
    const paths = new Set(calls.map((c) => c.path));
    expect(paths).toEqual(
      new Set([
        '/api/monitoring/status',
        '/api/monitoring/values',
        '/api/monitoring/navigation',
        '/api/monitoring/alarms',
      ]),
    );
    const n = calls.length;
    await waitFor(() => expect(calls.length).toBeGreaterThan(n)); // keeps polling
    unmount();
  });

  it('a proxied 502 while polling is an error, not data, and polling continues', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(proxy502());
    const store = createMonitoringStore();
    const createHub: CreateMonitoringHub = () => Promise.reject(new Error('x'));
    const { result } = renderHook(() =>
      useMonitoring(store, { createHub, pollMs: 20, retryMs: 60_000 }),
    );
    await waitFor(() => expect(result.current.error).toBeInstanceOf(ApiError));
    expect((result.current.error as ApiError).isOffline).toBe(true);
    expect(store.getState().loaded).toBe(false);
    const n = spy.mock.calls.length;
    await waitFor(() => expect(spy.mock.calls.length).toBeGreaterThan(n));
  });

  it('polls while the hub reconnects and goes back to live (with a fresh snapshot) afterwards', async () => {
    mockFetch(restRoutes());
    const hub = fakeHub(snapshot({ values: [value('B', 7)] }));
    const store = createMonitoringStore();
    const { result } = renderHook(() =>
      useMonitoring(store, { createHub: hub.createHub, pollMs: 20 }),
    );
    await waitFor(() => expect(result.current.transport).toBe('live'));
    act(() => hub.emit().reconnecting());
    await waitFor(() => expect(result.current.transport).toBe('polling'));
    await waitFor(() => expect(store.getState().values['ME-EXH-TT01']?.value).toBe(300));
    act(() => hub.emit().reconnected());
    await waitFor(() => expect(result.current.transport).toBe('live'));
    expect(hub.getSnapshot.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('retries the hub in the background after a failed start and switches to live', async () => {
    mockFetch(restRoutes());
    const hub = fakeHub(snapshot());
    let attempts = 0;
    const createHub: CreateMonitoringHub = (h) => {
      attempts += 1;
      return attempts < 2 ? Promise.reject(new Error('401')) : hub.createHub(h);
    };
    const store = createMonitoringStore();
    const { result } = renderHook(() =>
      useMonitoring(store, { createHub, pollMs: 20, retryMs: 30 }),
    );
    await waitFor(() => expect(result.current.transport).toBe('live'));
    expect(attempts).toBe(2);
  });

  it('reconnect backoff grows 0, 1, 2, 4, 8 s and caps at 10 s', () => {
    expect([0, 1, 2, 3, 4, 5, 6, 20].map(reconnectDelay)).toEqual([
      0, 1000, 2000, 4000, 8000, 10_000, 10_000, 10_000,
    ]);
  });
});

// ------------------------------------------------------------------ the page

describe('MonitoringPage', () => {
  const pageRoutes = (st: MonitoringStatus, over: Parameters<typeof mockFetch>[0] = {}) => ({
    'GET /api/auth/me': () => json(makeSession('alice', ['admin'])),
    'GET /api/projects': () => json([project]),
    'GET /api/projects/1/mimic': () => json(mimicFixture),
    ...restRoutes({ 'GET /api/monitoring/status': () => json(st) }),
    ...over,
  });

  it('renders the mimic, live values, connection badges and the alarm list from the hub', async () => {
    mockFetch(pageRoutes(status()));
    const hub = fakeHub(
      snapshot({ values: [value('ME-EXH-TT01', 412.3)], alarms: [alarm({ tag: 'ME-EXH-TT01' })] }),
    );
    renderAt('/monitoring', '/monitoring', <MonitoringPage createHub={hub.createHub} />);
    expect(await screen.findByTestId('mimic-svg')).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByTestId('widget-b-me-exh-tt01')).toHaveAttribute('data-tone', 'warning'),
    );
    expect(screen.getByTestId('widget-b-me-exh-tt01')).toHaveTextContent('412.3');
    expect(screen.getByTestId('alarm-banner')).toHaveTextContent('1 unacknowledged alarm');
    expect(screen.getByTestId('link-modbus')).toHaveTextContent('Connected');
    expect(screen.getByTestId('alarm-row-1')).toBeInTheDocument();
    expect(screen.queryByTestId('simulator-state')).toBeNull();
    expect(screen.getByTestId('transport')).toHaveAttribute('data-transport', 'live');
    // a push updates the same widget
    act(() => hub.emit().values({ timestamp: T0, values: [value('ME-EXH-TT01', 430)] }));
    expect(screen.getByTestId('widget-b-me-exh-tt01')).toHaveTextContent('430.0');
  });

  it('shows the specific "Simulator not running" state when both links are down', async () => {
    const down = status({
      modbus: conn('modbus', 'disconnected', { attempts: 5, lastError: 'connection refused' }),
      nmea: conn('nmea', 'disconnected', { attempts: 5, lastError: 'connection refused' }),
    });
    mockFetch(pageRoutes(down));
    const hub = fakeHub(snapshot({ status: down, values: [value('ME-EXH-TT01', 300)] }));
    renderAt('/monitoring', '/monitoring', <MonitoringPage createHub={hub.createHub} />);
    const box = await screen.findByTestId('simulator-state');
    expect(box).toHaveAttribute('data-state', 'down');
    expect(box).toHaveTextContent('Simulator not running');
    expect(box).toHaveTextContent('pnpm run sim');
    await waitFor(() =>
      expect(screen.getByTestId('widget-b-me-exh-tt01')).toHaveAttribute('data-tone', 'stale'),
    );
    expect(screen.getByTestId('link-modbus')).toHaveTextContent('Last error: connection refused');
  });

  it('works without a hub (no session): polls over HTTP and says so', async () => {
    mockFetch(pageRoutes(status()));
    const createHub: CreateMonitoringHub = () => Promise.reject(new Error('401'));
    renderAt(
      '/monitoring',
      '/monitoring',
      <MonitoringPage createHub={createHub} pollMs={20} retryMs={60_000} />,
    );
    await waitFor(() =>
      expect(screen.getByTestId('widget-b-me-exh-tt01')).toHaveTextContent('300.0'),
    );
    expect(screen.getByTestId('transport')).toHaveAttribute('data-transport', 'polling');
    expect(screen.getByTestId('transport')).toHaveTextContent(/over HTTP/i);
  });

  it('shows a specific message when the mimic layout is missing or invalid', async () => {
    mockFetch(
      pageRoutes(status(), {
        'GET /api/projects/1/mimic': () => problem(404, 'mimic_not_found', 'missing'),
      }),
    );
    const hub = fakeHub(snapshot());
    renderAt('/monitoring', '/monitoring', <MonitoringPage createHub={hub.createHub} />);
    expect(await screen.findByText('No mimic layout')).toBeInTheDocument();
  });

  it('reports an offline backend instead of an empty screen', async () => {
    mockFetch(pageRoutes(status(), {}));
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input);
      if (url.includes('/api/projects') && !url.includes('mimic')) return json([project]);
      if (url.includes('/api/auth/me')) return json(makeSession('alice', ['admin']));
      return proxy502();
    });
    const createHub: CreateMonitoringHub = () => Promise.reject(new Error('down'));
    renderAt(
      '/monitoring',
      '/monitoring',
      <MonitoringPage createHub={createHub} pollMs={20} retryMs={60_000} />,
    );
    expect(await screen.findByText(/backend is not reachable/i)).toBeInTheDocument();
  });
});
