import { describe, expect, it } from 'vitest';
import {
  DEFAULT_TAG_FILTER,
  fromColumnFilters,
  fromPagination,
  fromSorting,
  hasActiveFilters,
  parseTagSearch,
  toApiQuery,
  toColumnFilters,
  toPagination,
  toSearchParams,
  toSorting,
  withFilter,
} from './tagQuery';

const parse = (qs: string) => parseTagSearch(new URLSearchParams(qs));

describe('tag grid query params', () => {
  it('an empty URL is the default state and produces a clean URL', () => {
    expect(parse('')).toEqual(DEFAULT_TAG_FILTER);
    expect(toSearchParams(DEFAULT_TAG_FILTER).toString()).toBe('');
  });

  it('round-trips filter state through the URL (reload / share keeps state)', () => {
    const state = {
      ...DEFAULT_TAG_FILTER,
      filter: 'ME-EXH',
      system: ['ME', 'AE'],
      signalType: ['AI'],
      safetyClass: ['SIL', 'ESD'],
      alarmGroup: ['none', 'ME-EXH'],
      hasAlarm: false,
      bound: true,
      sort: 'rangeHigh' as const,
      order: 'desc' as const,
      page: 3,
      pageSize: 250,
    };
    const qs = toSearchParams(state).toString();
    expect(parse(qs)).toEqual(state);
  });

  it('the API query always carries explicit paging/sort and uses the backend param names', () => {
    const q = new URLSearchParams(
      toApiQuery({ ...DEFAULT_TAG_FILTER, filter: ' ME-EXH ', system: ['ME'], hasAlarm: true }),
    );
    expect(Object.fromEntries(q)).toEqual({
      filter: 'ME-EXH',
      system: 'ME',
      hasAlarm: 'true',
      sort: 'tagName',
      order: 'asc',
      page: '1',
      pageSize: '50',
    });
  });

  it('sanitises hostile or stale URL values', () => {
    const s = parse(
      'sort=drop%20table&order=sideways&page=-4&pageSize=99999&hasAlarm=maybe&system=,,ME,',
    );
    expect(s.sort).toBe('tagName');
    expect(s.order).toBe('asc');
    expect(s.page).toBe(1);
    expect(s.pageSize).toBe(1000);
    expect(s.hasAlarm).toBeNull();
    expect(s.system).toEqual(['ME']);
  });

  it('changing a filter returns to page 1 but paging keeps it', () => {
    const on3 = { ...DEFAULT_TAG_FILTER, page: 3 };
    expect(withFilter(on3, { filter: 'x' }).page).toBe(1);
    expect(withFilter(on3, { page: 4 }).page).toBe(4);
  });

  it('adapts to TanStack Table state both ways (manual sorting/filtering/pagination)', () => {
    const state = {
      ...DEFAULT_TAG_FILTER,
      system: ['ME'],
      sort: 'unit' as const,
      order: 'desc' as const,
      page: 2,
      pageSize: 100,
    };
    expect(toColumnFilters(state)).toEqual([{ id: 'system', value: ['ME'] }]);
    expect(toSorting(state)).toEqual([{ id: 'unit', desc: true }]);
    expect(toPagination(state)).toEqual({ pageIndex: 1, pageSize: 100 });

    const cleared = fromColumnFilters({ ...state, page: 2 }, []);
    expect(cleared.system).toEqual([]);
    expect(cleared.page).toBe(1);

    expect(fromSorting(state, [{ id: 'description', desc: false }])).toMatchObject({
      sort: 'description',
      order: 'asc',
      page: 1,
    });
    expect(fromSorting(state, [{ id: 'limit_HH', desc: false }])).toMatchObject({
      sort: 'tagName',
    });
    expect(fromPagination(state, { pageIndex: 4, pageSize: 100 }).page).toBe(5);
    expect(fromPagination(state, { pageIndex: 4, pageSize: 500 })).toMatchObject({
      page: 1,
      pageSize: 500,
    });
  });

  it('reports whether any filter (not sort/paging) is active', () => {
    expect(hasActiveFilters({ ...DEFAULT_TAG_FILTER, page: 4, sort: 'unit' })).toBe(false);
    expect(hasActiveFilters({ ...DEFAULT_TAG_FILTER, bound: false })).toBe(true);
  });
});
