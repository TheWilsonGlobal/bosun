import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { flexRender, getCoreRowModel, useReactTable, type ColumnDef } from '@tanstack/react-table';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Ban, RotateCcw, ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { cn } from '@/lib/utils';
import {
  BAND_CLASS,
  BAND_LABEL,
  BAND_MARK,
  cellNotes,
  fieldBand,
  formatValue,
  isInjection,
  rowBand,
} from './bands';
import { BandTag } from './parts';
import { NUMERIC_FIELDS, type BandThresholds, type ExtractedRow, type FieldKey } from './types';

export const ROW_HEIGHT = 44;

const FIELD_COLUMNS: { key: FieldKey; header: string; width: number }[] = [
  { key: 'tagName', header: 'Tag name', width: 180 },
  { key: 'description', header: 'Description', width: 240 },
  { key: 'signalType', header: 'Signal', width: 84 },
  { key: 'unit', header: 'Unit', width: 84 },
  { key: 'rangeLow', header: 'Range low', width: 96 },
  { key: 'rangeHigh', header: 'Range high', width: 96 },
  { key: 'alarmHh', header: 'HH', width: 84 },
  { key: 'alarmH', header: 'H', width: 84 },
  { key: 'alarmL', header: 'L', width: 84 },
  { key: 'alarmLl', header: 'LL', width: 84 },
  { key: 'delayS', header: 'Delay s', width: 84 },
];
const HEADER_OF = Object.fromEntries(FIELD_COLUMNS.map((c) => [c.key, c.header]));

export const cellErrorKey = (rowId: number, field: string) => `${rowId}:${field}`;

export interface GridActions {
  /** Resolves true when the edit was saved (the editor closes), false when it was refused (the editor stays open). */
  onEdit: (row: ExtractedRow, field: string, text: string) => Promise<boolean>;
  onToggleExcluded: (row: ExtractedRow) => void;
  onSelect: (row: ExtractedRow, on: boolean) => void;
  onActivate: (row: ExtractedRow) => void;
}

interface Props extends GridActions {
  rows: ExtractedRow[];
  bands: BandThresholds;
  editable: boolean;
  selected: ReadonlySet<number>;
  activeRowId: number | null;
  cellErrors: Readonly<Record<string, string>>;
  clearCellError: (rowId: number, field: string) => void;
  /** Ask the grid to scroll a row into view (the nonce makes the same row re-triggerable). */
  scrollRequest: { rowId: number; nonce: number } | null;
}

interface GridContextValue extends GridActions {
  bands: BandThresholds;
  editable: boolean;
  selected: ReadonlySet<number>;
  activeRowId: number | null;
  cellErrors: Readonly<Record<string, string>>;
  clearCellError: (rowId: number, field: string) => void;
}

/**
 * Cells read the changing state from this context. The column definitions stay module-level and therefore
 * referentially stable: TanStack's flexRender mounts a cell function as a component, so columns rebuilt on every
 * state change would remount every cell and throw away a half-typed edit.
 */
const GridContext = createContext<GridContextValue | null>(null);
function useGrid(): GridContextValue {
  const ctx = useContext(GridContext);
  if (!ctx) throw new Error('GridContext missing');
  return ctx;
}

