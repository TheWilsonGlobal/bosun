import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { ClipboardList, Trash2, X } from 'lucide-react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { addOps, createChangeSet, fetchChangeSets, validateChangeSet } from '@/lib/api/endpoints';
import { describeError } from '@/lib/api/errors';
import { editsToOps, tagsTouched, type PendingEdit } from '@/lib/pendingEdits';
import { formatLimit } from './EditableCells';
import { useAuth, useReportError } from '@/features/auth/session';
import { useProject } from '@/features/project/ProjectContext';
import { usePending } from './PendingContext';

const schema = z.object({
  title: z.string().trim().min(1, 'Give the change set a title').max(200, 'At most 200 characters'),
  target: z.string(),
});
type Values = z.infer<typeof schema>;

export function describeEdit(e: PendingEdit): { what: string; from: string; to: string } {
  if (e.kind === 'field') {
    return { what: e.field, from: e.old ?? '(empty)', to: e.new ?? '(empty)' };
  }
  return {
    what: `${e.level} limit`,
    from: e.old ? formatLimit(e.old) : '(none)',
    to: e.new ? formatLimit(e.new) : '(removed)',
  };
}

export function PendingBar() {
  const { list, discard, clear, setHighlight } = usePending();
  const [open, setOpen] = useState(false);
  if (list.length === 0) return null;
  const tags = tagsTouched(list).length;

  return (
    <>
      <div
        role="region"
        aria-label="Pending edits"
        className="bg-warning/20 border-warning flex flex-wrap items-center gap-3 rounded-md border px-3 py-2 text-sm"
      >
        <ClipboardList className="size-4" aria-hidden />
        <span>
          <strong>{list.length}</strong> pending {list.length === 1 ? 'edit' : 'edits'} on{' '}
          <strong>{tags}</strong> {tags === 1 ? 'tag' : 'tags'}. Nothing is saved until a change set
          is approved and applied.
        </span>
        <div className="ml-auto flex gap-2">
          <Button size="sm" variant="outline" onClick={clear}>
            <Trash2 aria-hidden /> Discard
          </Button>
          <Button size="sm" onClick={() => setOpen(true)}>
            Review &amp; create change set
          </Button>
        </div>
      </div>
      <ReviewDialog
        open={open}
        onOpenChange={setOpen}
        edits={list}
        onRemove={discard}
        onDone={(id, violations) => {
          clear();
          setHighlight(id, violations);
          setOpen(false);
        }}
      />
    </>
  );
}

function ReviewDialog({
  open,
  onOpenChange,
  edits,
  onRemove,
  onDone,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  edits: PendingEdit[];
  onRemove: (key: string) => void;
  onDone: (
    changeSetId: number,
    violations: Parameters<ReturnType<typeof usePending>['setHighlight']>[1],
  ) => void;
}) {
  const { project } = useProject();
  const { session } = useAuth();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const reportError = useReportError();
  const projectId = project?.id ?? 0;

  const drafts = useQuery({
    queryKey: ['change-sets', projectId, 'draft'],
    queryFn: () => fetchChangeSets(projectId, { status: 'draft', pageSize: 50 }),
    enabled: open && projectId > 0,
  });

  const form = useForm<Values>({
    resolver: zodResolver(schema),
    values: {
      title: `Manual edit: ${edits.length} ${edits.length === 1 ? 'change' : 'changes'} on ${tagsTouched(edits).length} ${tagsTouched(edits).length === 1 ? 'tag' : 'tags'}`,
      target: 'new',
    },
    resetOptions: { keepDirtyValues: true },
  });
  // eslint-disable-next-line react-hooks/incompatible-library -- third-party API, memoization skipped on purpose
  const target = form.watch('target');

  const submit = useMutation({
    mutationFn: async (v: Values) => {
      const ops = editsToOps(edits);
      const cs =
        v.target === 'new'
          ? await createChangeSet(projectId, v.title, ops)
          : await addOps(Number(v.target), ops);
      // Validate right away so the grid can highlight offending rows; a validation failure here is not fatal.
      const validation = await validateChangeSet(cs.id).catch(() => null);
      return { cs, validation };
    },
    onSuccess: ({ cs, validation }) => {
      void qc.invalidateQueries({ queryKey: ['change-sets'] });
      void qc.invalidateQueries({ queryKey: ['tags'] });
      onDone(cs.id, validation?.violations ?? []);
      toast.success(`Change set #${cs.id} created with ${cs.ops.length} operations`, {
        description: validation
          ? validation.valid
            ? 'Validation passed.'
            : 'Validation found errors - review them before approving.'
          : undefined,
      });
      navigate(`/change-sets/${cs.id}`);
    },
    onError: (e) => reportError(e),
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90dvh] overflow-hidden sm:max-w-2xl">
        <form
          onSubmit={form.handleSubmit((v) => submit.mutate(v))}
          className="grid min-h-0 gap-4"
          noValidate
        >
          <DialogHeader>
            <DialogTitle>Review pending edits</DialogTitle>
            <DialogDescription>
              These become operations of a change set. The old value shown is what you saw when you
              edited; if the tag changes meanwhile, applying fails with a conflict instead of
              overwriting.
            </DialogDescription>
          </DialogHeader>
          <div className="max-h-64 overflow-auto rounded border">
            <table className="w-full text-sm">
              <thead className="bg-muted sticky top-0 text-left text-xs">
                <tr>
                  <th className="px-2 py-1">Tag</th>
                  <th className="px-2 py-1">Field</th>
                  <th className="px-2 py-1">Old</th>
                  <th className="px-2 py-1">New</th>
                  <th className="px-2 py-1">
                    <span className="sr-only">Remove</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {edits.map((e) => {
                  const d = describeEdit(e);
                  return (
                    <tr key={e.key} className="border-t">
                      <td className="px-2 py-1 font-mono text-[13px]">{e.tagName}</td>
                      <td className="px-2 py-1">{d.what}</td>
                      <td className="text-muted-foreground px-2 py-1">{d.from}</td>
                      <td className="px-2 py-1 font-medium">{d.to}</td>
                      <td className="px-1 py-1">
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="size-6"
                          aria-label={`Remove edit of ${d.what} on ${e.tagName}`}
                          onClick={() => onRemove(e.key)}
                        >
                          <X className="size-3.5" aria-hidden />
                        </Button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="cs-target">Add to</Label>
            <select
              id="cs-target"
              className="border-input bg-background h-9 rounded-md border px-2 text-sm"
              {...form.register('target')}
            >
              <option value="new">A new change set</option>
              {(drafts.data?.items ?? []).map((d) => (
                <option key={d.id} value={d.id}>
                  Append to draft #{d.id}: {d.title}
                </option>
              ))}
            </select>
          </div>
          {target === 'new' && (
            <div className="grid gap-1.5">
              <Label htmlFor="cs-title">Title</Label>
              <Input
                id="cs-title"
                aria-invalid={!!form.formState.errors.title}
                {...form.register('title')}
              />
              {form.formState.errors.title && (
                <p className="text-destructive text-xs" role="alert">
                  {form.formState.errors.title.message}
                </p>
              )}
            </div>
          )}
          {!session && (
            <p className="text-sm" role="status">
              You are not signed in. Creating the change set will ask you to sign in; your edits are
              kept.
            </p>
          )}
          {submit.error && (
            <p className="text-destructive text-sm" role="alert">
              {describeError(submit.error).message}
            </p>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Keep editing
            </Button>
            <Button type="submit" disabled={submit.isPending || edits.length === 0}>
              {submit.isPending ? 'Creating...' : 'Create & validate'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
