import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { ArrowLeft, GitMerge } from 'lucide-react';
import { Status } from '@/components/Status';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { useAuth, useReportError } from '@/features/auth/session';
import { AuditTable } from '@/features/audit/AuditTable';
import { usePending } from '@/features/tags/PendingContext';
import { ApiError } from '@/lib/api/client';
import {
  applyChangeSet,
  approveChangeSet,
  fetchAudit,
  fetchChangeSet,
  fetchDiff,
  rejectChangeSet,
  validateChangeSet,
} from '@/lib/api/endpoints';
import { conflictsOf, describeError, violationsOf } from '@/lib/api/errors';
import type { ChangeSet, Violation } from '@/lib/api/types';
import { availableActions, type ActionName } from '@/lib/changeSetActions';
import { DiffTable } from './DiffTable';
import { ActionBar, SecondApproverBanner, StatusBadge, ViolationsList } from './parts';

const rejectSchema = z.object({ reason: z.string().trim().max(500, 'At most 500 characters') });

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '');

interface ActionFailure {
  title: string;
  message: string;
  conflicts: Violation[];
  violations: Violation[];
}

export function ChangeSetDetailPage() {
  const id = Number(useParams().id);
  const qc = useQueryClient();
  const { session } = useAuth();
  const reportError = useReportError();
  const { setHighlight } = usePending();
  const [failure, setFailure] = useState<ActionFailure | null>(null);
  const [rejecting, setRejecting] = useState(false);
  const [running, setRunning] = useState<ActionName | null>(null);

  const csQuery = useQuery({
    queryKey: ['change-set', id],
    queryFn: () => fetchChangeSet(id),
    enabled: Number.isInteger(id),
  });
  const diffQuery = useQuery({
    queryKey: ['diff', id],
    queryFn: () => fetchDiff(id),
    enabled: Number.isInteger(id),
  });
  const cs = csQuery.data;
  const applied = cs?.status === 'applied';
  const auditQuery = useQuery({
    queryKey: ['audit', cs?.projectId, id],
    queryFn: () => fetchAudit(cs!.projectId, { changeSetId: id, take: 500 }),
    enabled: !!cs && applied,
  });

  const refresh = (next?: ChangeSet) => {
    if (next) qc.setQueryData(['change-set', id], next);
    void qc.invalidateQueries({ queryKey: ['change-set', id] });
    void qc.invalidateQueries({ queryKey: ['diff', id] });
    void qc.invalidateQueries({ queryKey: ['change-sets'] });
  };

  const run = useMutation({
    mutationFn: async (action: ActionName | { reject: string }) => {
      if (typeof action === 'object')
        return { kind: 'reject' as const, cs: await rejectChangeSet(id, action.reject) };
      switch (action) {
        case 'validate':
          return { kind: 'validate' as const, result: await validateChangeSet(id) };
        case 'approve':
          return { kind: 'approve' as const, cs: await approveChangeSet(id) };
        case 'apply':
          return { kind: 'apply' as const, cs: await applyChangeSet(id) };
        default:
          throw new Error(`Unsupported action ${action}`);
      }
    },
    onMutate: (a) => {
      setFailure(null);
      setRunning(typeof a === 'object' ? 'reject' : a);
    },
    onSettled: () => setRunning(null),
    onSuccess: (r) => {
      if (r.kind === 'validate') {
        setHighlight(id, r.result.violations);
        refresh();
        toast[r.result.valid ? 'success' : 'warning'](
          r.result.valid ? 'Validation passed' : 'Validation found errors',
          { description: `${r.result.violations.length} finding(s).` },
        );
        return;
      }
      refresh(r.cs);
      if (r.kind === 'apply') {
        void qc.invalidateQueries({ queryKey: ['tags'] });
        void qc.invalidateQueries({ queryKey: ['audit'] });
        void qc.invalidateQueries({ queryKey: ['projects'] });
        toast.success(`Change set #${id} applied`);
      } else if (r.kind === 'approve') {
        toast.success(r.cs.pendingSecondApproval ? 'First approval recorded' : 'Approved');
      } else {
        toast.success('Change set rejected');
      }
    },
    onError: (e) => {
      const m = describeError(e);
      setFailure({
        title: m.title,
        message: m.message,
        conflicts: conflictsOf(e),
        violations: violationsOf(e),
      });
      if (e instanceof ApiError && (e.code === 'unauthorized' || e.code === 'backend_offline')) {
        reportError(e);
      }
      // State may have moved on (invalid_state, validation reset approvals, ...): re-read it.
      refresh();
    },
  });

  const rejectForm = useForm<z.infer<typeof rejectSchema>>({
    resolver: zodResolver(rejectSchema),
    defaultValues: { reason: '' },
  });

  if (!Number.isInteger(id))
    return (
      <Status tone="error" title="Not found">
        Invalid change set id.
      </Status>
    );
  if (csQuery.isPending || diffQuery.isPending) return <Status>Loading change set...</Status>;
  if (csQuery.isError || !cs) {
    const m = describeError(csQuery.error);
    return (
      <Status tone="error" title={m.title}>
        {m.message}
      </Status>
    );
  }
  const diff = diffQuery.data;
  const violations = diff?.violations ?? [];
  const actions = availableActions(cs, session, violations);

  const onAction = (a: ActionName) => {
    if (a === 'reject') setRejecting(true);
    else run.mutate(a);
  };

  return (
    <div className="flex h-full flex-col gap-4 overflow-auto p-4">
      <div>
        <Link
          to="/change-sets"
          className="text-muted-foreground inline-flex items-center gap-1 text-sm hover:underline"
        >
          <ArrowLeft className="size-3.5" aria-hidden /> Change sets
        </Link>
        <div className="mt-1 flex flex-wrap items-center gap-3">
          <GitMerge className="size-5" aria-hidden />
          <h1 className="text-lg font-semibold">
            #{cs.id} {cs.title}
          </h1>
          <StatusBadge status={cs.status} />
          <span className="text-muted-foreground text-sm">
            {cs.source} - created by {cs.createdBy} {when(cs.createdAt)}
          </span>
        </div>
        {(cs.approvedBy || cs.appliedBy || cs.rejectedBy) && (
          <p className="text-muted-foreground mt-1 text-sm">
            {cs.approvedBy && (
              <>
                Approved by {cs.approvedBy} {when(cs.approvedAt)}.{' '}
              </>
            )}
            {cs.secondApprovedBy && (
              <>
                Second approval by {cs.secondApprovedBy} {when(cs.secondApprovedAt)}.{' '}
              </>
            )}
            {cs.appliedBy && (
              <>
                Applied by {cs.appliedBy} {when(cs.appliedAt)}.{' '}
              </>
            )}
            {cs.rejectedBy && (
              <>
                Rejected by {cs.rejectedBy} {when(cs.rejectedAt)}
                {cs.rejectReason ? `: ${cs.rejectReason}` : ''}.
              </>
            )}
          </p>
        )}
      </div>

      <SecondApproverBanner cs={cs} />

      {failure && (
        <div
          role="alert"
          className="border-destructive bg-destructive/10 rounded-md border px-3 py-2 text-sm"
        >
          <p className="font-semibold">{failure.title}</p>
          <p>{failure.message}</p>
          {failure.conflicts.length > 0 && (
            <div className="mt-2" data-testid="conflicts">
              <p className="font-medium">
                Changed since you drafted this (
                {new Set(failure.conflicts.map((c) => c.tagName)).size} tags):
              </p>
              <ul className="list-disc pl-5">
                {failure.conflicts.map((c, i) => (
                  <li key={i}>
                    <Link
                      className="font-mono underline"
                      to={`/tags?filter=${encodeURIComponent(c.tagName ?? '')}`}
                    >
                      {c.tagName}
                    </Link>
                    {c.field ? ` (${c.field})` : ''}: {c.message}
                  </li>
                ))}
              </ul>
              <p className="mt-1">
                Reject this change set and redo the edits from the current values.
              </p>
            </div>
          )}
          {failure.violations.length > 0 && failure.conflicts.length === 0 && (
            <div className="mt-2">
              <ViolationsList violations={failure.violations} title="Blocking findings" />
            </div>
          )}
        </div>
      )}

      <ActionBar actions={actions} pending={running} onAction={onAction} />

      <section aria-label="Operations" className="grid gap-2">
        <h2 className="text-sm font-semibold">Operations ({diff?.rows.length ?? cs.ops.length})</h2>
        {diffQuery.isError ? (
          <p role="alert" className="text-destructive text-sm">
            {describeError(diffQuery.error).message}
          </p>
        ) : (
          <DiffTable rows={diff?.rows ?? []} violations={violations} />
        )}
      </section>

      {cs.status !== 'applied' && cs.status !== 'rejected' && (
        <ViolationsList violations={violations} />
      )}

      {applied && (
        <section aria-label="Resulting audit entries" className="grid gap-2">
          <h2 className="text-sm font-semibold">Audit entries written by this change set</h2>
          {auditQuery.isPending ? (
            <p className="text-muted-foreground text-sm">Loading audit entries...</p>
          ) : auditQuery.isError ? (
            <p role="alert" className="text-destructive text-sm">
              {describeError(auditQuery.error).message}
            </p>
          ) : auditQuery.data.length === 0 ? (
            <p className="text-muted-foreground text-sm">No audit entries found.</p>
          ) : (
            <AuditTable entries={auditQuery.data} caption="Audit entries of this change set" />
          )}
        </section>
      )}

      <Dialog open={rejecting} onOpenChange={setRejecting}>
        <DialogContent>
          <form
            noValidate
            className="grid gap-4"
            onSubmit={rejectForm.handleSubmit((v) => {
              setRejecting(false);
              rejectForm.reset();
              run.mutate({ reject: v.reason });
            })}
          >
            <DialogHeader>
              <DialogTitle>Reject change set #{cs.id}</DialogTitle>
              <DialogDescription>
                A rejected change set can no longer be approved or applied.
              </DialogDescription>
            </DialogHeader>
            <div className="grid gap-1.5">
              <Label htmlFor="reject-reason">Reason (optional)</Label>
              <Textarea id="reject-reason" rows={3} {...rejectForm.register('reason')} />
              {rejectForm.formState.errors.reason && (
                <p role="alert" className="text-destructive text-xs">
                  {rejectForm.formState.errors.reason.message}
                </p>
              )}
            </div>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setRejecting(false)}>
                Cancel
              </Button>
              <Button type="submit" variant="destructive">
                Reject
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </div>
  );
}
