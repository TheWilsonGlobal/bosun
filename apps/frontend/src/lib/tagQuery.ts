import type { ColumnFiltersState, PaginationState, SortingState } from '@tanstack/react-table';

// Grid filter state <-> URL query string <-> backend query string. The URL uses the backend's own parameter
// names, so a shared link is also a readable API call.

export const SORT_FIELDS = [
  'id',
  'tagName',
  'description',
  'system',
  'equipment',
  'signalType',
  'unit',
  'rangeLow',
  'rangeHigh',
  'safetyClass',
  'alarmGroup',
  'version',
] as const;
export type SortField = (typeof SORT_FIELDS)[number];

export const LIST_FILTERS = ['system', 'unit', 'signalType', 'safetyClass', 'alarmGroup'] as const;
export type ListFilter = (typeof LIST_FILTERS)[number];

export const PAGE_SIZES = [25, 50, 100, 250, 500, 1000] as const;
export const DEFAULT_PAGE_SIZE = 50;
export const MAX_PAGE_SIZE = 1000;

export interface TagFilterState {
  filter: string;
  system: string[];
  unit: string[];
  signalType: string[];
  safetyClass: string[];
  alarmGroup: string[];
  hasAlarm: boolean | null;
  bound: boolean | null;
  sort: SortField;
  order: 'asc' | 'desc';
  page: number;
  pageSize: number;
}

export const DEFAULT_TAG_FILTER: TagFilterState = {
  filter: '',
  system: [],
  unit: [],
  signalType: [],
  safetyClass: [],
  alarmGroup: [],
  hasAlarm: null,
  bound: null,
  sort: 'tagName',
  order: 'asc',
  page: 1,
  pageSize: DEFAULT_PAGE_SIZE,
};

const list = (v: string | null) =>
  v
    ? v
        .split(',')
        .map((s) => s.trim())
        .filter(Boolean)
    : [];
const tri = (v: string | null): boolean | null =>
  v === 'true' ? true : v === 'false' ? false : null;
const posInt = (v: string | null, fallback: number) => {
  const n = Number(v);
  return v !== null && Number.isInteger(n) && n >= 1 ? n : fallback;
};

export function parseTagSearch(params: URLSearchParams): TagFilterState {
  const sort = params.get('sort') as SortField | null;
  return {
    filter: params.get('filter') ?? '',
    system: list(params.get('system')),
    unit: list(params.get('unit')),
    signalType: list(params.get('signalType')),
    safetyClass: list(params.get('safetyClass')),
    alarmGroup: list(params.get('alarmGroup')),
    hasAlarm: tri(params.get('hasAlarm')),
    bound: tri(params.get('bound')),
    sort: sort && SORT_FIELDS.includes(sort) ? sort : DEFAULT_TAG_FILTER.sort,
    order: params.get('order') === 'desc' ? 'desc' : 'asc',
    page: posInt(params.get('page'), 1),
    pageSize: Math.min(posInt(params.get('pageSize'), DEFAULT_PAGE_SIZE), MAX_PAGE_SIZE),
  };
}

/** Shared by the browser URL and the API call: only non-default values are emitted, in a stable order. */
function serialize(state: TagFilterState, alwaysPaging: boolean): URLSearchParams {
  const q = new URLSearchParams();
  if (state.filter.trim()) q.set('filter', state.filter.trim());
  for (const key of LIST_FILTERS) if (state[key].length) q.set(key, state[key].join(','));
  if (state.hasAlarm !== null) q.set('hasAlarm', String(state.hasAlarm));
  if (state.bound !== null) q.set('bound', String(state.bound));
  if (alwaysPaging || state.sort !== DEFAULT_TAG_FILTER.sort) q.set('sort', state.sort);
  if (alwaysPaging || state.order !== 'asc') q.set('order', state.order);
  if (alwaysPaging || state.page !== 1) q.set('page', String(state.page));
  if (alwaysPaging || state.pageSize !== DEFAULT_PAGE_SIZE)
    q.set('pageSize', String(state.pageSize));
  return q;
}

export const toSearchParams = (state: TagFilterState) => serialize(state, false);
export const toApiQuery = (state: TagFilterState) => serialize(state, true).toString();

/** Any change to a filter (not to paging itself) returns to page 1. */
export function withFilter(state: TagFilterState, patch: Partial<TagFilterState>): TagFilterState {
  const paging = 'page' in patch || 'pageSize' in patch;
  return { ...state, ...patch, page: paging ? (patch.page ?? 1) : 1 };
}

export function hasActiveFilters(state: TagFilterState): boolean {
  return (
    state.filter.trim() !== '' ||
    LIST_FILTERS.some((k) => state[k].length > 0) ||
    state.hasAlarm !== null ||
    state.bound !== null
  );
}

// ----- TanStack Table state adapters (manualFiltering / manualSorting / manualPagination) -----

export const toColumnFilters = (state: TagFilterState): ColumnFiltersState =>
  LIST_FILTERS.filter((k) => state[k].length > 0).map((k) => ({ id: k, value: state[k] }));

export function fromColumnFilters(
  state: TagFilterState,
  filters: ColumnFiltersState,
): TagFilterState {
  const patch: Partial<TagFilterState> = {};
  for (const key of LIST_FILTERS) {
    const found = filters.find((f) => f.id === key);
    patch[key] = Array.isArray(found?.value) ? (found.value as string[]) : [];
  }
  return withFilter(state, patch);
}

export const toSorting = (state: TagFilterState): SortingState => [
  { id: state.sort, desc: state.order === 'desc' },
];

export function fromSorting(state: TagFilterState, sorting: SortingState): TagFilterState {
  const first = sorting[0];
  if (!first || !SORT_FIELDS.includes(first.id as SortField)) {
    return withFilter(state, { sort: DEFAULT_TAG_FILTER.sort, order: 'asc' });
  }
  return withFilter(state, { sort: first.id as SortField, order: first.desc ? 'desc' : 'asc' });
}

export const toPagination = (state: TagFilterState): PaginationState => ({
  pageIndex: state.page - 1,
  pageSize: state.pageSize,
});

export function fromPagination(state: TagFilterState, p: PaginationState): TagFilterState {
  const sizeChanged = p.pageSize !== state.pageSize;
  return withFilter(state, {
    pageSize: p.pageSize,
    page: sizeChanged ? 1 : p.pageIndex + 1,
  });
}
