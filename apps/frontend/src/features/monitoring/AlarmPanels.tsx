import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { AlertOctagon, AlertTriangle, BellOff, CheckCircle2, Plug, PlugZap } from 'lucide-react';
import { toast } from 'sonner';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { useAuth } from '@/features/auth/session';
import { cn } from '@/lib/utils';
import { ackAlarm, fetchAlarms } from './api';
import { ALARM_STATE_LABELS, describeAckError, LEVEL_LABELS, LINK_LABELS } from './messages';
import {
  alarmSummary,
  isCritical,
  isOpen,
  needsAck,
  useMonitoringSelector,
  type MonitoringStore,
} from './store';
import type { Alarm, AlarmFilter, Connection, LinkName, LinkState } from './types';
import { formatNumber } from './widgetLogic';

const time = (iso: string | null) =>
  iso
    ? new Date(iso).toLocaleTimeString([], {
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        hour12: false,
      })
    : '--';

// ---------------------------------------------------------------- banner

export function AlarmBanner({ store }: { store: MonitoringStore }) {
  const alarms = useMonitoringSelector(store, (s) => s.alarms);
  const loaded = useMonitoringSelector(store, (s) => s.loaded);
  const sum = useMemo(() => alarmSummary(alarms), [alarms]);

  if (!loaded) {
    return (
      <div role="status" className="text-muted-foreground rounded-md border px-3 py-2 text-sm">
        Waiting for the first data from the backend...
      </div>
    );
  }
  if (sum.open === 0) {
    return (
      <div
        role="status"
        data-testid="alarm-banner"
        data-severity="none"
        className="border-success/50 bg-success/10 flex items-center gap-2 rounded-md border px-3 py-2 text-sm"
      >
        <CheckCircle2 className="text-success size-4" aria-hidden />
        No active alarms.
      </div>
    );
  }
  const worst = sum.highestUnacked ?? sum.highest;
  const critical = worst ? isCritical(worst) : false;
  const Icon = critical ? AlertOctagon : AlertTriangle;
  return (
    <div
      role={sum.unacknowledged > 0 ? 'alert' : 'status'}
      data-testid="alarm-banner"
      data-severity={critical ? 'critical' : 'warning'}
      data-unacknowledged={sum.unacknowledged}
      className={cn(
        'flex flex-wrap items-center gap-x-3 gap-y-1 rounded-md border px-3 py-2 text-sm',
        critical ? 'border-destructive/60 bg-destructive/10' : 'border-warning bg-warning/20',
      )}
    >
      <Icon
        className={cn('size-4', critical ? 'text-destructive' : 'text-warning-foreground')}
        aria-hidden
      />
      <span className="font-semibold">
        {sum.unacknowledged > 0
          ? `${sum.unacknowledged} unacknowledged alarm${sum.unacknowledged === 1 ? '' : 's'}`
          : 'All active alarms acknowledged'}
      </span>
      <span>
        {sum.open} active
        {worst && (
          <>
            , highest severity <strong>{worst}</strong> ({LEVEL_LABELS[worst]}
            {critical ? ', critical' : ''})
          </>
        )}
      </span>
    </div>
  );
}

// ---------------------------------------------------------------- connections

const LINK_BADGE: Record<LinkState, { label: string; cls: string }> = {
  connected: { label: 'Connected', cls: 'border-success/60 text-success' },
  connecting: { label: 'Connecting', cls: 'border-warning text-warning-foreground' },
  disconnected: { label: 'Disconnected', cls: 'border-destructive/60 text-destructive' },
  disabled: { label: 'Disabled', cls: 'text-muted-foreground' },
};