function EditableCell({ row, field }: { row: ExtractedRow; field: FieldKey }) {
  const { bands, editable, cellErrors, onEdit, clearCellError } = useGrid();
  const error = cellErrors[cellErrorKey(row.id, field)];
  const onClear = () => clearCellError(row.id, field);
  const f = row.fields[field];
  const band = fieldBand(f, bands);
  const numeric = NUMERIC_FIELDS.has(field);
  const value = formatValue(f?.value);
  const notes = cellNotes(f);
  const canEdit = editable && row.state === 'pending';
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [saving, setSaving] = useState(false);
  const label = `${HEADER_OF[field]} of row ${row.seq}`;

  const begin = () => {
    setDraft(value || f?.raw || '');
    onClear();
    setEditing(true);
  };
  const commit = async () => {
    if (saving) return;
    if (draft.trim() === value.trim()) {
      setEditing(false);
      return;
    }
    setSaving(true);
    const ok = await onEdit(row, field, draft);
    setSaving(false);
    if (ok) setEditing(false);
  };

  if (editing) {
    return (
      <div className="p-0.5">
        <input
          autoFocus
          aria-label={`Edit ${label}`}
          aria-invalid={!!error}
          aria-describedby={error ? `err-${row.id}-${field}` : undefined}
          className="border-ring bg-background ring-ring/40 aria-invalid:border-destructive aria-invalid:ring-destructive/40 h-8 w-full rounded border px-1.5 text-sm ring-2 outline-none"
          value={draft}
          disabled={saving}
          inputMode={numeric ? 'decimal' : 'text'}
          onChange={(e) => {
            setDraft(e.target.value);
            onClear();
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') void commit();
            if (e.key === 'Escape') {
              onClear();
              setEditing(false);
            }
          }}
          onBlur={() => {
            if (!saving && !error) setEditing(false);
          }}
        />
        {error && (
          <p
            id={`err-${row.id}-${field}`}
            role="alert"
            className="text-destructive text-[11px] leading-tight"
          >
            {error}
          </p>
        )}
      </div>
    );
  }

  const text = `${label}: ${value || 'empty'}. Confidence band ${BAND_LABEL[band]}.`;
  return (
    <button
      type="button"
      disabled={!canEdit}
      onClick={begin}
      aria-label={`${text}${canEdit ? ' Press to edit.' : ''}${error ? ` Error: ${error}` : ''}`}
      title={[BAND_LABEL[band], ...notes].join('\n')}
      data-band={band}
      data-field={field}
      className={cn(
        'hover:ring-ring/60 relative flex h-10 w-full items-center gap-1 rounded-sm px-1.5 text-left text-sm hover:ring-1 disabled:cursor-default',
        BAND_CLASS[band],
        numeric && 'justify-end tabular-nums',
        error && 'ring-destructive ring-2',
      )}
    >
      <span aria-hidden className="text-[10px] opacity-70">
        {BAND_MARK[band]}
      </span>
      <span className="truncate">{value || <span className="text-muted-foreground">-</span>}</span>
      {error && (
        <span className="text-destructive text-[11px]" aria-hidden>
          !
        </span>
      )}
      {notes.length > 0 && <span className="sr-only">{notes.join('. ')}</span>}
    </button>
  );
}

function SelectCell({ row }: { row: ExtractedRow }) {
  const { selected, onSelect } = useGrid();
  return (
    <div className="flex justify-center">
      <Checkbox
        aria-label={`Select row ${row.seq} for accepting`}
        checked={selected.has(row.id)}
        disabled={row.state !== 'pending'}
        onCheckedChange={(v) => onSelect(row, v === true)}
      />
    </div>
  );
}

function SeqCell({ row }: { row: ExtractedRow }) {
  const { activeRowId, onActivate } = useGrid();
  return (
    <button
      type="button"
      aria-pressed={row.id === activeRowId}
      aria-label={`Show source of row ${row.seq} (page ${row.page})`}
      onClick={() => onActivate(row)}
      className="hover:bg-accent h-8 w-full rounded px-1 text-left text-xs tabular-nums"
    >
      {row.seq}
      <span className="text-muted-foreground"> p{row.page}</span>
    </button>
  );
}

function RowBandCell({ row }: { row: ExtractedRow }) {
  const { bands } = useGrid();
  return (
    <div className="flex flex-col gap-0.5 px-1">
      <BandTag band={rowBand(row, bands)} />
      {isInjection(row) && (
        <span className="text-destructive inline-flex items-center gap-1 text-[11px] font-semibold">
          <ShieldAlert className="size-3" aria-hidden /> Suspected injection
        </span>
      )}
    </div>
  );
}

function StateCell({ row }: { row: ExtractedRow }) {
  const { editable, onToggleExcluded } = useGrid();
  if (row.state === 'accepted')
    return (
      <span className="px-1.5 text-xs">
        Accepted
        {row.changeSetId !== null && (
          <>
            {' '}
            <Link className="underline" to={`/change-sets/${row.changeSetId}`}>
              change set #{row.changeSetId}
            </Link>
          </>
        )}
      </span>
    );
  return (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      disabled={!editable}
      onClick={() => onToggleExcluded(row)}
      aria-label={`${row.state === 'excluded' ? 'Include' : 'Exclude'} row ${row.seq}`}
    >
      {row.state === 'excluded' ? (
        <>
          <RotateCcw aria-hidden /> Include
        </>
      ) : (
        <>
          <Ban aria-hidden /> Exclude
        </>
      )}
    </Button>
  );
}

