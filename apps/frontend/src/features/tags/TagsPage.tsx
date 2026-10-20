import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type {
  ColumnFiltersState,
  PaginationState,
  SortingState,
  Updater,
} from '@tanstack/react-table';
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Search, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { fetchTagFacets, fetchTags } from '@/lib/api/endpoints';
import { describeError } from '@/lib/api/errors';
import {
  PAGE_SIZES,
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
  DEFAULT_TAG_FILTER,
  type TagFilterState,
} from '@/lib/tagQuery';
import { useProject } from '@/features/project/ProjectContext';
import { useDebouncedDraft } from '@/lib/useDebouncedDraft';
import { PendingBar } from './PendingBar';
import { usePending } from './PendingContext';
import { TagGrid } from './TagGrid';
import { Status } from '@/components/Status';

const resolve = <T,>(u: Updater<T>, prev: T): T =>
  typeof u === 'function' ? (u as (p: T) => T)(prev) : u;

function TriSelect({
  id,
  label,
  value,
  onChange,
}: {
  id: string;
  label: string;
  value: boolean | null;
  onChange: (v: boolean | null) => void;
}) {
  return (
    <div className="flex items-center gap-1.5">
      <label htmlFor={id} className="text-muted-foreground text-sm">
        {label}
      </label>
      <select
        id={id}
        className="border-input bg-background h-8 rounded-md border px-1.5 text-sm"
        value={value === null ? '' : String(value)}
        onChange={(e) => onChange(e.target.value === '' ? null : e.target.value === 'true')}
      >
        <option value="">Any</option>
        <option value="true">Yes</option>
        <option value="false">No</option>
      </select>
    </div>
  );
}

