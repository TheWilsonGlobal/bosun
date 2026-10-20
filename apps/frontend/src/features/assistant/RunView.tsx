import { useEffect, useState } from 'react';
import { Loader2, WifiOff } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import { cancelRun } from './api';
import { PHASE_LABELS, STATUS_LABELS } from './messages';
import { RunResult } from './RunResult';
import { liveBudgets } from './runState';
import { StepList } from './StepList';
import { isTerminal } from './types';
import { useAgentRun, type UseAgentRunOptions } from './useAgentRun';

function Meter({ label, value, max }: { label: string; value: number; max: number }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0;
  return (
    <div className="text-xs">
      <div className="flex justify-between">
        <span>{label}</span>
        <span className="tabular-nums">
          {value.toLocaleString()}
          {max > 0 && ` / ${max.toLocaleString()}`}
        </span>
      </div>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={max}
        aria-valuenow={Math.min(value, max)}
        className="bg-muted mt-0.5 h-1.5 overflow-hidden rounded-full"
      >
        <div
          className={pct >= 90 ? 'bg-warning h-full' : 'bg-primary h-full'}
          style={{ width: `${pct}%` }}
        />
      </div>
    </div>
  );
}

export interface RunViewProps extends UseAgentRunOptions {
  runId: number;
  live: boolean;
  /** Called when the run reaches a terminal status (the panel refreshes its history). */
  onFinished?: () => void;
}

export function RunView({ runId, live, onFinished, ...options }: RunViewProps) {
  const { state, transport, error, loading } = useAgentRun(runId, live, options);
  const [cancelError, setCancelError] = useState<unknown>(null);
  const [cancelling, setCancelling] = useState(false);
  const run = state.run;
  const budgets = liveBudgets(state);
  const running = run !== null && !isTerminal(run.status);

  // Tell the panel when this run ends (it refreshes its history).
  const finishedId = run && isTerminal(run.status) ? run.id : null;
  useEffect(() => {
    if (finishedId !== null) onFinished?.();
  }, [finishedId, onFinished]);

  const cancel = async () => {
    setCancelling(true);
    setCancelError(null);
    try {
      await cancelRun(runId);
    } catch (e) {
      // 409: it finished in the meantime; the done event / next poll shows the real status.
      if (!(e instanceof ApiError && e.status === 409)) setCancelError(e);
    } finally {
      setCancelling(false);
    }
  };

  return (
    <section aria-label={`Run ${runId}`} className="grid gap-3">
      <div className="bg-muted/50 rounded-md px-3 py-2 text-sm">
        <p className="text-muted-foreground text-xs">Run #{runId}</p>
        <p className="break-words">{run?.message ?? '...'}</p>
      </div>

      {loading && !run && <p className="text-muted-foreground text-sm">Loading the run...</p>}
      {error != null && !run && (
        <p role="alert" className="text-destructive text-sm">
          {describeError(error).title}: {describeError(error).message}
        </p>
      )}

      {run && (
        <div className="grid gap-2">
          <div className="flex items-center gap-2 text-sm">
            {running && <Loader2 className="size-4 animate-spin" aria-hidden />}
            <span data-testid="run-status" className="font-medium">
              {STATUS_LABELS[run.status] ?? run.status}
            </span>
            {running && run.phase && (
              <span className="text-muted-foreground">
                - {PHASE_LABELS[run.phase] ?? run.phase}
              </span>
            )}
            {running && (
              <Button
                variant="outline"
                size="sm"
                className="ml-auto"
                onClick={() => void cancel()}
                disabled={cancelling}
              >
                {cancelling ? 'Cancelling...' : 'Cancel'}
              </Button>
            )}
          </div>
          <Meter label="Tool calls" value={budgets.toolCalls} max={budgets.maxToolCalls} />
          <Meter label="Tokens" value={budgets.tokens} max={budgets.maxTokens} />
          {run.model && (
            <p className="text-muted-foreground text-xs">
              {run.model}
              {run.promptVersion && `, prompt ${run.promptVersion}`}
            </p>
          )}
        </div>
      )}

      {transport === 'polling' && running && (
        <p className="text-muted-foreground flex items-center gap-1.5 text-xs" role="status">
          <WifiOff className="size-3.5" aria-hidden />
          Live connection unavailable; refreshing every few seconds.
        </p>
      )}
      {error != null && run && running && (
        <p role="alert" className="text-warning-foreground bg-warning/30 rounded px-2 py-1 text-xs">
          Connection problem: {describeError(error).message} Trying again.
        </p>
      )}
      {cancelError != null && (
        <p role="alert" className="text-destructive text-sm">
          {describeError(cancelError).title}: {describeError(cancelError).message}
        </p>
      )}

      <StepList steps={state.steps} />
      {run && <RunResult run={run} />}
    </section>
  );
}
