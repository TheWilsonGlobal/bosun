import { useQuery } from '@tanstack/react-query';
import { ChevronDown, ChevronUp } from 'lucide-react';
import { fetchRuns } from './api';
import { describeError } from '@/lib/api/errors';
import { STATUS_LABELS } from './messages';
import { RunView } from './RunView';
import type { UseAgentRunOptions } from './useAgentRun';
import { cn } from '@/lib/utils';

const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });

export const runsKey = (projectId: number) => ['agent-runs', projectId] as const;

export interface RunHistoryProps {
  projectId: number;
  selected?: { id: number; live: boolean } | null;
  selectedId?: number | null;
  onSelect: (selected: { id: number; live: boolean } | null) => void;
  onFinished?: () => void;
  runViewOptions?: UseAgentRunOptions;
}

export function RunHistory({
  projectId,
  selected,
  selectedId,
  onSelect,
  onFinished,
  runViewOptions,
}: RunHistoryProps) {
  const q = useQuery({ queryKey: runsKey(projectId), queryFn: () => fetchRuns(projectId) });

  const activeSelectedId = selected ? selected.id : (selectedId ?? null);
  const isSelectedLive = selected ? selected.live : false;

  return (
    <section aria-label="Run history" className="grid gap-2">
      <h3 className="text-sm font-semibold">History</h3>
      {q.isPending && <p className="text-muted-foreground text-sm">Loading runs...</p>}
      {q.isError && (
        <p role="alert" className="text-destructive text-sm">
          {describeError(q.error).title}: {describeError(q.error).message}
        </p>
      )}

      {/* Standalone card if a new run was just started and is not yet in q.data items */}
      {selected && (!q.data || !q.data.items.some((r) => r.id === selected.id)) && (
        <div className="rounded-lg border bg-card/80 p-3 shadow-xs">
          <div className="flex items-center justify-between pb-2 mb-2 border-b">
            <span className="font-medium text-sm">Run #{selected.id} (Live)</span>
            <button
              type="button"
              onClick={() => onSelect(null)}
              className="text-muted-foreground hover:text-foreground p-1 rounded-md cursor-pointer"
              aria-label="Collapse run"
            >
              <ChevronUp className="size-4" />
            </button>
          </div>
          <RunView
            key={`${selected.id}-${selected.live}`}
            runId={selected.id}
            live={selected.live}
            onFinished={onFinished}
            {...runViewOptions}
          />
        </div>
      )}

      {q.data && q.data.items.length === 0 && !selected && (
        <p className="text-muted-foreground text-sm">No assistant runs in this project yet.</p>
      )}

      {q.data && q.data.items.length > 0 && (
        <ul className="grid gap-2">
          {q.data.items.map((r) => {
            const isExpanded = activeSelectedId === r.id;
            const live = isExpanded ? isSelectedLive || r.status === 'running' : r.status === 'running';

            return (
              <li
                key={r.id}
                className={cn(
                  'rounded-lg border transition-colors overflow-hidden',
                  isExpanded ? 'border-primary/50 bg-card shadow-xs' : 'border-border bg-card/60 hover:bg-card/90',
                )}
              >
                <button
                  type="button"
                  aria-expanded={isExpanded}
                  aria-current={isExpanded ? 'true' : undefined}
                  onClick={() => {
                    if (isExpanded) {
                      onSelect(null);
                    } else {
                      onSelect({ id: r.id, live: r.status === 'running' });
                    }
                  }}
                  className={cn(
                    'hover:bg-accent/50 focus-visible:ring-ring/50 flex w-full items-center justify-between px-3 py-2 text-left text-sm outline-none focus-visible:ring-[2px] transition-colors cursor-pointer select-none',
                    isExpanded && 'bg-accent/30',
                  )}
                >
                  <div className="min-w-0 flex-1 pr-2">
                    <span className="line-clamp-2 break-words font-medium">{r.message}</span>
                    <span className="text-muted-foreground mt-0.5 flex flex-wrap items-center justify-between gap-1 text-xs">
                      <span className="font-medium">{STATUS_LABELS[r.status] ?? r.status}</span>
                      <span className="font-mono">
                        #{r.id} · {when(r.startedAt)}
                      </span>
                    </span>
                  </div>
                  <div className="text-muted-foreground shrink-0 pl-1">
                    {isExpanded ? (
                      <ChevronUp className="size-4 text-primary" aria-label="Collapse chat" />
                    ) : (
                      <ChevronDown className="size-4" aria-label="Expand chat" />
                    )}
                  </div>
                </button>

                {/* Detail of chat displayed directly bellow chat */}
                {isExpanded && (
                  <div className="border-t p-3 bg-muted/20">
                    <RunView
                      key={`${r.id}-${live}`}
                      runId={r.id}
                      live={live}
                      onFinished={onFinished}
                      {...runViewOptions}
                    />
                  </div>
                )}
              </li>
            );
          })}
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
