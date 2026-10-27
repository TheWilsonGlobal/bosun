import { useMemo, useRef } from 'react';
import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnDef,
  type ColumnFiltersState,
  type PaginationState,
  type SortingState,
  type Updater,
} from '@tanstack/react-table';
import { useVirtualizer } from '@tanstack/react-virtual';
import { AlertTriangle, ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import { cn } from '@/lib/utils';
import { ALARM_LEVELS, type Tag, type TagFacets } from '@/lib/api/types';
import {
  effectiveField,
  effectiveLimit,
  fieldKey,
  limitKey,
  type PendingEdits,
} from '@/lib/pendingEdits';
import type { Violation } from '@/lib/api/types';
import { usePending } from './PendingContext';
import { ColumnFilter } from './ColumnFilter';
import { LimitCell, SafetyCell, TextCell } from './EditableCells';

export const ROW_HEIGHT = 36;

interface GridProps {
  data: Tag[];
  facets: TagFacets | undefined;
  sorting: SortingState;
  columnFilters: ColumnFiltersState;
  pagination: PaginationState;
  pageCount: number;
  onSortingChange: (updater: Updater<SortingState>) => void;
  onColumnFiltersChange: (updater: Updater<ColumnFiltersState>) => void;
  onPaginationChange: (updater: Updater<PaginationState>) => void;
  violationsByTag: ReadonlyMap<string, Violation[]> | undefined;
  busy: boolean;
}

const WIDTHS: Record<string, number> = {
  tagName: 190,
  description: 280,
  system: 100,
  equipment: 96,
  signalType: 96,
  unit: 100,
  rangeLow: 96,
  rangeHigh: 100,
  safetyClass: 112,
  alarmGroup: 140,
  limit_HH: 136,
  limit_H: 136,
  limit_L: 136,
  limit_LL: 136,
  channel: 100,
};

function useColumns(edits: PendingEdits, violations: GridProps['violationsByTag']) {
  const { editField, editLimit } = usePending();
  return useMemo<ColumnDef<Tag>[]>(() => {
    const textCol = (id: 'description' | 'unit', header: string): ColumnDef<Tag> => ({
      id,
      header,
      accessorFn: (t) => t[id],
      cell: ({ row }) => (
        <TextCell
          value={effectiveField(row.original, edits, id)}
          label={`${header} of ${row.original.tagName}`}
          modified={edits.has(fieldKey(row.original.id, id))}
          onCommit={(v) => editField(row.original, id, v)}
        />
      ),
    });
    const numCol = (id: 'rangeLow' | 'rangeHigh', header: string): ColumnDef<Tag> => ({
      id,
      header,
      accessorFn: (t) => t[id],
      cell: ({ row }) => (
        <TextCell
          numeric
          value={effectiveField(row.original, edits, id)}
          label={`${header} of ${row.original.tagName}`}
          modified={edits.has(fieldKey(row.original.id, id))}
          disabled={row.original.signalType === 'DI' || row.original.signalType === 'DO'}
          onCommit={(v) => editField(row.original, id, v)}
        />
      ),
    });
    const plain = (id: keyof Tag & string, header: string, cls = ''): ColumnDef<Tag> => ({
      id,
      header,
      accessorFn: (t) => t[id] as string | number | null,
      cell: ({ getValue }) => (
        <span className={cn('block truncate px-1.5', cls)}>{String(getValue() ?? '') || '-'}</span>
      ),
    });
    const cols: ColumnDef<Tag>[] = [
      {
        id: 'tagName',
        header: 'Tag',
        accessorFn: (t) => t.tagName,
        cell: ({ row }) => {
          const v = violations?.get(row.original.tagName);
          const hasError = v?.some((x) => x.severity === 'error');
          return (
            <span className="flex items-center gap-1 px-1.5 font-mono text-[13px]">
              {v && (
                <AlertTriangle
                  className={cn(
                    'size-3.5 shrink-0',
                    hasError ? 'text-destructive' : 'text-warning',
                  )}
                  aria-label={`Rule violations: ${v.map((x) => x.message).join('; ')}`}
                />
              )}
              <span className="truncate">{row.original.tagName}</span>
            </span>
          );
        },
      },
      textCol('description', 'Description'),
      plain('system', 'System'),
      plain('equipment', 'Equip.'),
      plain('signalType', 'Type'),
      textCol('unit', 'Unit'),
      numCol('rangeLow', 'Range low'),
      numCol('rangeHigh', 'Range high'),
      {
        id: 'safetyClass',
        header: 'Safety',
        accessorFn: (t) => t.safetyClass,
        cell: ({ row }) => (
          <SafetyCell
            value={
              (effectiveField(row.original, edits, 'safetyClass') ?? 'NONE') as Tag['safetyClass']
            }
            label={`Safety class of ${row.original.tagName}`}
            modified={edits.has(fieldKey(row.original.id, 'safetyClass'))}
            onCommit={(v) => editField(row.original, 'safetyClass', v)}
          />
        ),
      },
      plain('alarmGroup', 'Alarm group'),
      ...ALARM_LEVELS.map<ColumnDef<Tag>>((level) => ({
        id: `limit_${level}`,
        header: level,
        enableSorting: false,
        accessorFn: (t) => t.alarmLimits.find((l) => l.level === level)?.value ?? null,
        cell: ({ row }) => (
          <LimitCell
            value={effectiveLimit(row.original, edits, level)}
            label={`${level} limit of ${row.original.tagName}`}
            modified={edits.has(limitKey(row.original.id, level))}
            onCommit={(v) => editLimit(row.original, level, v)}
          />
        ),
      })),
      {
        id: 'channel',
        header: 'Channel',
        enableSorting: false,
        accessorFn: (t) => t.channel,
        cell: ({ getValue }) => (
          <span className="block truncate px-1.5">{(getValue() as string | null) ?? '-'}</span>
        ),
      },
    ];
    return cols;
  }, [edits, editField, editLimit, violations]);
}

const FILTER_COLUMNS: Record<
  string,
  { label: string; facet: keyof TagFacets; allowNone?: boolean }
> = {
  system: { label: 'System', facet: 'systems' },
  unit: { label: 'Unit', facet: 'units' },
  signalType: { label: 'Type', facet: 'signalTypes' },
  safetyClass: { label: 'Safety', facet: 'safetyClasses' },
  alarmGroup: { label: 'Alarm group', facet: 'alarmGroups', allowNone: true },
};

export function TagGrid(props: GridProps) {
  const { data, facets, sorting, columnFilters, pagination, pageCount, violationsByTag, busy } =
    props;
  const { edits } = usePending();
  const columns = useColumns(edits, violationsByTag);

  // eslint-disable-next-line react-hooks/incompatible-library -- third-party API, memoization skipped on purpose
  const table = useReactTable({
    data,
    columns,
    getRowId: (t) => String(t.id),
    getCoreRowModel: getCoreRowModel(),
    manualPagination: true,
    manualSorting: true,
    manualFiltering: true,
    enableSortingRemoval: false,
    pageCount,
    state: { sorting, columnFilters, pagination },
    onSortingChange: props.onSortingChange,
    onColumnFiltersChange: props.onColumnFiltersChange,
    onPaginationChange: props.onPaginationChange,
    defaultColumn: { sortDescFirst: false },
  });

  const rows = table.getRowModel().rows;
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  });
  const items = virtualizer.getVirtualItems();
  const paddingTop = items.length ? items[0].start : 0;
  const paddingBottom = items.length ? virtualizer.getTotalSize() - items[items.length - 1].end : 0;
  const totalWidth = table.getVisibleLeafColumns().reduce((w, c) => w + (WIDTHS[c.id] ?? 100), 0);

  return (
    <div
      ref={scrollRef}
      className={cn('bg-card h-full overflow-auto rounded-md border', busy && 'opacity-70')}
      role="region"
      aria-label="Tag grid"
      aria-busy={busy}
      tabIndex={0}
    >
      <table
        className="border-separate border-spacing-0 text-sm"
        style={{ width: totalWidth, tableLayout: 'fixed' }}
        aria-rowcount={rows.length + 1}
      >
        <colgroup>
          {table.getVisibleLeafColumns().map((c) => (
            <col key={c.id} style={{ width: WIDTHS[c.id] ?? 100 }} />
          ))}
        </colgroup>
        <thead className="bg-muted sticky top-0 z-10">
          <tr>
            {table.getHeaderGroups()[0].headers.map((h) => {
              const sorted = h.column.getIsSorted();
              const fcfg = FILTER_COLUMNS[h.column.id];
              return (
                <th
                  key={h.id}
                  scope="col"
                  className="overflow-hidden border-b px-1 py-1 text-left text-xs font-semibold"
                  aria-sort={
                    sorted === 'asc' ? 'ascending' : sorted === 'desc' ? 'descending' : undefined
                  }
                >
                  <div className="flex items-center gap-0.5">
                    {h.column.getCanSort() ? (
                      <button
                        type="button"
                        className="hover:bg-accent flex flex-1 items-center gap-1 rounded px-1 py-1 text-left"
                        onClick={h.column.getToggleSortingHandler()}
                      >
                        <span className="truncate">{String(h.column.columnDef.header)}</span>
                        {sorted === 'asc' ? (
                          <ArrowUp className="size-3" aria-hidden />
                        ) : sorted === 'desc' ? (
                          <ArrowDown className="size-3" aria-hidden />
                        ) : (
                          <ArrowUpDown className="text-muted-foreground size-3" aria-hidden />
                        )}
                        <span className="sr-only">
                          {sorted ? `sorted ${sorted}ending, click to reverse` : 'click to sort'}
                        </span>
                      </button>
                    ) : (
                      <span className="flex-1 truncate px-1 py-1">
                        {String(h.column.columnDef.header)}
                      </span>
                    )}
                    {fcfg && (
                      <ColumnFilter
                        label={fcfg.label}
                        options={(facets?.[fcfg.facet] as string[] | undefined) ?? []}
                        allowNone={fcfg.allowNone}
                        selected={(h.column.getFilterValue() as string[] | undefined) ?? []}
                        onChange={(next) => h.column.setFilterValue(next.length ? next : undefined)}
                      />
                    )}
                  </div>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {paddingTop > 0 && (
            <tr aria-hidden style={{ height: paddingTop }}>
              <td colSpan={columns.length} />
            </tr>
          )}
          {items.map((vi) => {
            const row = rows[vi.index];
            const v = violationsByTag?.get(row.original.tagName);
            const hasError = v?.some((x) => x.severity === 'error');
            return (
              <tr
                key={row.id}
                aria-rowindex={vi.index + 2}
                data-violation={v ? (hasError ? 'error' : 'warning') : undefined}
                className={cn(
                  'hover:bg-accent/40',
                  vi.index % 2 === 1 ? 'bg-muted/20' : 'bg-card',
                  v && (hasError ? 'bg-destructive/10' : 'bg-warning/15'),
                )}
                style={{ height: ROW_HEIGHT }}
              >
                {row.getVisibleCells().map((cell) => (
                  <td key={cell.id} className="border-b p-0 align-middle">
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                ))}
              </tr>
            );
          })}
          {paddingBottom > 0 && (
            <tr aria-hidden style={{ height: paddingBottom }}>
              <td colSpan={columns.length} />
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
