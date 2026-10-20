import type {
  AlarmLevel,
  AlarmLimitValue,
  ChangeOp,
  SafetyClass,
  SetAlarmLimitOp,
  Tag,
  UpdateField,
  UpdateTagOp,
} from './api/types';

// Inline grid edits never write directly. They accumulate here and are turned into change-set ops on Review.
// Each edit captures the value that was DISPLAYED when it was first touched as `old`, so the backend can
// answer 409 concurrency_conflict if the tag changed in the meantime.

export interface FieldEdit {
  key: string;
  kind: 'field';
  tagId: number;
  tagName: string;
  field: UpdateField;
  old: string | null;
  new: string | null;
}

export interface LimitEdit {
  key: string;
  kind: 'limit';
  tagId: number;
  tagName: string;
  level: AlarmLevel;
  old: AlarmLimitValue | null;
  new: AlarmLimitValue | null;
}

export type PendingEdit = FieldEdit | LimitEdit;
export type PendingEdits = ReadonlyMap<string, PendingEdit>;

export const EDITABLE_FIELDS: UpdateField[] = [
  'description',
  'unit',
  'rangeLow',
  'rangeHigh',
  'safetyClass',
];

export const fieldKey = (tagId: number, field: string) => `${tagId}:f:${field}`;
export const limitKey = (tagId: number, level: AlarmLevel) => `${tagId}:l:${level}`;

/** The string an updateTag op carries for this field (numbers use the invariant culture; empty = null). */
export function fieldValue(tag: Tag, field: UpdateField): string | null {
  const raw = tag[field];
  if (raw === null || raw === undefined || raw === '') return null;
  return String(raw);
}

export function limitValue(tag: Tag, level: AlarmLevel): AlarmLimitValue | null {
  const found = tag.alarmLimits.find((l) => l.level === level);
  return found ? { value: found.value, delayS: found.delayS, hysteresis: found.hysteresis } : null;
}

const normText = (v: string | null) => {
  const t = v?.trim() ?? '';
  return t === '' ? null : t;
};

function sameField(field: UpdateField, a: string | null, b: string | null): boolean {
  const x = normText(a);
  const y = normText(b);
  if (x === null || y === null) return x === y;
  if (field === 'rangeLow' || field === 'rangeHigh') return Number(x) === Number(y);
  return x === y;
}

const sameLimit = (a: AlarmLimitValue | null, b: AlarmLimitValue | null) =>
  a === null || b === null
    ? a === b
    : a.value === b.value && a.delayS === b.delayS && a.hysteresis === b.hysteresis;

/** Records (or updates, or cancels) an edit of a scalar field. Returns a new map. */
export function setFieldEdit(
  edits: PendingEdits,
  tag: Tag,
  field: UpdateField,
  next: string | null,
): PendingEdits {
  const key = fieldKey(tag.id, field);
  const existing = edits.get(key) as FieldEdit | undefined;
  const old = existing ? existing.old : fieldValue(tag, field);
  const out = new Map(edits);
  const value = normText(next);
  if (sameField(field, old, value)) out.delete(key);
  else
    out.set(key, {
      key,
      kind: 'field',
      tagId: tag.id,
      tagName: tag.tagName,
      field,
      old,
      new: value,
    });
  return out;
}

/** `next` null removes the limit. */
export function setLimitEdit(
  edits: PendingEdits,
  tag: Tag,
  level: AlarmLevel,
  next: AlarmLimitValue | null,
): PendingEdits {
  const key = limitKey(tag.id, level);
  const existing = edits.get(key) as LimitEdit | undefined;
  const old = existing ? existing.old : limitValue(tag, level);
  const out = new Map(edits);
  if (sameLimit(old, next)) out.delete(key);
  else
    out.set(key, {
      key,
      kind: 'limit',
      tagId: tag.id,
      tagName: tag.tagName,
      level,
      old,
      new: next,
    });
  return out;
}

/** The value the grid should show for a field: pending edit if any, else the server value. */
export function effectiveField(tag: Tag, edits: PendingEdits, field: UpdateField): string | null {
  const e = edits.get(fieldKey(tag.id, field)) as FieldEdit | undefined;
  return e ? e.new : fieldValue(tag, field);
}

export function effectiveLimit(
  tag: Tag,
  edits: PendingEdits,
  level: AlarmLevel,
): AlarmLimitValue | null {
  const e = edits.get(limitKey(tag.id, level)) as LimitEdit | undefined;
  return e ? e.new : limitValue(tag, level);
}

/**
 * Builds the ops. `old` is ALWAYS sent explicitly (null included): the backend treats an omitted `old` as
 * "take the current value", which would defeat stale-edit detection.
 */
export function editsToOps(edits: Iterable<PendingEdit>): ChangeOp[] {
  const ops: ChangeOp[] = [];
  for (const e of edits) {
    if (e.kind === 'field') {
      const op: UpdateTagOp = {
        type: 'updateTag',
        tagName: e.tagName,
        field: e.field,
        old: e.old,
        new: e.new,
      };
      ops.push(op);
    } else {
      const op: SetAlarmLimitOp = {
        type: 'setAlarmLimit',
        tagName: e.tagName,
        level: e.level,
        old: e.old,
        new: e.new,
      };
      ops.push(op);
    }
  }
  return ops;
}

export function tagsTouched(edits: Iterable<PendingEdit>): string[] {
  return [...new Set([...edits].map((e) => e.tagName))];
}

// ----- parsing of what the user typed -----

export type ParseResult<T> = { ok: true; value: T } | { ok: false; error: string };

export function parseNumberInput(raw: string): ParseResult<string | null> {
  const t = raw.trim();
  if (t === '') return { ok: true, value: null };
  const n = Number(t);
  if (!Number.isFinite(n)) return { ok: false, error: 'Enter a number' };
  return { ok: true, value: String(n) };
}

export function parseSafetyClass(raw: string): ParseResult<SafetyClass> {
  return raw === 'NONE' || raw === 'SIL' || raw === 'ESD'
    ? { ok: true, value: raw }
    : { ok: false, error: 'Unknown safety class' };
}

/** Value + delay text from the limit editor. Empty value removes the limit. Hysteresis is kept from the old limit. */
export function parseLimitInput(
  valueText: string,
  delayText: string,
  old: AlarmLimitValue | null,
): ParseResult<AlarmLimitValue | null> {
  if (valueText.trim() === '') return { ok: true, value: null };
  const value = Number(valueText.trim());
  if (!Number.isFinite(value)) return { ok: false, error: 'Value must be a number' };
  let delayS = old?.delayS ?? 0;
  if (delayText.trim() !== '') {
    delayS = Number(delayText.trim());
    if (!Number.isFinite(delayS) || delayS < 0) {
      return { ok: false, error: 'Delay must be a number of seconds >= 0' };
    }
  }
  return { ok: true, value: { value, delayS, hysteresis: old?.hysteresis ?? 0 } };
}
