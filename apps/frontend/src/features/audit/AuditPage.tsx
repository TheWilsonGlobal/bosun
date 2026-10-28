import { Link, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Status } from '@/components/Status';
import { Input } from '@/components/ui/input';
import { useProject } from '@/features/project/ProjectContext';
import { fetchAudit } from '@/lib/api/endpoints';
import { describeError } from '@/lib/api/errors';
import { useDebouncedDraft } from '@/lib/useDebouncedDraft';
import { AuditTable } from './AuditTable';

const TAKES = [50, 100, 250, 500];

export function AuditPage() {
  const { project } = useProject();
  const [params, setParams] = useSearchParams();
  const tagName = params.get('tagName') ?? '';
  const changeSetId = Number(params.get('changeSetId')) || undefined;
  const take = TAKES.includes(Number(params.get('take'))) ? Number(params.get('take')) : 100;

  const [text, setText] = useDebouncedDraft(tagName, (v) => {
    const next = new URLSearchParams(params);
    if (v.trim()) next.set('tagName', v.trim());
    else next.delete('tagName');
    setParams(next, { replace: true });
  });

  const q = useQuery({
    queryKey: ['audit', project?.id, changeSetId, tagName, take],
    queryFn: () => fetchAudit(project!.id, { changeSetId, tagName: tagName || undefined, take }),
    enabled: !!project,
  });

  if (!project) return <Status>Select a project.</Status>;

  return (
    <div className="flex h-full flex-col gap-3 overflow-hidden p-4">
      <div className="flex shrink-0 flex-wrap items-center gap-3">
        <h1 className="text-lg font-semibold">Audit log</h1>
        <Input
          className="ml-auto h-8 w-60"
          aria-label="Filter by exact tag name"
          placeholder="Exact tag name"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        {changeSetId && (
          <span className="text-sm">
            Change set #{changeSetId} (
            <Link className="underline" to="/audit">
              clear
            </Link>
            )
          </span>
        )}
        <label htmlFor="audit-take" className="text-muted-foreground text-sm">
          Show last
        </label>
        <select
          id="audit-take"
          className="border-input bg-background h-8 rounded-md border px-1.5 text-sm"
          value={take}
          onChange={(e) => {
            const next = new URLSearchParams(params);
            next.set('take', e.target.value);
            setParams(next);
          }}
        >
          {TAKES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>
      {q.isPending ? (
        <Status>Loading audit log...</Status>
      ) : q.isError ? (
        <Status tone="error" title={describeError(q.error).title}>
          {describeError(q.error).message}
        </Status>
      ) : q.data.length === 0 ? (
        <Status>No audit entries match. Entries are written when a change set is applied.</Status>
      ) : (
        <AuditTable className="flex-1 min-h-0" entries={q.data} />
      )}
    </div>
  );
}