const COLUMNS: ColumnDef<ExtractedRow>[] = [
  { id: 'select', header: 'Pick', cell: ({ row }) => <SelectCell row={row.original} /> },
  { id: 'seq', header: 'Row', cell: ({ row }) => <SeqCell row={row.original} /> },
  { id: 'band', header: 'Row band', cell: ({ row }) => <RowBandCell row={row.original} /> },
  ...FIELD_COLUMNS.map<ColumnDef<ExtractedRow>>((c) => ({
    id: c.key,
    header: c.header,
    cell: ({ row }) => <EditableCell row={row.original} field={c.key} />,
  })),
  { id: 'state', header: 'State', cell: ({ row }) => <StateCell row={row.original} /> },
];

export function ReviewGrid(props: Props) {
  const { rows, bands, editable, selected, activeRowId, cellErrors, scrollRequest } = props;
  const { onEdit, onToggleExcluded, onSelect, onActivate, clearCellError } = props;

  const ctx = useMemo<GridContextValue>(
    () => ({
      bands,
      editable,
      selected,
      activeRowId,
      cellErrors,
      clearCellError,
      onEdit,
      onToggleExcluded,
      onSelect,
      onActivate,
    }),
    [
      bands,
      editable,
      selected,
      activeRowId,
      cellErrors,
      clearCellError,
      onEdit,
      onToggleExcluded,
      onSelect,
      onActivate,
    ],
  );

  const WIDTHS: Record<string, number> = {
    select: 48,
    seq: 76,
    band: 140,
    state: 150,
    ...Object.fromEntries(FIELD_COLUMNS.map((c) => [c.key, c.width])),
  };

  // eslint-disable-next-line react-hooks/incompatible-library -- third-party API, memoization skipped on purpose
  const table = useReactTable({
    data: rows,
    columns: COLUMNS,
    getRowId: (r) => String(r.id),
    getCoreRowModel: getCoreRowModel(),
  });
  const tableRows = table.getRowModel().rows;
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtualizer = useVirtualizer({
    count: tableRows.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 10,
    initialRect: { width: 1200, height: 600 },
  });

  useEffect(() => {
    if (!scrollRequest) return;
    const index = rows.findIndex((r) => r.id === scrollRequest.rowId);
    if (index >= 0) virtualizer.scrollToIndex(index, { align: 'center' });
    // eslint-disable-next-line react-hooks/exhaustive-deps -- only a new request scrolls
  }, [scrollRequest]);

  const items = virtualizer.getVirtualItems();
  const paddingTop = items.length ? items[0].start : 0;
  const paddingBottom = items.length ? virtualizer.getTotalSize() - items[items.length - 1].end : 0;
  const totalWidth = table.getVisibleLeafColumns().reduce((w, c) => w + (WIDTHS[c.id] ?? 100), 0);

  return (
    <GridContext.Provider value={ctx}>
      <div
        ref={scrollRef}
        className="bg-card h-full overflow-auto rounded-md border"
        role="region"
        aria-label="Extracted rows"
        tabIndex={0}
      >
        <table
          className="border-separate border-spacing-0 text-sm"
          style={{ width: totalWidth, tableLayout: 'fixed' }}
          aria-rowcount={tableRows.length + 1}
        >
          <colgroup>
            {table.getVisibleLeafColumns().map((c) => (
              <col key={c.id} style={{ width: WIDTHS[c.id] ?? 100 }} />
            ))}
          </colgroup>
          <thead className="bg-muted sticky top-0 z-10">
            <tr>
              {table.getHeaderGroups()[0].headers.map((h) => (
                <th
                  key={h.id}
                  scope="col"
                  className="overflow-hidden border-b px-1.5 py-1.5 text-left text-xs font-semibold"
                >
                  {String(h.column.columnDef.header)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {paddingTop > 0 && (
              <tr aria-hidden style={{ height: paddingTop }}>
                <td colSpan={COLUMNS.length} />
              </tr>
            )}
            {items.map((vi) => {
              const row = tableRows[vi.index];
              const o = row.original;
              const inj = isInjection(o);
              return (
                <tr
                  key={row.id}
                  aria-rowindex={vi.index + 2}
                  data-row-id={o.id}
                  data-injection={inj ? 'true' : undefined}
                  data-state={o.state}
                  onFocus={() => onActivate(o)}
                  className={cn(
                    'hover:bg-accent/30',
                    inj && 'bg-destructive/10 outline-destructive outline-2 -outline-offset-2',
                    o.id === activeRowId && 'bg-accent/50',
                    o.state === 'excluded' && 'opacity-50',
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
                <td colSpan={COLUMNS.length} />
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </GridContext.Provider>
  );
}