export function TagsPage() {
  const { project, loading: projectLoading, error: projectError } = useProject();
  const { highlight, clearHighlight } = usePending();
  const [params, setParams] = useSearchParams();
  const state = useMemo(() => parseTagSearch(params), [params]);
  const projectId = project?.id;

  const update = useCallback(
    (next: TagFilterState, replace = false) => setParams(toSearchParams(next), { replace }),
    [setParams],
  );

  // The global filter box is debounced into the URL.
  const [text, setText] = useDebouncedDraft(state.filter, (v) =>
    update(withFilter(state, { filter: v }), true),
  );

  const query = toApiQuery(state);
  const tags = useQuery({
    queryKey: ['tags', projectId, query],
    queryFn: ({ signal }) => fetchTags(projectId!, query, signal),
    enabled: projectId !== undefined,
    placeholderData: keepPreviousData,
  });
  const facets = useQuery({
    queryKey: ['tag-facets', projectId],
    queryFn: () => fetchTagFacets(projectId!),
    enabled: projectId !== undefined,
    staleTime: 60_000,
  });

  const sorting = useMemo(() => toSorting(state), [state]);
  const columnFilters = useMemo(() => toColumnFilters(state), [state]);
  const pagination = useMemo(() => toPagination(state), [state]);

  const total = tags.data?.total ?? 0;
  const pageCount = Math.max(1, Math.ceil(total / state.pageSize));
  const from = total === 0 ? 0 : (state.page - 1) * state.pageSize + 1;
  const to = Math.min(total, state.page * state.pageSize);

  if (projectLoading) return <Status>Loading projects...</Status>;
  if (projectError) {
    const m = describeError(projectError);
    return (
      <Status tone="error" title={m.title}>
        {m.message}
      </Status>
    );
  }
  if (!project) return <Status>No project exists yet.</Status>;

  return (
    <div className="flex h-full flex-col gap-3 p-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative w-72">
          <Search className="text-muted-foreground absolute top-2 left-2.5 size-4" aria-hidden />
          <Input
            type="search"
            className="h-8 pl-8"
            placeholder="Filter tags (e.g. ME-EXH)"
            aria-label="Filter tags by name or description"
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
        </div>
        <TriSelect
          id="f-has-alarm"
          label="Has alarm"
          value={state.hasAlarm}
          onChange={(v) => update(withFilter(state, { hasAlarm: v }))}
        />
        <TriSelect
          id="f-bound"
          label="Bound to channel"
          value={state.bound}
          onChange={(v) => update(withFilter(state, { bound: v }))}
        />
        {hasActiveFilters(state) && (
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              update({
                ...DEFAULT_TAG_FILTER,
                pageSize: state.pageSize,
                sort: state.sort,
                order: state.order,
              })
            }
          >
            <X aria-hidden /> Clear filters
          </Button>
        )}
        <span className="text-muted-foreground ml-auto text-sm" aria-live="polite">
          {tags.isPending
            ? 'Loading...'
            : `${from.toLocaleString()}-${to.toLocaleString()} of ${total.toLocaleString()} tags`}
        </span>
      </div>

      {highlight && (
        <div
          role="status"
          className="bg-destructive/10 border-destructive/40 flex items-center gap-3 rounded-md border px-3 py-1.5 text-sm"
        >
          Highlighting {highlight.byTag.size} tag{highlight.byTag.size === 1 ? '' : 's'} with rule
          violations from change set #{highlight.changeSetId}.
          <Button variant="ghost" size="sm" className="ml-auto" onClick={clearHighlight}>
            Dismiss
          </Button>
        </div>
      )}

      <div className="relative min-h-0 flex-1">
        {tags.isError && !tags.data ? (
          <Status tone="error" title={describeError(tags.error).title}>
            {describeError(tags.error).message}{' '}
            <Button size="sm" variant="outline" onClick={() => void tags.refetch()}>
              Retry
            </Button>
          </Status>
        ) : tags.isPending ? (
          <Status>Loading tags...</Status>
        ) : (
          <>
            <TagGrid
              data={tags.data?.items ?? []}
              facets={facets.data}
              sorting={sorting}
              columnFilters={columnFilters}
              pagination={pagination}
              pageCount={pageCount}
              onSortingChange={(u: Updater<SortingState>) =>
                update(fromSorting(state, resolve(u, sorting)))
              }
              onColumnFiltersChange={(u: Updater<ColumnFiltersState>) =>
                update(fromColumnFilters(state, resolve(u, columnFilters)))
              }
              onPaginationChange={(u: Updater<PaginationState>) =>
                update(fromPagination(state, resolve(u, pagination)))
              }
              violationsByTag={highlight?.byTag}
              busy={tags.isFetching}
            />
            {total === 0 && (
              <div className="absolute inset-x-0 top-24 text-center text-sm">
                <p className="font-medium">No tags match these filters.</p>
                <p className="text-muted-foreground">
                  Clear a filter or check the spelling of the search.
                </p>
              </div>
            )}
            {tags.isError && (
              <p
                role="alert"
                className="bg-destructive absolute right-3 bottom-3 rounded px-3 py-1.5 text-sm text-white"
              >
                {describeError(tags.error).title}: showing the last loaded page.
              </p>
            )}
          </>
        )}
      </div>

      <nav aria-label="Pagination" className="flex flex-wrap items-center gap-2 text-sm">
        <label htmlFor="page-size" className="text-muted-foreground">
          Rows per page
        </label>
        <select
          id="page-size"
          className="border-input bg-background h-8 rounded-md border px-1.5"
          value={state.pageSize}
          onChange={(e) =>
            update(fromPagination(state, { pageIndex: 0, pageSize: Number(e.target.value) }))
          }
        >
          {PAGE_SIZES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <div className="ml-auto flex items-center gap-1">
          <Button
            variant="outline"
            size="icon"
            className="size-8"
            aria-label="First page"
            disabled={state.page <= 1}
            onClick={() => update(withFilter(state, { page: 1 }))}
          >
            <ChevronsLeft />
          </Button>
          <Button
            variant="outline"
            size="icon"
            className="size-8"
            aria-label="Previous page"
            disabled={state.page <= 1}
            onClick={() => update(withFilter(state, { page: state.page - 1 }))}
          >
            <ChevronLeft />
          </Button>
          <span className="px-2 tabular-nums">
            Page {state.page} of {pageCount}
          </span>
          <Button
            variant="outline"
            size="icon"
            className="size-8"
            aria-label="Next page"
            disabled={state.page >= pageCount}
            onClick={() => update(withFilter(state, { page: state.page + 1 }))}
          >
            <ChevronRight />
          </Button>
          <Button
            variant="outline"
            size="icon"
            className="size-8"
            aria-label="Last page"
            disabled={state.page >= pageCount}
            onClick={() => update(withFilter(state, { page: pageCount }))}
          >
            <ChevronsRight />
          </Button>
        </div>
      </nav>
      <PendingBar />
    </div>
  );
}