function LinkBadge({ name, connection }: { name: LinkName; connection?: Connection }) {
  const state = connection?.state;
  const meta = state ? LINK_BADGE[state] : { label: 'Unknown', cls: 'text-muted-foreground' };
  const Icon = state === 'connected' ? PlugZap : Plug;
  const detail = connection
    ? [
        connection.endpoint,
        `since ${time(connection.since)}`,
        connection.attempts > 0
          ? `${connection.attempts} failed attempt${connection.attempts === 1 ? '' : 's'}`
          : null,
      ]
        .filter(Boolean)
        .join(', ')
    : 'no data yet';
  return (
    <div className="flex flex-col" data-testid={`link-${name}`} data-state={state ?? 'unknown'}>
      <Badge variant="outline" className={cn('w-fit gap-1.5 font-normal', meta.cls)}>
        <Icon className="size-3.5" aria-hidden />
        <span className="font-semibold">{LINK_LABELS[name]}</span>
        {meta.label}
      </Badge>
      <span className="text-muted-foreground mt-0.5 text-xs">{detail}</span>
      {connection?.lastError && state !== 'connected' && (
        <span className="text-destructive max-w-72 truncate text-xs" title={connection.lastError}>
          Last error: {connection.lastError}
        </span>
      )}
    </div>
  );
}

export function ConnectionBadges({ store }: { store: MonitoringStore }) {
  const connections = useMonitoringSelector(store, (s) => s.connections);
  return (
    <div className="flex flex-wrap gap-x-6 gap-y-2" aria-label="Source connections">
      <LinkBadge name="modbus" connection={connections.modbus} />
      <LinkBadge name="nmea" connection={connections.nmea} />
    </div>
  );
}

// ---------------------------------------------------------------- list

const STATE_BADGE: Record<Alarm['state'], string> = {
  unacknowledged: 'border-destructive/60 text-destructive',
  acknowledged: 'border-warning text-warning-foreground',
  returned_unacknowledged: 'border-primary/60 text-primary',
  cleared: 'text-muted-foreground',
};

export const FILTER_LABELS: Record<AlarmFilter, string> = {
  active: 'Active',
  all: 'All',
  cleared: 'Cleared',
};

export function filterAlarms(alarms: Record<number, Alarm>, filter: AlarmFilter): Alarm[] {
  return Object.values(alarms)
    .filter((a) => (filter === 'active' ? isOpen(a) : filter === 'cleared' ? !isOpen(a) : true))
    .sort((a, b) => b.id - a.id);
}

