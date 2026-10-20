import { Bot, User } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import type { AuditEntry } from '@/lib/api/types';

const time = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'medium' });

/** Compact one-line description of what an audited op changed (the stored op JSON). */
export function summarizeDiff(e: AuditEntry): string {
  const d = e.diff as Record<string, unknown> | null;
  if (!d || typeof d !== 'object') return '';
  const show = (v: unknown) =>
    v === null || v === undefined
      ? '(empty)'
      : typeof v === 'object'
        ? JSON.stringify(v)
        : String(v);
  switch (e.opType) {
    case 'updateTag':
      return `${show(d.field)}: ${show(d.old)} -> ${show(d.new)}`;
    case 'setAlarmLimit':
      return `${show(d.level)} limit: ${show(d.old)} -> ${show(d.new)}`;
    case 'assignGroup':
    case 'bindChannel':
      return `${show(d.old)} -> ${show(d.new)}`;
    default:
      return e.opType;
  }
}

export function AuditTable({ entries, caption }: { entries: AuditEntry[]; caption?: string }) {
  return (
    <div className="overflow-auto rounded-md border">
      <table className="w-full text-sm" aria-label={caption ?? 'Audit entries'}>
        <thead className="bg-muted text-left text-xs">
          <tr>
            {[
              'When',
              'Actor',
              'Approved by',
              'Change set',
              'Tag',
              'Operation',
              'Change',
              'Model',
            ].map((h) => (
              <th key={h} scope="col" className="px-3 py-2 font-semibold">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {entries.map((e) => (
            <tr key={e.id} className="border-t align-top">
              <td className="px-3 py-1.5 whitespace-nowrap">{time(e.timestamp)}</td>
              <td className="px-3 py-1.5">
                <span className="flex items-center gap-1">
                  <User className="size-3.5" aria-label="user" />
                  {e.actor}
                </span>
                {e.proposedBy === 'agent' && (
                  <Badge
                    variant="secondary"
                    className="mt-1 h-auto gap-1 whitespace-normal"
                    data-testid="proposed-by-agent"
                  >
                    <Bot className="size-3" aria-hidden />
                    proposed by agent
                    {e.model
                      ? ` (${e.model}${e.promptVersion ? `, prompt ${e.promptVersion}` : ''})`
                      : ''}
                  </Badge>
                )}
                {e.agentRunId !== null && (
                  <div className="text-muted-foreground text-xs">agent run #{e.agentRunId}</div>
                )}
              </td>
              <td className="px-3 py-1.5">{e.approvedBy ?? '-'}</td>
              <td className="px-3 py-1.5 tabular-nums">
                {e.changeSetId ? `#${e.changeSetId}` : '-'}
              </td>
              <td className="px-3 py-1.5 font-mono text-[13px]">{e.tagName ?? '-'}</td>
              <td className="px-3 py-1.5">{e.opType}</td>
              <td className="px-3 py-1.5">{summarizeDiff(e)}</td>
              <td className="px-3 py-1.5">
                {e.model ? `${e.model}${e.promptVersion ? ` (${e.promptVersion})` : ''}` : '-'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
