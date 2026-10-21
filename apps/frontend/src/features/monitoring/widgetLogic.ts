import { isCritical } from './store';
import type { Alarm, LiveValue, Navigation } from './types';

// Pure decisions for the live widgets: colour, blinking and the non-colour markers. Kept out of the components so they
// are unit tested without rendering.

export type Tone = 'normal' | 'warning' | 'critical' | 'stale';

export interface Appearance {
  tone: Tone;
  /** Grey because the value is stale or the Modbus link is down. An open alarm is still marked. */
  stale: boolean;
  /** Unacknowledged alarms blink (CSS drops the animation under prefers-reduced-motion; the marker text stays). */
  blink: boolean;
  /** Text marker so the state is never conveyed by colour alone, e.g. "HH UNACK". */
  marker: string | null;
  severity: 'critical' | 'warning' | null;
}

export const STATE_WORD: Record<Alarm['state'], string> = {
  unacknowledged: 'UNACK',
  acknowledged: 'ACK',
  returned_unacknowledged: 'RTN',
  cleared: '',
};

export function appearanceOf(
  value: LiveValue | undefined,
  alarm: Alarm | undefined,
  linkUp: boolean,
): Appearance {
  const stale = !linkUp || value?.quality === 'stale';
  const severity = alarm ? (isCritical(alarm.level) ? 'critical' : 'warning') : null;
  const marker = alarm ? `${alarm.level} ${STATE_WORD[alarm.state]}`.trim() : null;
  const tone: Tone = stale ? 'stale' : (severity ?? 'normal');
  return {
    tone,
    stale,
    // A returned alarm is no longer in alarm: it keeps its marker but does not blink.
    blink: !!alarm && alarm.state === 'unacknowledged' && !stale,
    marker,
    severity,
  };
}

export function formatNumber(value: number | undefined | null, decimals = 1): string {
  if (value === undefined || value === null || !Number.isFinite(value)) return '--';
  return value.toFixed(decimals);
}

/** 0..1 position of `value` in [low, high]; a missing or empty range falls back to 0..100. */
export function fraction(value: number | undefined, low: number | null, high: number | null) {
  if (value === undefined || !Number.isFinite(value)) return 0;
  const lo = low ?? 0;
  const hi = high !== null && high > lo ? high : lo + 100;
  return Math.min(1, Math.max(0, (value - lo) / (hi - lo)));
}

function dm(value: number, pos: string, neg: string, degWidth: number): string {
  const hemi = value >= 0 ? pos : neg;
  const abs = Math.abs(value);
  const deg = Math.floor(abs);
  const min = (abs - deg) * 60;
  return `${String(deg).padStart(degWidth, '0')}°${min.toFixed(3).padStart(6, '0')}'${hemi}`;
}

export const formatLatitude = (v: number) => dm(v, 'N', 'S', 2);
export const formatLongitude = (v: number) => dm(v, 'E', 'W', 3);

export interface NavigationText {
  /** null when there is a fix to show. */
  unavailable: string | null;
  position: string;
  speed: string;
  course: string;
  heading: string;
}

export function navigationText(nav: Navigation | null, linkUp: boolean): NavigationText {
  const blank = { position: '--', speed: '--', course: '--', heading: '--' };
  if (!linkUp) return { unavailable: 'NMEA not connected', ...blank };
  if (!nav) return { unavailable: 'Waiting for the first NMEA sentence', ...blank };
  const heading = nav.headingDeg === null ? '--' : `${nav.headingDeg.toFixed(0)}°`;
  if (!nav.valid || nav.latitude === null || nav.longitude === null) {
    return { unavailable: 'No position fix', ...blank, heading };
  }
  return {
    unavailable: null,
    position: `${formatLatitude(nav.latitude)} ${formatLongitude(nav.longitude)}`,
    speed: nav.speedKnots === null ? '--' : `${nav.speedKnots.toFixed(1)} kn`,
    course: nav.courseDeg === null ? '--' : `${nav.courseDeg.toFixed(0)}°`,
    heading,
  };
}
