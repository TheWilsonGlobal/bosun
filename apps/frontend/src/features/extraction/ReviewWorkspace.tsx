import { useCallback, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { AlertCircle, CheckCircle2, ShieldAlert } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { ApiError } from '@/lib/api/client';
import { useAuth } from '@/features/auth/session';
import { acceptRows, patchRow } from './api';
import { countBy, filterRows, ROW_FILTERS, rowBand, type RowFilter } from './bands';
import { buildExcludePatch, buildFieldPatch } from './edit';
import { applyPatchResult } from './merge';
import { describeAcceptError, describePatchError, type AcceptFailure } from './messages';
import { FlaggedOverrideDialog } from './FlaggedOverrideDialog';
import { cellErrorKey, ReviewGrid } from './ReviewGrid';
import { SourcePanel } from './SourcePanel';
import { Legend, SummaryBar } from './parts';
import { extractionKey } from './useExtraction';
import type { AcceptResult, ExtractedRow, Extraction } from './types';
import { cn } from '@/lib/utils';

export function ReviewWorkspace({ extraction }: { extraction: Extraction }) {
  const qc = useQueryClient();
  const { openLogin } = useAuth();
  const { id, bands, rows } = extraction;
  const editable = extraction.status === 'completed' || extraction.status === 'partial';

  const [filter, setFilter] = useState<RowFilter>('all');
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set());
  const [activeRowId, setActiveRowId] = useState<number | null>(null);
  const [cellErrors, setCellErrors] = useState<Record<string, string>>({});
  const [scrollRequest, setScrollRequest] = useState<{ rowId: number; nonce: number } | null>(null);
  const [accepting, setAccepting] = useState(false);
  const [failure, setFailure] = useState<AcceptFailure | null>(null);
  const [accepted, setAccepted] = useState<AcceptResult | null>(null);
  // Rows the server refused as flagged; non-null = the confirmation dialog is open (nonce remounts it).
  const [override, setOverride] = useState<{ rowIds: number[]; nonce: number } | null>(null);

  const counts = useMemo(() => countBy(rows, bands), [rows, bands]);
  const visible = useMemo(() => filterRows(rows, filter, bands), [rows, filter, bands]);
  const activeRow = rows.find((r) => r.id === activeRowId) ?? null;

  const refetch = useCallback(
    () => qc.invalidateQueries({ queryKey: extractionKey(id) }),
    [qc, id],
  );

  const notify = useCallback(
    (error: unknown, title: string, message: string) => {
      toast.error(title, { description: message });
      if (error instanceof ApiError && error.code === 'unauthorized') openLogin();
    },
    [openLogin],
  );

  const clearCellError = useCallback((rowId: number, field: string) => {
    setCellErrors((prev) => {
      const k = cellErrorKey(rowId, field);
      if (!(k in prev)) return prev;
      const { [k]: _gone, ...rest } = prev;
      void _gone;
      return rest;
    });
  }, []);

  const applyPatch = useCallback(
    async (row: ExtractedRow, body: ReturnType<typeof buildFieldPatch>, field: string | null) => {
      try {
        const res = await patchRow(id, row.id, body);
        qc.setQueryData<Extraction>(extractionKey(id), (old) =>
          old ? applyPatchResult(old, res) : old,
        );
        setCellErrors((prev) =>
          Object.fromEntries(Object.entries(prev).filter(([k]) => !k.startsWith(`${row.id}:`))),
        );
        // A row that is no longer open cannot stay selected.
        if (res.row.state !== 'pending')
          setSelected((s) => new Set([...s].filter((x) => x !== row.id)));
        return true;
      } catch (e) {
        const f = describePatchError(e);
        const target = f.field ?? field;
        if (e instanceof ApiError && e.code === 'invalid_field' && target) {
          setCellErrors((prev) => ({ ...prev, [cellErrorKey(row.id, target)]: f.message }));
        } else {
          notify(e, f.title, f.message);
        }
        if (f.refetch) void refetch();
        return false;
      }
    },
    [id, qc, notify, refetch],
  );

  const onEdit = useCallback(
    (row: ExtractedRow, field: string, text: string) =>
      applyPatch(row, buildFieldPatch(field, text), field),
    [applyPatch],
  );
  const onToggleExcluded = useCallback(
    (row: ExtractedRow) => void applyPatch(row, buildExcludePatch(row.state !== 'excluded'), null),
    [applyPatch],
  );
  const onSelect = useCallback((row: ExtractedRow, on: boolean) => {
    setSelected((s) => {
      const next = new Set(s);
      if (on) next.add(row.id);
      else next.delete(row.id);
      return next;
    });
  }, []);
  const onActivate = useCallback((row: ExtractedRow) => setActiveRowId(row.id), []);

  const autoRows = rows.filter((r) => r.state === 'pending' && rowBand(r, bands) === 'auto');
  const selectAllAuto = () => setSelected(new Set(autoRows.map((r) => r.id)));

  const jumpTo = (rowId: number) => {
    const row = rows.find((r) => r.id === rowId);
    if (!row) return;
    if (!filterRows([row], filter, bands).length) setFilter('all');
    setActiveRowId(rowId);
    setScrollRequest({ rowId, nonce: Date.now() });
  };

  const accept = async (allowFlagged?: number[]) => {
    setFailure(null);
    setAccepted(null);
    setAccepting(true);
    try {
      const res = await acceptRows(id, [...selected], allowFlagged);
      setOverride(null);
      setAccepted(res);
      setSelected(new Set());
      await refetch();
      void qc.invalidateQueries({ queryKey: ['change-sets'] });
      void qc.invalidateQueries({ queryKey: ['documents'] });
    } catch (e) {
      const f = describeAcceptError(e);
      if (f.kind === 'flagged_rows_need_override' && f.rowIds.length > 0) {
        setOverride({ rowIds: f.rowIds, nonce: Date.now() });
      } else {
        setOverride(null);
        setFailure(f);
      }
      if (e instanceof ApiError && e.code === 'unauthorized') openLogin();
      if (f.refetch) void refetch();
      if (f.kind === 'unresolved_must_fix') setFilter('must_fix');
    } finally {
      setAccepting(false);
    }
  };

  const openMustFix = (
    failure?.kind === 'unresolved_must_fix' && failure.rowIds.length > 0
      ? failure.rowIds.map((rid) => rows.find((r) => r.id === rid)).filter((r) => !!r)
      : failure?.kind === 'unresolved_must_fix'
        ? rows.filter((r) => r.state === 'pending' && rowBand(r, bands) === 'must_fix')
        : []
  ) as ExtractedRow[];

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2">
      <SummaryBar summary={extraction.summary} />
      <Legend bands={bands} />

      <div className="flex flex-wrap items-center gap-2">
        <div role="group" aria-label="Filter rows" className="flex flex-wrap gap-1">
          {ROW_FILTERS.map((f) => (
            <Button
              key={f.value}
              type="button"
              size="sm"
              variant={filter === f.value ? 'default' : 'outline'}
              aria-pressed={filter === f.value}
              onClick={() => setFilter(f.value)}
              className={cn(
                f.value === 'injection' && counts.injection > 0 && 'border-destructive',
              )}
            >
              {f.label} ({counts[f.value]})
            </Button>
          ))}
        </div>
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={selectAllAuto}
            disabled={!editable || autoRows.length === 0}
          >
            Select all auto-accept rows ({autoRows.length})
          </Button>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            onClick={() => setSelected(new Set())}
            disabled={selected.size === 0}
          >
            Clear selection
          </Button>
          <Button
            type="button"
            size="sm"
            onClick={() => void accept(undefined)}
            disabled={!editable || selected.size === 0 || accepting}
          >
            {accepting ? 'Accepting...' : `Accept ${selected.size} selected row(s)`}
          </Button>
        </div>
      </div>

      {failure && (
        <div
          role="alert"
          className="border-destructive bg-destructive/10 rounded-md border px-3 py-2 text-sm"
        >
          <p className="flex items-center gap-1.5 font-semibold">
            <AlertCircle className="size-4" aria-hidden /> {failure.title}
          </p>
          <p>{failure.message}</p>
          {openMustFix.length > 0 && (
            <ul className="mt-1 flex flex-wrap gap-1" aria-label="Open must-fix rows">
              {openMustFix.map((r) => (
                <li key={r.id}>
                  <Button type="button" size="sm" variant="outline" onClick={() => jumpTo(r.id)}>
                    Row {r.seq}
                    {r.fields.tagName?.value ? ` (${String(r.fields.tagName.value)})` : ''}
                  </Button>
                </li>
              ))}
            </ul>
          )}
          {failure.kind !== 'unresolved_must_fix' && failure.rowIds.length > 0 && (
            <ul className="mt-1 flex flex-wrap gap-1" aria-label="Rows concerned">
              {failure.rowIds.map((rid) => {
                const r = rows.find((x) => x.id === rid);
                return r ? (
                  <li key={rid}>
                    <Button type="button" size="sm" variant="outline" onClick={() => jumpTo(rid)}>
                      Row {r.seq}
                    </Button>
                  </li>
                ) : null;
              })}
            </ul>
          )}
        </div>
      )}

      {override && (
        <FlaggedOverrideDialog
          key={override.nonce}
          rows={override.rowIds
            .map((rid) => rows.find((r) => r.id === rid))
            .filter((r): r is ExtractedRow => !!r)}
          busy={accepting}
          onCancel={() => setOverride(null)}
          onConfirm={() => void accept(override.rowIds)}
        />
      )}

      {accepted && (
        <div
          role="status"
          className="border-success bg-success/10 rounded-md border px-3 py-2 text-sm"
        >
          <p className="flex items-center gap-1.5 font-semibold">
            <CheckCircle2 className="size-4" aria-hidden /> {accepted.acceptedRowIds.length} row(s)
            accepted into {accepted.changeSets.length} draft change set(s)
          </p>
          <p>Nothing is applied yet. Review and approve each change set:</p>
          <ul className="list-disc pl-5">
            {accepted.changeSets.map((cs) => (
              <li key={cs.id}>
                <Link className="underline" to={`/change-sets/${cs.id}`}>
                  #{cs.id} {cs.title}
                </Link>{' '}
                ({cs.opCount} operations)
              </li>
            ))}
          </ul>
        </div>
      )}

      {counts.injection > 0 && (
        <p
          className="text-destructive flex items-center gap-1.5 text-sm"
          data-testid="injection-notice"
        >
          <ShieldAlert className="size-4" aria-hidden /> {counts.injection} row(s) contain
          instruction-like text from the document. It is shown as plain text and is never followed.
          Check these rows before accepting.
        </p>
      )}

      {rows.length === 0 ? (
        <p className="text-muted-foreground p-6 text-center text-sm">
          The extraction found no rows in this document.
        </p>
      ) : visible.length === 0 ? (
        <p className="text-muted-foreground p-6 text-center text-sm">No rows match this filter.</p>
      ) : (
        <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_minmax(260px,24rem)] gap-2">
          <ReviewGrid
            rows={visible}
            bands={bands}
            editable={editable}
            selected={selected}
            activeRowId={activeRowId}
            cellErrors={cellErrors}
            clearCellError={clearCellError}
            scrollRequest={scrollRequest}
            onEdit={onEdit}
            onToggleExcluded={onToggleExcluded}
            onSelect={onSelect}
            onActivate={onActivate}
          />
          <div className="min-h-0 overflow-hidden rounded-md border">
            <SourcePanel extractionId={id} row={activeRow} />
          </div>
        </div>
      )}
    </div>
  );
}
