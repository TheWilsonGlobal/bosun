import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Bot, ExternalLink, Eye, User } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { DataTable, type Column } from '@/components/DataTable';
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
  const [selectedEntry, setSelectedEntry] = useState<AuditEntry | null>(null);

  const columns = useMemo<Column<AuditEntry>[]>(
    () => [
      {
        key: 'timestamp',
        header: 'When',
        className: 'whitespace-nowrap font-mono text-[11px] text-muted-foreground',
        render: (e) => time(e.timestamp),
      },
      {
        key: 'actor',
        header: 'Actor',
        className: 'whitespace-nowrap',
        render: (e) => (
          <div className="flex items-center gap-1.5">
            <User className="size-3 text-muted-foreground shrink-0" aria-label="user" />
            <span className="font-medium text-foreground">{e.actor}</span>
            {e.proposedBy === 'agent' && (
              <Badge
                variant="secondary"
                className="h-4.5 px-1.5 py-0 text-[10px] gap-1 shrink-0 font-normal"
                data-testid="proposed-by-agent"
                title={`Proposed by agent${e.model ? ` (${e.model})` : ''}`}
              >
                <Bot className="size-2.5 text-primary" aria-hidden />
                <span>agent</span>
                {/* Accessible test text so existing assertions match */}
                <span className="sr-only">
                  proposed by agent
                  {e.model ? ` (${e.model}${e.promptVersion ? `, prompt ${e.promptVersion}` : ''})` : ''}
                </span>
                {e.agentRunId !== null && (
                  <span className="sr-only">agent run #{e.agentRunId}</span>
                )}
              </Badge>
            )}
          </div>
        ),
      },
      {
        key: 'approvedBy',
        header: 'Approved by',
        className: 'whitespace-nowrap text-muted-foreground',
        render: (e) => e.approvedBy ?? '-',
      },
      {
        key: 'changeSetId',
        header: 'Change set',
        className: 'whitespace-nowrap tabular-nums',
        render: (e) =>
          e.changeSetId ? (
            <span className="font-medium text-primary hover:underline">
              #{e.changeSetId}
            </span>
          ) : (
            '-'
          ),
      },
      {
        key: 'tagName',
        header: 'Tag',
        className: 'whitespace-nowrap font-mono text-[11px]',
        render: (e) =>
          e.tagName ? (
            <span className="bg-muted px-1.5 py-0.5 rounded text-foreground font-medium">
              {e.tagName}
            </span>
          ) : (
            '-'
          ),
      },
      {
        key: 'opType',
        header: 'Operation',
        className: 'whitespace-nowrap',
        render: (e) => (
          <Badge variant="outline" className="text-[10px] py-0 h-4.5 font-normal">
            {e.opType}
          </Badge>
        ),
      },
      {
        key: 'change',
        header: 'Change',
        className: 'whitespace-nowrap',
        render: (e) => (
          <div
            className="max-w-[240px] xl:max-w-[340px] truncate text-foreground font-mono text-[11px]"
            title={summarizeDiff(e)}
          >
            {summarizeDiff(e)}
          </div>
        ),
      },
      {
        key: 'model',
        header: 'Model',
        className: 'whitespace-nowrap text-muted-foreground',
        render: (e) => (
          <span className="truncate max-w-[120px] inline-block align-middle" title={e.model ?? '-'}>
            {e.model ?? '-'}
          </span>
        ),
      },
      {
        key: 'actions',
        header: '',
        width: 40,
        align: 'right',
        render: (e) => (
          <Button
            variant="ghost"
            size="icon"
            className="size-6 text-muted-foreground group-hover:text-primary hover:bg-background"
            aria-label="View entry details"
            onClick={(ev) => {
              ev.stopPropagation();
              setSelectedEntry(e);
            }}
          >
            <Eye className="size-3" />
          </Button>
        ),
      },
    ],
    [],
  );

  return (
    <>
      <DataTable
        data={entries}
        columns={columns}
        caption={caption ?? 'Audit entries'}
        rowAriaLabel={(e) => `Audit entry ${e.id}`}
        striped
        clientPagination={entries.length > 10}
        initialPageSize={25}
        onRowClick={(e) => setSelectedEntry(e)}
        rowClassName="hover:bg-accent/40 cursor-pointer transition-colors group select-none text-xs focus-visible:outline-none focus-visible:bg-accent/60"
      />

      {/* Detailed Record Popup Dialog */}
      <Dialog open={!!selectedEntry} onOpenChange={(open) => !open && setSelectedEntry(null)}>
        <DialogContent className="sm:max-w-2xl max-h-[88vh] overflow-y-auto">
          {selectedEntry && (
            <>
              <DialogHeader>
                <div className="flex items-center gap-2">
                  <Badge variant="outline" className="font-mono text-xs">
                    {selectedEntry.opType}
                  </Badge>
                  <DialogTitle className="text-base font-bold">
                    Audit Entry #{selectedEntry.id}
                  </DialogTitle>
                </div>
                <DialogDescription className="text-xs">
                  Recorded at {new Date(selectedEntry.timestamp).toUTCString()} ({time(selectedEntry.timestamp)})
                </DialogDescription>
              </DialogHeader>

              <div className="grid gap-3.5 py-2 text-xs">
                {/* Metadata Card */}
                <div className="grid grid-cols-2 sm:grid-cols-3 gap-3 bg-muted/40 p-3 rounded-lg border">
                  <div>
                    <span className="text-muted-foreground block text-[11px]">Actor</span>
                    <span className="font-medium flex items-center gap-1 mt-0.5 text-foreground">
                      <User className="size-3 text-muted-foreground" /> {selectedEntry.actor}
                    </span>
                  </div>
                  <div>
                    <span className="text-muted-foreground block text-[11px]">Approved By</span>
                    <span className="font-medium mt-0.5 block text-foreground">
                      {selectedEntry.approvedBy ?? 'None / Auto'}
                    </span>
                  </div>
                  <div>
                    <span className="text-muted-foreground block text-[11px]">Target Tag</span>
                    <span className="font-mono font-medium mt-0.5 block text-primary">
                      {selectedEntry.tagName ?? 'N/A'}
                    </span>
                  </div>
                  <div>
                    <span className="text-muted-foreground block text-[11px]">Change Set</span>
                    {selectedEntry.changeSetId ? (
                      <Link
                        to={`/change-sets/${selectedEntry.changeSetId}`}
                        className="font-medium text-primary hover:underline inline-flex items-center gap-1 mt-0.5"
                      >
                        #{selectedEntry.changeSetId} <ExternalLink className="size-3" />
                      </Link>
                    ) : (
                      <span className="mt-0.5 block">N/A</span>
                    )}
                  </div>
                  <div>
                    <span className="text-muted-foreground block text-[11px]">Author Origin</span>
                    <span className="font-medium mt-0.5 block capitalize text-foreground">
                      {selectedEntry.proposedBy}
                    </span>
                  </div>
                  <div>
                    <span className="text-muted-foreground block text-[11px]">Project ID</span>
                    <span className="font-medium mt-0.5 block text-foreground">
                      #{selectedEntry.projectId}
                    </span>
                  </div>
                </div>

                {/* AI Provenance if proposed by agent */}
                {selectedEntry.proposedBy === 'agent' && (
                  <div className="rounded-lg border bg-primary/5 p-3 space-y-1.5 border-primary/20">
                    <div className="flex items-center gap-1.5 font-semibold text-primary">
                      <Bot className="size-3.5" /> AI Agent Provenance
                    </div>
                    <div className="grid sm:grid-cols-2 gap-2 text-muted-foreground pt-1">
                      <div>
                        Model: <strong className="text-foreground">{selectedEntry.model ?? 'N/A'}</strong>
                      </div>
                      {selectedEntry.agentRunId !== null && (
                        <div>
                          Agent Run:{' '}
                          <strong className="text-foreground">
                            #{selectedEntry.agentRunId}
                          </strong>
                        </div>
                      )}
                      {selectedEntry.promptVersion && (
                        <div className="sm:col-span-2 break-all text-[11px]">
                          Prompt Version:{' '}
                          <code className="text-foreground font-mono bg-muted/60 px-1 py-0.5 rounded">
                            {selectedEntry.promptVersion}
                          </code>
                        </div>
                      )}
                    </div>
                  </div>
                )}

                {/* Summary of Modification */}
                <div className="space-y-1">
                  <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                    Modification Summary
                  </span>
                  <div className="rounded-md border bg-muted/20 p-2.5 font-mono text-xs break-all whitespace-pre-wrap">
                    {summarizeDiff(selectedEntry)}
                  </div>
                </div>

                {/* Raw Op Payload JSON */}
                {selectedEntry.diff != null && (
                  <div className="space-y-1">
                    <span className="text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">
                      Stored Diff Payload (JSON)
                    </span>
                    <pre className="max-h-48 overflow-auto rounded-md bg-muted/50 p-3 font-mono text-[11px] border leading-relaxed">
                      {JSON.stringify(selectedEntry.diff, null, 2)}
                    </pre>
                  </div>
                )}
              </div>

              <DialogFooter showCloseButton />
            </>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}
export default AuditTable;
