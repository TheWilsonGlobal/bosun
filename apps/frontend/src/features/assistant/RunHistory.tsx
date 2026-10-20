import { useQuery } from '@tanstack/react-query';
import { fetchRuns } from './api';
import { describeError } from '@/lib/api/errors';
import { STATUS_LABELS } from './messages';
import { cn } from '@/lib/utils';

const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });

export const runsKey = (projectId: number) => ['agent-runs', projectId] as const;

export function RunHistory({
  projectId,
  selectedId,
  onSelect,
}: {
  projectId: number;
  selectedId: number | null;
  onSelect: (id: number, running: boolean) => void;
}) {
  const q = useQuery({ queryKey: runsKey(projectId), queryFn: () => fetchRuns(projectId) });

  return (
    <section aria-label="Run history" className="grid gap-1.5">
      <h3 className="text-sm font-semibold">History</h3>
      {q.isPending && <p className="text-muted-foreground text-sm">Loading runs...</p>}
      {q.isError && (
        <p role="alert" className="text-destructive text-sm">
          {describeError(q.error).title}: {describeError(q.error).message}
        </p>
      )}
      {q.data && q.data.items.length === 0 && (
        <p className="text-muted-foreground text-sm">No assistant runs in this project yet.</p>
      )}
      {q.data && q.data.items.length > 0 && (
        <ul className="grid gap-1">
          {q.data.items.map((r) => (
            <li key={r.id}>
              <button
                type="button"
                aria-current={r.id === selectedId ? 'true' : undefined}
                onClick={() => onSelect(r.id, r.status === 'running')}
                className={cn(
                  'hover:bg-accent focus-visible:ring-ring/50 w-full rounded-md border px-2 py-1.5 text-left text-sm outline-none focus-visible:ring-[3px]',
                  r.id === selectedId && 'bg-accent',
                )}
              >
                <span className="line-clamp-2 break-words">{r.message}</span>
                <span className="text-muted-foreground mt-0.5 flex justify-between text-xs">
                  <span>{STATUS_LABELS[r.status] ?? r.status}</span>
                  <span>
                    #{r.id} - {when(r.startedAt)}
                  </span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {q.data && q.data.total > q.data.items.length && (
        <p className="text-muted-foreground text-xs">
          Showing the newest {q.data.items.length} of {q.data.total} runs.
        </p>
      )}
    </section>
  );
}
