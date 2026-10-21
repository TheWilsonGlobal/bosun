import { memo } from 'react';
import { cn } from '@/lib/utils';
import { useMonitoringSelector, type MonitoringStore } from './store';
import type { MimicBinding, MimicTagInfo } from './types';
import {
  appearanceOf,
  fraction,
  formatNumber,
  navigationText,
  type Appearance,
  type Tone,
} from './widgetLogic';

// One live widget per binding. Each subscribes to exactly its own tag (value, open alarm) and to the link state, so a
// values batch re-renders only the widgets whose tag changed. They are memoised on (binding, info, store), which never
// change after the mimic loads.

interface WidgetProps {
  store: MonitoringStore;
  binding: MimicBinding;
  info?: MimicTagInfo;
}

const BOX: Record<Tone, string> = {
  normal: 'fill-card stroke-border',
  warning: 'fill-warning/30 stroke-warning',
  critical: 'fill-destructive/20 stroke-destructive',
  stale: 'fill-muted stroke-muted-foreground/50',
};
const BAR: Record<Tone, string> = {
  normal: 'fill-primary',
  warning: 'fill-warning',
  critical: 'fill-destructive',
  stale: 'fill-muted-foreground/40',
};
const TEXT: Record<Tone, string> = {
  normal: 'fill-foreground',
  warning: 'fill-foreground',
  critical: 'fill-foreground',
  stale: 'fill-muted-foreground',
};

/** Triangle (filled = critical, outline = warning) plus the level text: the alarm is readable without colour. */
function Marker({ appearance, x, y }: { appearance: Appearance; x: number; y: number }) {
  if (!appearance.marker) return null;
  const critical = appearance.severity === 'critical';
  return (
    <g data-testid="alarm-marker" transform={`translate(${x} ${y})`}>
      <polygon
        points="0,-7 7,6 -7,6"
        className={cn(
          'stroke-foreground',
          critical ? 'fill-destructive' : 'fill-warning',
          !critical && 'fill-transparent',
        )}
        strokeWidth={1.5}
        strokeLinejoin="round"
      />
      <text y={4} textAnchor="middle" fontSize={8} fontWeight={700} className="fill-foreground">
        !
      </text>
    </g>
  );
}

function useWidgetState(store: MonitoringStore, tag: string | undefined, link: 'modbus' | 'nmea') {
  const value = useMonitoringSelector(store, (s) => (tag ? s.values[tag] : undefined));
  const alarm = useMonitoringSelector(store, (s) => (tag ? s.alarmByTag[tag] : undefined));
  const linkUp = useMonitoringSelector(store, (s) => s.connections[link]?.state === 'connected');
  return { value, alarm, linkUp, appearance: appearanceOf(value, alarm, linkUp) };
}

function Frame({
  binding,
  appearance,
  children,
  title,
}: {
  binding: MimicBinding;
  appearance: Appearance;
  children: React.ReactNode;
  title: string;
}) {
  return (
    <g
      data-testid={`widget-${binding.id}`}
      data-tone={appearance.tone}
      data-blink={appearance.blink ? 'true' : 'false'}
      transform={`translate(${binding.x} ${binding.y})`}
    >
      <title>{title}</title>
      <rect
        width={binding.width}
        height={binding.height}
        rx={5}
        strokeWidth={appearance.severity && !appearance.stale ? 2.5 : 1.25}
        strokeDasharray={appearance.stale ? '4 3' : undefined}
        className={cn(BOX[appearance.tone], appearance.blink && 'mimic-blink')}
      />
      {children}
    </g>
  );
}

function describe(
  binding: MimicBinding,
  text: string,
  appearance: Appearance,
  info?: MimicTagInfo,
) {
  return [
    `${binding.label}${binding.tag ? ` (${binding.tag})` : ''}: ${text}`,
    info?.description,
    appearance.stale ? 'No live data (stale)' : null,
    appearance.marker ? `Alarm ${appearance.marker}` : null,
  ]
    .filter(Boolean)
    .join('. ');
}

function ValueWidgetBase({ store, binding, info }: WidgetProps) {
  const { value, alarm, appearance } = useWidgetState(store, binding.tag, 'modbus');
  const unit = value?.unit ?? info?.unit ?? '';
  const text = formatNumber(value?.value, binding.decimals ?? 1);
  const footer = alarm
    ? `${alarm.level} ${formatNumber(alarm.limitValue, binding.decimals ?? 1)}`
    : binding.showLimits && info && info.rangeLow !== null && info.rangeHigh !== null
      ? `${info.rangeLow}–${info.rangeHigh}`
      : null;
  const t = TEXT[appearance.tone];
  return (
    <Frame
      binding={binding}
      appearance={appearance}
      title={describe(binding, `${text} ${unit}`.trim(), appearance, info)}
    >
      <text x={6} y={13} fontSize={10} className="fill-muted-foreground">
        {binding.label}
      </text>
      <text x={6} y={34} fontSize={16} fontWeight={700} className={cn(t, 'tabular-nums')}>
        {text}
        {value && unit ? (
          <tspan fontSize={9} fontWeight={400} dx={3}>
            {unit}
          </tspan>
        ) : null}
      </text>
      {footer && (
        <text x={6} y={binding.height - 5} fontSize={9} className={t}>
          {footer}
          {alarm && appearance.marker ? ` ${appearance.marker.split(' ').slice(1).join(' ')}` : ''}
        </text>
      )}
      <Marker appearance={appearance} x={binding.width - 11} y={12} />
    </Frame>
  );
}

