import { AlertCircle, CheckCircle2, ListChecks, ShieldCheck, Square, Wrench } from 'lucide-react';
import { ViolationsList } from '@/features/changesets/parts';
import { cn } from '@/lib/utils';
import { describeErrorCode } from './messages';
import {
  errorOf,
  finalOf,
  planOf,
  previewArgs,
  toolCallOf,
  toolResultOf,
  verifyOf,
} from './stepPayloads';
import type { AgentStep } from './types';

function Json({ value, label }: { value: unknown; label: string }) {
  return (
    <details className="text-xs">
      <summary className="text-muted-foreground cursor-pointer select-none">{label}</summary>
      <pre className="bg-muted mt-1 max-h-48 overflow-auto rounded p-2 break-words whitespace-pre-wrap">
        {JSON.stringify(value, null, 2)}
      </pre>
    </details>
  );
}

function Row({
  step,
  icon,
  tone,
  children,
}: {
  step: AgentStep;
  icon: React.ReactNode;
  tone?: 'ok' | 'bad';
  children: React.ReactNode;
}) {
  return (
    <li
      data-seq={step.seq}
      data-kind={step.kind}
      className={cn(
        'flex gap-2 rounded-md border px-2 py-1.5 text-sm',
        tone === 'bad' && 'border-destructive/50',
      )}
    >
      <span className="mt-0.5 shrink-0" aria-hidden>
        {icon}
      </span>
      <div className="min-w-0 flex-1">{children}</div>
      {step.latencyMs > 0 && (
        <span className="text-muted-foreground shrink-0 text-xs tabular-nums">
          {step.latencyMs} ms
        </span>
      )}
    </li>
  );
}

function PlanStep({ step }: { step: AgentStep }) {
  const plan = planOf(step.payload);
  return (
    <Row step={step} icon={<ListChecks className="size-4" />}>
      <p className="font-medium">
        Plan{plan.title ? `: ${plan.title}` : ''}
        {plan.decision && plan.decision !== 'proceed' && (
          <span className="text-muted-foreground font-normal"> ({plan.decision})</span>
        )}
      </p>
      {plan.message && <p className="text-muted-foreground text-xs">{plan.message}</p>}
      {plan.steps.length > 0 && (
        <ul aria-label="Plan checklist" className="mt-1 grid gap-0.5">
          {plan.steps.map((s, i) => (
            <li key={i} className="flex gap-1.5">
              <Square className="text-muted-foreground mt-0.5 size-3.5 shrink-0" aria-hidden />
              <span>{s}</span>
            </li>
          ))}
        </ul>
      )}
      {plan.affectedScope && (
        <p className="mt-1 text-xs">
          <span className="font-semibold">Scope:</span> {plan.affectedScope}
        </p>
      )}
      {plan.risks.length > 0 && (
        <div className="mt-1 text-xs">
          <span className="font-semibold">Risks:</span>
          <ul className="list-disc pl-4">
            {plan.risks.map((r, i) => (
              <li key={i}>{r}</li>
            ))}
          </ul>
        </div>
      )}
    </Row>
  );
}

function ToolCallStep({ step }: { step: AgentStep }) {
  const call = toolCallOf(step.payload);
  const preview = previewArgs(call.args);
  return (
    <Row step={step} icon={<Wrench className="size-4" />}>
      <p className="truncate">
        <span className="font-mono text-[13px] font-medium">{call.name}</span>
        {preview && (
          <span className="text-muted-foreground ml-1.5 font-mono text-xs">{preview}</span>
        )}
      </p>
      <Json value={call.args} label="Arguments" />
    </Row>
  );
}

function ToolResultStep({ step }: { step: AgentStep }) {
  const res = toolResultOf(step.payload);
  return (
    <Row
      step={step}
      tone={res.ok ? 'ok' : 'bad'}
      icon={
        res.ok ? (
          <CheckCircle2 className="text-success size-4" />
        ) : (
          <AlertCircle className="text-destructive size-4" />
        )
      }
    >
      <p>
        <span className="font-mono text-[13px]">{res.name}</span>{' '}
        <span className={res.ok ? 'text-success' : 'text-destructive'}>
          {res.ok ? 'returned' : 'failed'}
        </span>
        {!res.ok && res.errorMessage && (
          <span className="text-muted-foreground">: {res.errorMessage}</span>
        )}
      </p>
      <Json value={res.response} label="Result" />
    </Row>
  );
}

function VerifyStep({ step }: { step: AgentStep }) {
  const v = verifyOf(step.payload);
  return (
    <Row
      step={step}
      tone={v.valid ? 'ok' : 'bad'}
      icon={<ShieldCheck className={cn('size-4', v.valid ? 'text-success' : 'text-warning')} />}
    >
      <p className="font-medium">
        Verify (round {v.round + 1}):{' '}
        {v.valid
          ? 'the draft passes every rule'
          : `${v.errorCount} rule error${v.errorCount === 1 ? '' : 's'}`}
      </p>
      {v.requiresSecondApprover && (
        <p className="text-xs">Touches SIL/ESD tags: a second approver will be required.</p>
      )}
      {v.violations.length > 0 && (
        <div className="mt-1">
          <ViolationsList violations={v.violations} title="Rule check" />
        </div>
      )}
    </Row>
  );
}

function FinalStep({ step }: { step: AgentStep }) {
  const f = finalOf(step.payload);
  return (
    <Row step={step} icon={<CheckCircle2 className="size-4" />}>
      <p className="font-medium">
        Finished
        {f.kind ? `: ${f.kind.replaceAll('_', ' ')}` : ''}
        {f.changeSetId !== null && ` (change set #${f.changeSetId}, ${f.opCount} operations)`}
      </p>
      {f.message && <p className="text-muted-foreground text-xs">{f.message}</p>}
    </Row>
  );
}

function ErrorStep({ step }: { step: AgentStep }) {
  const e = errorOf(step.payload);
  const known = describeErrorCode(e.code, e.message);
  // The run outcome box below the timeline already carries the full explanation and the raw provider detail;
  // repeating them here showed every failure twice. The timeline keeps just the one-line record that it happened.
  return (
    <Row step={step} tone="bad" icon={<AlertCircle className="text-destructive size-4" />}>
      <p className="font-medium">{known.title}</p>
    </Row>
  );
}

function StepRow({ step }: { step: AgentStep }) {
  switch (step.kind) {
    case 'plan':
      return <PlanStep step={step} />;
    case 'tool_call':
      return <ToolCallStep step={step} />;
    case 'tool_result':
      return <ToolResultStep step={step} />;
    case 'verify':
      return <VerifyStep step={step} />;
    case 'final':
      return <FinalStep step={step} />;
    case 'error':
      return <ErrorStep step={step} />;
    default:
      return (
        <Row step={step} icon={<Square className="size-4" />}>
          <p className="text-muted-foreground text-xs">Unrecognised step ({String(step.kind)})</p>
          <Json value={step.payload} label="Payload" />
        </Row>
      );
  }
}

export function StepList({ steps }: { steps: readonly AgentStep[] }) {
  if (steps.length === 0) return null;
  return (
    <ol aria-label="Run steps" className="grid gap-1.5" role="log" aria-live="polite">
      {steps.map((s) => (
        <StepRow key={s.seq} step={s} />
      ))}
    </ol>
  );
}
