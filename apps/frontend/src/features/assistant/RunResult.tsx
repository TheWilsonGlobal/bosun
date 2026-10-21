import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { AlertCircle, AlertTriangle, CheckCircle2, Info } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { DiffTable } from '@/features/changesets/DiffTable';
import { ViolationsList } from '@/features/changesets/parts';
import { fetchDiff } from '@/lib/api/endpoints';
import { describeError } from '@/lib/api/errors';
import { cn } from '@/lib/utils';
import { describeOutcome, ERROR_MESSAGES, type Tone } from './messages';
import type { AgentRun } from './types';

const TONE: Record<Tone, { cls: string; icon: typeof Info }> = {
  success: { cls: 'border-success/50 bg-success/10', icon: CheckCircle2 },
  info: { cls: 'border-primary/40 bg-primary/10', icon: Info },
  warning: { cls: 'border-warning bg-warning/20', icon: AlertTriangle },
  error: { cls: 'border-destructive/50 bg-destructive/10', icon: AlertCircle },
};

/** The diff of the draft the run produced. Reviewing and acting on it stays on the change-set page (this only links there). */
function DraftSummary({ changeSetId }: { changeSetId: number }) {
  const q = useQuery({
    queryKey: ['change-set-diff', changeSetId],
    queryFn: () => fetchDiff(changeSetId),
  });
  return (
    <section aria-label="Draft" className="grid gap-2">
      <h3 className="text-sm font-semibold">Draft change set #{changeSetId}</h3>
      {q.isPending && <p className="text-muted-foreground text-sm">Loading the diff...</p>}
      {q.isError && (
        <p role="alert" className="text-destructive text-sm">
          {describeError(q.error).title}: {describeError(q.error).message}
        </p>
      )}
      {q.data && (
        <>
          <p className="text-muted-foreground text-xs">
            {q.data.rows.length} operation{q.data.rows.length === 1 ? '' : 's'}
            {q.data.requiresSecondApprover && ' - touches SIL/ESD tags: two approvers required'}
          </p>
          <DiffTable rows={q.data.rows} violations={q.data.violations} />
          <ViolationsList violations={q.data.violations} title="Rule check" />
        </>
      )}
      <Button asChild className="w-fit">
        <Link to={`/change-sets/${changeSetId}`}>Review &amp; approve</Link>
      </Button>
    </section>
  );
}

export function RunResult({ run }: { run: AgentRun }) {
  const outcome = describeOutcome(run);
  if (!outcome) return null;
  const { cls, icon: Icon } = TONE[outcome.tone];
  // Raw provider text is only a detail when a friendly, code-specific message already says what happened.
  const rawDetail =
    run.errorCode && run.errorCode in ERROR_MESSAGES && run.errorMessage?.trim()
      ? run.errorMessage.trim()
      : null;
  return (
    <div className="grid gap-3">
      <div
        role={outcome.tone === 'error' ? 'alert' : 'status'}
        data-testid="run-outcome"
        data-status={run.status}
        data-error-code={run.errorCode ?? undefined}
        className={cn('flex gap-2 rounded-md border px-3 py-2 text-sm', cls)}
      >
        <Icon className="mt-0.5 size-4 shrink-0" aria-hidden />
        <div>
          <p className="font-semibold">{outcome.title}</p>
          <p>{outcome.message}</p>
          {rawDetail && (
            <details className="mt-1 text-xs">
              <summary className="cursor-pointer">Details</summary>
              <p className="text-muted-foreground mt-1 break-words">{rawDetail}</p>
            </details>
          )}
        </div>
      </div>
      {run.changeSetId !== null && <DraftSummary changeSetId={run.changeSetId} />}
    </div>
  );
}