function BarWidgetBase({ store, binding, info }: WidgetProps) {
  const { value, appearance } = useWidgetState(store, binding.tag, 'modbus');
  const unit = value?.unit ?? info?.unit ?? '';
  const text = formatNumber(value?.value, binding.decimals ?? 1);
  const f = fraction(value?.value, info?.rangeLow ?? null, info?.rangeHigh ?? null);
  const vertical = binding.orientation === 'vertical';
  const pad = 6;
  const trackW = binding.width - 2 * pad;
  const trackH = vertical ? binding.height - 34 : 8;
  const trackY = vertical ? 20 : binding.height - 14;
  return (
    <Frame
      binding={binding}
      appearance={appearance}
      title={describe(binding, `${text} ${unit}`.trim(), appearance, info)}
    >
      <text x={pad} y={13} fontSize={10} className="fill-muted-foreground">
        {binding.label}
      </text>
      <text
        x={binding.width - pad - (appearance.marker ? 14 : 0)}
        y={13}
        fontSize={11}
        fontWeight={700}
        textAnchor="end"
        className={cn(TEXT[appearance.tone], 'tabular-nums')}
      >
        {text} {value ? unit : ''}
      </text>
      <rect
        x={pad}
        y={trackY}
        width={vertical ? 14 : trackW}
        height={trackH}
        rx={3}
        className="fill-muted stroke-border"
      />
      {vertical ? (
        <rect
          x={pad}
          y={trackY + trackH * (1 - f)}
          width={14}
          height={trackH * f}
          rx={3}
          className={BAR[appearance.tone]}
        />
      ) : (
        <rect
          x={pad}
          y={trackY}
          width={trackW * f}
          height={trackH}
          rx={3}
          className={BAR[appearance.tone]}
        />
      )}
      <Marker appearance={appearance} x={binding.width - 8} y={11} />
    </Frame>
  );
}

function TankWidgetBase({ store, binding, info }: WidgetProps) {
  const { value, appearance } = useWidgetState(store, binding.tag, 'modbus');
  const text = formatNumber(value?.value, binding.decimals ?? 0);
  const f = fraction(value?.value, info?.rangeLow ?? 0, info?.rangeHigh ?? 100);
  const top = 30;
  const h = binding.height - top - 8;
  const w = binding.width - 16;
  return (
    <Frame
      binding={binding}
      appearance={appearance}
      title={describe(binding, `${text} ${value?.unit ?? info?.unit ?? '%'}`, appearance, info)}
    >
      <text
        x={binding.width / 2}
        y={13}
        fontSize={10}
        textAnchor="middle"
        className="fill-muted-foreground"
      >
        {binding.label}
      </text>
      <text
        x={binding.width / 2}
        y={25}
        fontSize={11}
        fontWeight={700}
        textAnchor="middle"
        className={cn(TEXT[appearance.tone], 'tabular-nums')}
      >
        {text}
        {value ? (value.unit ?? '%') : ''}
      </text>
      <rect x={8} y={top} width={w} height={h} rx={3} className="fill-muted stroke-border" />
      <rect
        x={8}
        y={top + h * (1 - f)}
        width={w}
        height={h * f}
        rx={3}
        data-testid={`tank-fill-${binding.id}`}
        className={BAR[appearance.tone]}
      />
      <Marker appearance={appearance} x={binding.width - 9} y={11} />
    </Frame>
  );
}

function NavigationWidgetBase({ store, binding }: WidgetProps) {
  const nav = useMonitoringSelector(store, (s) => s.navigation);
  const linkUp = useMonitoringSelector(store, (s) => s.connections.nmea?.state === 'connected');
  const t = navigationText(nav, linkUp);
  const appearance = appearanceOf(undefined, undefined, linkUp);
  const cell = (label: string, text: string, x: number) => (
    <g transform={`translate(${x} 0)`}>
      <text y={15} fontSize={9} className="fill-muted-foreground">
        {label}
      </text>
      <text
        y={34}
        fontSize={14}
        fontWeight={700}
        className={cn(TEXT[appearance.tone], 'tabular-nums')}
      >
        {text}
      </text>
    </g>
  );
  return (
    <Frame
      binding={binding}
      appearance={appearance}
      title={t.unavailable ?? `${binding.label}: ${t.position}, ${t.speed}, heading ${t.heading}`}
    >
      <g transform="translate(10 0)">
        {t.unavailable ? (
          <>
            <text y={15} fontSize={9} className="fill-muted-foreground">
              {binding.label}
            </text>
            <text y={34} fontSize={14} fontWeight={700} className={TEXT[appearance.tone]}>
              {t.unavailable}
              {t.heading !== '--' ? `  HDG ${t.heading}` : ''}
            </text>
          </>
        ) : (
          <>
            {cell('POSITION', t.position, 0)}
            {cell('SPEED', t.speed, 330)}
            {cell('COURSE', t.course, 440)}
            {cell('HEADING', t.heading, 540)}
          </>
        )}
      </g>
    </Frame>
  );
}

export const ValueWidget = memo(ValueWidgetBase);
export const BarWidget = memo(BarWidgetBase);
export const TankWidget = memo(TankWidgetBase);
export const NavigationWidget = memo(NavigationWidgetBase);

export function Widget(props: WidgetProps) {
  switch (props.binding.kind) {
    case 'value':
      return <ValueWidget {...props} />;
    case 'bar':
      return <BarWidget {...props} />;
    case 'tank':
      return <TankWidget {...props} />;
    case 'navigation':
      return <NavigationWidget {...props} />;
    default:
      return null;
  }
}
