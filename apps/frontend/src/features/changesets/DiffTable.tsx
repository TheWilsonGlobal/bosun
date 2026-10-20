import { useMemo } from 'react';
import { flexRender, getCoreRowModel, useReactTable, type ColumnDef } from '@tanstack/react-table';
import { ArrowRight } from 'lucide-react';
import { cn } from '@/lib/utils';
import type { DiffRow, Violation } from '@/lib/api/types';

export function DiffTable({
  rows,
  violations,
}: {
  rows: DiffRow[];
  violations: readonly Violation[];
}) {
  const bad = useMemo(() => {
    const m = new Map<string, 'error' | 'warning'>();
    for (const v of violations) {
      if (!v.tagName) continue;
      if (v.severity === 'error') m.set(v.tagName, 'error');
      else if (v.severity === 'warning' && !m.has(v.tagName)) m.set(v.tagName, 'warning');
    }
    return m;
  }, [violations]);

  const columns = useMemo<ColumnDef<DiffRow>[]>(
    () => [
      { id: 'index', header: '#', accessorFn: (r) => r.index + 1 },
      {
        id: 'tagName',
        header: 'Tag',
        accessorFn: (r) => r.tagName,
        cell: ({ row }) => <span className="font-mono text-[13px]">{row.original.tagName}</span>,
      },
      { id: 'type', header: 'Operation', accessorFn: (r) => r.type },
      { id: 'field', header: 'Field', accessorFn: (r) => r.field ?? '-' },
      {
        id: 'change',
        header: 'Old -> New',
        cell: ({ row }) => (
          <span className="flex flex-wrap items-center gap-2">
            <span className="text-muted-foreground line-through decoration-1">
              {row.original.old ?? '(empty)'}
            </span>
            <ArrowRight className="size-3.5 shrink-0" aria-label="becomes" />
            <span className="font-medium">{row.original.new ?? '(empty)'}</span>
          </span>
        ),
      },
    ],
    [],
  );

  // eslint-disable-next-line react-hooks/incompatible-library -- third-party API, memoization skipped on purpose
  const table = useReactTable({ data: rows, columns, getCoreRowModel: getCoreRowModel() });

  if (rows.length === 0) {
    return <p className="text-muted-foreground text-sm">This change set has no operations yet.</p>;
  }

  return (
    <div className="overflow-auto rounded-md border">
      <table className="w-full text-sm" aria-label="Change set operations">
        <thead className="bg-muted text-left text-xs">
          {table.getHeaderGroups().map((hg) => (
            <tr key={hg.id}>
              {hg.headers.map((h) => (
                <th key={h.id} scope="col" className="px-3 py-2 font-semibold">
                  {flexRender(h.column.columnDef.header, h.getContext())}
                </th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map((row) => {
            const sev = bad.get(row.original.tagName);
            return (
              <tr
                key={row.id}
                data-violation={sev}
                className={cn(
                  'border-t',
                  sev === 'error' && 'bg-destructive/10',
                  sev === 'warning' && 'bg-warning/15',
                )}
              >
                {row.getVisibleCells().map((c) => (
                  <td key={c.id} className="px-3 py-1.5 align-top">
                    {flexRender(
                      c.column.columnDef.cell ?? ((ctx) => String(ctx.getValue())),
                      c.getContext(),
                    )}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
