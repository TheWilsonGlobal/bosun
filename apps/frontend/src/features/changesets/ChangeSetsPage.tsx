import { Link, useSearchParams } from 'react-router-dom';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Status } from '@/components/Status';
import { Button } from '@/components/ui/button';
import { useProject } from '@/features/project/ProjectContext';
import { fetchChangeSets } from '@/lib/api/endpoints';
import { describeError } from '@/lib/api/errors';
import type { ChangeSetStatus } from '@/lib/api/types';
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

  if (!project) return <Status>Select a project.</Status>;
  const pages = Math.max(1, Math.ceil((q.data?.total ?? 0) / PAGE_SIZE));

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
        <div className="overflow-auto rounded-md border">
          <table className="w-full text-sm">
            <thead className="bg-muted text-left text-xs">
              <tr>
                {[
                  '#',
                  'Title',
                  'Status',
                  'Source',
                  'Ops',
                  'Created by',
                  'Created',
                  'Approvals',
                ].map((h) => (
                  <th key={h} scope="col" className="px-3 py-2 font-semibold">
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {q.data.items.map((c) => (
                <tr key={c.id} className="hover:bg-accent/40 border-t">
                  <td className="px-3 py-2 tabular-nums">{c.id}</td>
                  <td className="px-3 py-2">
                    <Link
                      className="text-primary font-medium underline-offset-2 hover:underline"
                      to={`/change-sets/${c.id}`}
                    >
                      {c.title}
                    </Link>
                  </td>
                  <td className="px-3 py-2">
                    <StatusBadge status={c.status} />
                  </td>
                  <td className="px-3 py-2">{c.source}</td>
                  <td className="px-3 py-2 tabular-nums">{c.opCount}</td>
                  <td className="px-3 py-2">{c.createdBy}</td>
                  <td className="px-3 py-2 whitespace-nowrap">{formatTime(c.createdAt)}</td>
                  <td className="px-3 py-2">
                    {c.requiresSecondApprover ? (
                      <span title="SIL/ESD: two approvers">
                        {[c.approvedBy, c.secondApprovedBy].filter(Boolean).join(' + ') ||
                          'needs 2'}
                      </span>
                    ) : (
                      (c.approvedBy ?? '-')
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {q.data && pages > 1 && (
        <nav aria-label="Pagination" className="flex items-center gap-2 text-sm">
          <Button
            variant="outline"
            size="sm"
            disabled={page <= 1}
            onClick={() => set({ page: String(page - 1) })}
          >
            Previous
          </Button>
          <span className="tabular-nums">
            Page {page} of {pages}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={page >= pages}
            onClick={() => set({ page: String(page + 1) })}
          >
            Next
          </Button>
        </nav>
      )}
    </div>
  );
}