export function AlarmList({ store }: { store: MonitoringStore }) {
  const { session, openLogin } = useAuth();
  const [filter, setFilter] = useState<AlarmFilter>('active');
  const [ackingId, setAckingId] = useState<number | null>(null);
  const [ackError, setAckError] = useState<ReturnType<typeof describeAckError> | null>(null);
  const alarms = useMonitoringSelector(store, (s) => s.alarms);
  const rows = useMemo(() => filterAlarms(alarms, filter), [alarms, filter]);

  // The store already holds the open alarms (snapshot/events). History needs one REST fetch per switch.
  const history = useQuery({
    queryKey: ['monitoring-alarms', filter],
    queryFn: () => fetchAlarms('all', 500),
    enabled: filter !== 'active',
    staleTime: 0,
  });
  const historyAlarms = history.data?.items;
  useEffect(() => {
    if (historyAlarms) store.dispatch({ type: 'alarmList', alarms: historyAlarms });
  }, [historyAlarms, store]);

  const refreshFromServer = async () => {
    try {
      const list = await fetchAlarms('all', 500);
      store.dispatch({ type: 'openAlarms', alarms: list.items.filter(isOpen) });
      store.dispatch({ type: 'alarmList', alarms: list.items.filter((a) => !isOpen(a)) });
    } catch {
      /* the connection banner reports an offline backend */
    }
  };

  const ack = useMutation({
    mutationFn: (id: number) => ackAlarm(id),
    onMutate: (id) => {
      setAckingId(id);
      setAckError(null);
    },
    onSuccess: (alarm) => {
      store.dispatch({ type: 'alarmList', alarms: [alarm] });
      toast.success(`Alarm ${alarm.tag} acknowledged`);
    },
    onError: (e) => {
      const m = describeAckError(e);
      setAckError(m);
      if (m.needsLogin) openLogin();
      if (m.refresh) void refreshFromServer();
    },
    onSettled: () => setAckingId(null),
  });

  return (
    <section aria-label="Alarm list" className="grid gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-base font-semibold">Alarms</h2>
        <label htmlFor="alarm-filter" className="text-muted-foreground ml-auto text-sm">
          Show
        </label>
        <Select value={filter} onValueChange={(v) => setFilter(v as AlarmFilter)}>
          <SelectTrigger id="alarm-filter" size="sm" className="w-32">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {(Object.keys(FILTER_LABELS) as AlarmFilter[]).map((f) => (
              <SelectItem key={f} value={f}>
                {FILTER_LABELS[f]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {!session && rows.some(needsAck) && (
        <p className="text-muted-foreground text-xs">Sign in to acknowledge alarms.</p>
      )}
      {ackError && (
        <div
          role="alert"
          className="border-destructive/50 bg-destructive/10 rounded-md border px-3 py-2 text-sm"
        >
          <p className="font-semibold">{ackError.title}</p>
          <p>{ackError.message}</p>
        </div>
      )}
      {history.isError && filter !== 'active' && (
        <p role="alert" className="text-destructive text-sm">
          Could not load alarm history. The list below only shows what this page has seen.
        </p>
      )}

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Raised</TableHead>
            <TableHead>Tag</TableHead>
            <TableHead>Level</TableHead>
            <TableHead className="text-right">Limit</TableHead>
            <TableHead className="text-right">Value / peak</TableHead>
            <TableHead>State</TableHead>
            <TableHead className="text-right">
              <span className="sr-only">Action</span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.length === 0 && (
            <TableRow>
              <TableCell colSpan={7} className="text-muted-foreground py-6 text-center">
                {filter === 'active'
                  ? 'No active alarms.'
                  : filter === 'cleared'
                    ? 'No cleared alarms yet.'
                    : 'No alarms yet.'}
              </TableCell>
            </TableRow>
          )}
          {rows.map((a) => (
            <TableRow key={a.id} data-testid={`alarm-row-${a.id}`} data-state={a.state}>
              <TableCell className="tabular-nums">{time(a.raisedAt)}</TableCell>
              <TableCell>
                <span className="font-medium">{a.tag}</span>
                {a.description && (
                  <span
                    className="text-muted-foreground block max-w-56 truncate text-xs"
                    title={a.description}
                  >
                    {a.description}
                  </span>
                )}
              </TableCell>
              <TableCell>
                <span className={cn('font-semibold', isCritical(a.level) && 'text-destructive')}>
                  {a.level}
                </span>
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {formatNumber(a.limitValue)} {a.unit ?? ''}
                <span className="text-muted-foreground block text-xs">delay {a.delayS} s</span>
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {formatNumber(a.raisedValue)} / {formatNumber(a.peakValue)} {a.unit ?? ''}
              </TableCell>
              <TableCell>
                <Badge variant="outline" className={cn('font-normal', STATE_BADGE[a.state])}>
                  {ALARM_STATE_LABELS[a.state]}
                </Badge>
                {a.acknowledgedBy && (
                  <span className="text-muted-foreground block text-xs">
                    by {a.acknowledgedBy} at {time(a.acknowledgedAt)}
                  </span>
                )}
              </TableCell>
              <TableCell className="text-right">
                {needsAck(a) &&
                  (session ? (
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={ackingId === a.id}
                      onClick={() => ack.mutate(a.id)}
                      aria-label={`Acknowledge ${a.tag} ${a.level}`}
                    >
                      <BellOff aria-hidden />
                      {ackingId === a.id ? 'Acknowledging...' : 'Ack'}
                    </Button>
                  ) : (
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={openLogin}
                      aria-label={`Sign in to acknowledge ${a.tag} ${a.level}`}
                    >
                      Sign in to ack
                    </Button>
                  ))}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </section>
  );
}
