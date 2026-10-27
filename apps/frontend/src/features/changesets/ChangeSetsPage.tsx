import { useMemo } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Status } from '@/components/Status';
import { DataTable, type Column } from '@/components/DataTable';
import { useProject } from '@/features/project/ProjectContext';
import { fetchChangeSets } from '@/lib/api/endpoints';
import { describeError } from '@/lib/api/errors';
import type { ChangeSetStatus, ChangeSetSummary } from '@/lib/api/types';
import { StatusBadge } from './parts';

const STATUSES: ChangeSetStatus[] = ['draft', 'validated', 'approved', 'applied', 'rejected'];
const PAGE_SIZE = 25;

export const formatTime = (iso: string | null) =>
  iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '-';

export function ChangeSetsPage() {
  const { project } = useProject();
  const [params, setParams] = useSearchParams();
  const status = (STATUSES as string[]).includes(params.get('status') ?? '')
    ? (params.get('status') as ChangeSetStatus)
    : undefined;
  const page = Math.max(1, Number(params.get('page')) || 1);

  const q = useQuery({
    queryKey: ['change-sets', project?.id, status ?? 'all', page],
    queryFn: () => fetchChangeSets(project!.id, { status, page, pageSize: PAGE_SIZE }),
    enabled: !!project,
    placeholderData: keepPreviousData,
  });

  const set = (patch: Record<string, string | null>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setParams(next);
  };

  const columns = useMemo<Column<ChangeSetSummary>[]>(
    () => [
      {
        key: 'id',
        header: '#',
        width: 60,
        className: 'tabular-nums font-mono',
        render: (c) => c.id,
      },
      {
        key: 'title',
        header: 'Title',
        render: (c) => (
          <Link
            className="text-primary font-medium underline-offset-2 hover:underline"
            to={`/change-sets/${c.id}`}
          >
            {c.title}
          </Link>
        ),
      },
      {
        key: 'status',
        header: 'Status',
        render: (c) => <StatusBadge status={c.status} />,
      },
      {
        key: 'source',
        header: 'Source',
        render: (c) => c.source,
      },
      {
        key: 'opCount',
        header: 'Ops',
        className: 'tabular-nums',
        render: (c) => c.opCount,
      },
      {
        key: 'createdBy',
        header: 'Created by',
        render: (c) => c.createdBy,
      },
      {
        key: 'createdAt',
        header: 'Created',
        className: 'whitespace-nowrap',
        render: (c) => formatTime(c.createdAt),
      },
      {
        key: 'approvals',
        header: 'Approvals',
        render: (c) =>
          c.requiresSecondApprover ? (
            <span title="SIL/ESD: two approvers">
              {[c.approvedBy, c.secondApprovedBy].filter(Boolean).join(' + ') || 'needs 2'}
            </span>
          ) : (
            c.approvedBy ?? '-'
          ),
      },
    ],
    [],
  );

  if (!project) return <Status>Select a project.</Status>;

  return (
    <div className="flex h-full flex-col gap-3 overflow-auto p-4">
      <div className="flex items-center gap-3">
        <h1 className="text-lg font-semibold">Change sets</h1>
        <label htmlFor="cs-status" className="text-muted-foreground ml-auto text-sm">
          Status
        </label>
        <select
          id="cs-status"
          className="border-input bg-background h-8 rounded-md border px-1.5 text-sm"
          value={status ?? ''}
          onChange={(e) => set({ status: e.target.value || null, page: null })}
        >
          <option value="">All</option>
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </div>

      {q.isPending ? (
        <Status>Loading change sets...</Status>
      ) : q.isError ? (
        <Status tone="error" title={describeError(q.error).title}>
          {describeError(q.error).message}
        </Status>
      ) : q.data.items.length === 0 ? (
        <Status>
          {status
            ? `No ${status} change sets.`
            : 'No change sets yet. Edit tags in the grid to create one.'}
        </Status>
      ) : (
        <DataTable
          data={q.data.items}
          columns={columns}
          caption="Change sets"
          striped
          pagination={{
            page,
            pageSize: PAGE_SIZE,
            total: q.data.total,
            itemLabel: 'change sets',
            onPageChange: (p) => set({ page: String(p) }),
          }}
        />
      )}
    </div>
  );
}
