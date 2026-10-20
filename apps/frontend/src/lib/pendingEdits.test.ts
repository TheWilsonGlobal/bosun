import { describe, expect, it } from 'vitest';
import type { Tag } from './api/types';
import {
  editsToOps,
  effectiveField,
  effectiveLimit,
  parseLimitInput,
  parseNumberInput,
  setFieldEdit,
  setLimitEdit,
  tagsTouched,
  type PendingEdits,
} from './pendingEdits';

const tag = (over: Partial<Tag> = {}): Tag => ({
  id: 10,
  projectId: 1,
  tagName: 'ME-EXH-TT01',
  description: 'Exhaust gas temp cyl 1',
  system: 'ME',
  equipment: 'EXH',
  signalType: 'AI',
  unit: 'degC',
  rangeLow: 0,
  rangeHigh: 600,
  safetyClass: 'NONE',
  alarmGroupId: null,
  alarmGroup: null,
  channelId: null,
  channel: null,
  alarmLimits: [{ level: 'H', value: 450, delayS: 5, hysteresis: 2 }],
  hasAlarm: true,
  version: 3,
  ...over,
});
const empty: PendingEdits = new Map();

describe('pending edits -> change-set ops', () => {
  it('captures the DISPLAYED value as old, as an explicit string (numbers invariant)', () => {
    const e = setFieldEdit(empty, tag(), 'rangeHigh', '650');
    expect(editsToOps(e.values())).toEqual([
      { type: 'updateTag', tagName: 'ME-EXH-TT01', field: 'rangeHigh', old: '600', new: '650' },
    ]);
  });

  it('keeps the FIRST old value across repeated edits of the same cell', () => {
    let e = setFieldEdit(empty, tag(), 'unit', 'degF');
    e = setFieldEdit(e, tag(), 'unit', 'K');
    const [op] = editsToOps(e.values());
    expect(op).toMatchObject({ field: 'unit', old: 'degC', new: 'K' });
    expect(e.size).toBe(1);
  });

  it('editing back to the original cancels the edit (no no-op ops)', () => {
    let e = setFieldEdit(empty, tag(), 'description', 'changed');
    expect(e.size).toBe(1);
    e = setFieldEdit(e, tag(), 'description', 'Exhaust gas temp cyl 1');
    expect(e.size).toBe(0);
    // numeric equality is by value: "600.0" is still 600
    expect(setFieldEdit(empty, tag(), 'rangeHigh', '600.0').size).toBe(0);
  });

  it('sends null (not undefined) for an empty old/new so the backend enforces it', () => {
    const t = tag({ unit: null });
    const e = setFieldEdit(empty, t, 'unit', 'bar');
    const [op] = editsToOps(e.values());
    expect(JSON.parse(JSON.stringify(op))).toHaveProperty('old', null);
    const cleared = setFieldEdit(empty, tag(), 'unit', '   ');
    expect(editsToOps(cleared.values())[0]).toMatchObject({ old: 'degC', new: null });
  });

  it('maps alarm-limit edits to setAlarmLimit with old limit captured (incl. null = new limit)', () => {
    let e = setLimitEdit(empty, tag(), 'H', { value: 460, delayS: 10, hysteresis: 2 });
    e = setLimitEdit(e, tag(), 'HH', { value: 500, delayS: 0, hysteresis: 0 });
    e = setLimitEdit(e, tag(), 'L', null); // removing a limit that does not exist is a no-op
    expect(editsToOps(e.values())).toEqual([
      {
        type: 'setAlarmLimit',
        tagName: 'ME-EXH-TT01',
        level: 'H',
        old: { value: 450, delayS: 5, hysteresis: 2 },
        new: { value: 460, delayS: 10, hysteresis: 2 },
      },
      {
        type: 'setAlarmLimit',
        tagName: 'ME-EXH-TT01',
        level: 'HH',
        old: null,
        new: { value: 500, delayS: 0, hysteresis: 0 },
      },
    ]);
  });

  it('removing an existing limit sends new: null', () => {
    const e = setLimitEdit(empty, tag(), 'H', null);
    expect(editsToOps(e.values())[0]).toMatchObject({ level: 'H', new: null });
  });

  it('shows pending values in the grid and counts touched tags', () => {
    const t = tag();
    let e = setFieldEdit(empty, t, 'unit', 'degF');
    e = setLimitEdit(e, t, 'H', { value: 1, delayS: 1, hysteresis: 0 });
    e = setFieldEdit(e, tag({ id: 11, tagName: 'ME-EXH-TT02' }), 'unit', 'K');
    expect(effectiveField(t, e, 'unit')).toBe('degF');
    expect(effectiveField(t, e, 'description')).toBe('Exhaust gas temp cyl 1');
    expect(effectiveLimit(t, e, 'H')?.value).toBe(1);
    expect(tagsTouched(e.values())).toEqual(['ME-EXH-TT01', 'ME-EXH-TT02']);
  });

  it('does not mutate the previous map', () => {
    const before = setFieldEdit(empty, tag(), 'unit', 'K');
    setFieldEdit(before, tag(), 'unit', 'bar');
    expect(editsToOps(before.values())[0]).toMatchObject({ new: 'K' });
  });
});

describe('input parsing', () => {
  it('parses numbers and rejects garbage', () => {
    expect(parseNumberInput(' 12.5 ')).toEqual({ ok: true, value: '12.5' });
    expect(parseNumberInput('')).toEqual({ ok: true, value: null });
    expect(parseNumberInput('12,5').ok).toBe(false);
    expect(parseNumberInput('abc').ok).toBe(false);
  });

  it('parses limit value + delay, defaulting delay/hysteresis from the old limit', () => {
    const old = { value: 450, delayS: 5, hysteresis: 2 };
    expect(parseLimitInput('460', '', old)).toEqual({
      ok: true,
      value: { value: 460, delayS: 5, hysteresis: 2 },
    });
    expect(parseLimitInput('95', '10', null)).toEqual({
      ok: true,
      value: { value: 95, delayS: 10, hysteresis: 0 },
    });
    expect(parseLimitInput('', '3', old)).toEqual({ ok: true, value: null });
    expect(parseLimitInput('x', '', old).ok).toBe(false);
    expect(parseLimitInput('1', '-2', old).ok).toBe(false);
  });
});
