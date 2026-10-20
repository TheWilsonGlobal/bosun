import { AlertCircle, AlertTriangle, Info, ShieldAlert, ShieldCheck } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import type { ActionMap, ActionName } from '@/lib/changeSetActions';
import type { ChangeSet, ChangeSetStatus, Severity, Violation } from '@/lib/api/types';
import { cn } from '@/lib/utils';

const STATUS_STYLE: Record<ChangeSetStatus, string> = {
  draft: 'bg-secondary text-secondary-foreground',
  validated: 'bg-primary/15 text-primary',
  approved: 'bg-success/20 text-success',
  applied: 'bg-success text-white',
  rejected: 'bg-destructive/15 text-destructive',
};

export function StatusBadge({ status }: { status: ChangeSetStatus }) {
  return (
    <Badge variant="outline" className={cn('border-transparent capitalize', STATUS_STYLE[status])}>
      {status}
    </Badge>
  );
}

const SEVERITY_ICON: Record<Severity, typeof Info> = {
  error: AlertCircle,
  warning: AlertTriangle,
  info: Info,
};
const SEVERITY_CLASS: Record<Severity, string> = {
  error: 'text-destructive',
  warning: 'text-warning',
  info: 'text-muted-foreground',
};

export function ViolationsList({
  violations,
  title = 'Validation',
}: {
  violations: readonly Violation[];
  title?: string;
}) {
  if (violations.length === 0) {
    return (
      <p className="text-muted-foreground flex items-center gap-2 text-sm">
        <ShieldCheck className="text-success size-4" aria-hidden /> No rule violations.
      </p>
    );
  }
  const order: Severity[] = ['error', 'warning', 'info'];
  const sorted = [...violations].sort(
    (a, b) => order.indexOf(a.severity) - order.indexOf(b.severity),
  );
  const counts = order.map((s) => [s, violations.filter((v) => v.severity === s).length] as const);
  return (
    <section aria-label={title}>
      <h3 className="mb-1 text-sm font-semibold">
        {title}:{' '}
        {counts
          .filter(([, n]) => n > 0)
          .map(([s, n]) => `${n} ${s}${n === 1 ? '' : 's'}`)
          .join(', ')}
      </h3>
      <ul className="divide-y rounded-md border">
        {sorted.map((v, i) => {
          const Icon = SEVERITY_ICON[v.severity];
          return (
            <li
              key={`${v.code}-${v.tagName}-${v.field}-${i}`}
              className="flex gap-2 px-3 py-2 text-sm"
            >
              <Icon
                className={cn('mt-0.5 size-4 shrink-0', SEVERITY_CLASS[v.severity])}
                aria-hidden
              />
              <div>
                <p>
                  <span className="sr-only">{v.severity}: </span>
                  {v.message}
                </p>
                <p className="text-muted-foreground text-xs">
                  <code>{v.code}</code>
                  {v.tagName && <> - {v.tagName}</>}
                  {v.field && <> - {v.field}</>}
                </p>
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}

/** SIL/ESD change sets need two different approvers; this explains where the change set stands. */
export function SecondApproverBanner({ cs }: { cs: ChangeSet }) {
  if (!cs.requiresSecondApprover) return null;
  let text: string;
  if (cs.status === 'applied' || cs.status === 'approved') {
    text = `Approved by ${cs.approvedBy ?? 'unknown'} and ${cs.secondApprovedBy ?? 'unknown'} (two approvers).`;
  } else if (cs.status === 'rejected') {
    text = 'This change set touched SIL/ESD tags and was rejected.';
  } else if (cs.pendingSecondApproval) {
    text = `First approval given by ${cs.approvedBy}. Waiting for a second approval from a different user with the approver role.`;
  } else {
    text =
      'This change set touches SIL/ESD tags: it needs two approvals, the second from a different user with the approver role.';
  }
  const waiting = cs.pendingSecondApproval;
  return (
    <div
      role="status"
      data-testid="second-approver-banner"
      className={cn(
        'flex items-start gap-3 rounded-md border px-3 py-2 text-sm',
        waiting ? 'border-warning bg-warning/20' : 'border-primary/40 bg-primary/10',
      )}
    >
      <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
      <div>
        <p className="font-semibold">Requires second approver (SIL/ESD)</p>
        <p>{text}</p>
      </div>
    </div>
  );
}

const ORDER: ActionName[] = ['validate', 'approve', 'apply', 'reject'];

export function ActionBar({
  actions,
  pending,
  onAction,
}: {
  actions: ActionMap;
  pending: ActionName | null;
  onAction: (a: ActionName) => void;
}) {
  const reasons = ORDER.filter((a) => !actions[a].enabled && actions[a].reason);
  const allSame =
    reasons.length > 1 && reasons.every((r) => actions[r].reason === actions[reasons[0]].reason);
  return (
    <div>
      <div role="toolbar" aria-label="Change set actions" className="flex flex-wrap gap-2">
        {ORDER.map((name) => {
          const a = actions[name];
          return (
            <Button
              key={name}
              variant={
                name === 'reject' ? 'outline' : name === 'validate' ? 'secondary' : 'default'
              }
              className={name === 'reject' ? 'text-destructive' : undefined}
              disabled={!a.enabled || pending !== null}
              title={a.reason}
              aria-describedby={a.reason && !allSame ? `why-${name}` : undefined}
              onClick={() => onAction(name)}
            >
              {pending === name ? `${a.label}...` : a.label}
            </Button>
          );
        })}
      </div>
      <ul className="text-muted-foreground mt-2 grid gap-0.5 text-xs">
        {allSame ? (
          <li>{actions[reasons[0]].reason}</li>
        ) : (
          reasons.map((name) => (
            <li key={name} id={`why-${name}`}>
              {actions[name].label}: {actions[name].reason}
            </li>
          ))
        )}
      </ul>
    </div>
  );
}
