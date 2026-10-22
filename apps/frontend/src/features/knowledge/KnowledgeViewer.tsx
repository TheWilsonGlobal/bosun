import { useState, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { CheckCircle2, FileText, Search } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { cn } from '@/lib/utils';
import { fetchKnowledge, fetchKnowledgeChunks } from '@/features/assistant/ask/api';
import type { KnowledgeChunkItem, KnowledgeStatus } from '@/features/assistant/ask/askLogic';

const DOC_DESCRIPTIONS: Record<string, string> = {
  'alarm-philosophy': 'Alarm classification, priority bands, delay hysteresis, and suppression rules.',
  'cause-and-effect': 'Logic interlocks, automatic safety actions, trip delays, and ESD matrix.',
  'functional-design-spec': 'Subsystem boundaries, I/O signal engineering units, and sensor ranges.',
};

export function KnowledgeViewer({
  projectId,
  initialStatus,
  compact = false,
}: {
  projectId: number;
  initialStatus?: KnowledgeStatus;
  compact?: boolean;
}) {
  const [selectedDoc, setSelectedDoc] = useState<string>('all');
  const [searchQuery, setSearchQuery] = useState('');

  const statusQuery = useQuery({
    queryKey: ['knowledge', projectId],
    queryFn: () => fetchKnowledge(projectId),
    initialData: initialStatus,
  });

  const chunksQuery = useQuery({
    queryKey: ['knowledge-chunks', projectId],
    queryFn: () => fetchKnowledgeChunks(projectId),
  });

  const status = statusQuery.data;
  const chunks = chunksQuery.data ?? [];

  const filteredChunks = useMemo(() => {
    return chunks.filter((c) => {
      const matchesDoc = selectedDoc === 'all' || c.source === selectedDoc;
      if (!matchesDoc) return false;
      if (!searchQuery.trim()) return true;
      const q = searchQuery.toLowerCase();
      return (
        c.section.toLowerCase().includes(q) ||
        c.text.toLowerCase().includes(q) ||
        c.source.toLowerCase().includes(q)
      );
    });
  }, [chunks, selectedDoc, searchQuery]);

  return (
    <div className="grid gap-4" data-testid="knowledge-viewer">
      {/* Overview Cards for the 3 Reference Docs */}
      {status?.sources && status.sources.length > 0 && (
        <div className="grid gap-3 sm:grid-cols-3">
          {status.sources.map((s) => {
            const isSelected = selectedDoc === s.source;
            return (
              <button
                key={s.source}
                type="button"
                onClick={() => setSelectedDoc(isSelected ? 'all' : s.source)}
                className={cn(
                  'flex flex-col text-left rounded-lg border p-3.5 transition-all',
                  isSelected
                    ? 'border-primary bg-primary/5 ring-1 ring-primary'
                    : 'border-border bg-card hover:border-primary/50 hover:bg-accent/40',
                )}
              >
                <div className="flex items-center justify-between gap-1 mb-1">
                  <div className="flex items-center gap-1.5 font-medium text-sm">
                    <FileText className="size-4 text-primary shrink-0" />
                    <span className="truncate">{s.source}.md</span>
                  </div>
                  <Badge variant={isSelected ? 'default' : 'secondary'} className="text-xs">
                    {s.chunks} chunks
                  </Badge>
                </div>
                <p className="text-muted-foreground text-xs line-clamp-2 mt-1">
                  {DOC_DESCRIPTIONS[s.source] ?? 'System engineering reference document.'}
                </p>
                <div className="mt-2 text-[11px] text-muted-foreground flex items-center gap-2">
                  <span>Page {s.pages}</span>
                  <span>•</span>
                  <span className="text-success flex items-center gap-0.5">
                    <CheckCircle2 className="size-3" /> Indexed
                  </span>
                </div>
              </button>
            );
          })}
        </div>
      )}

      {/* Filter & Search Bar */}
      <div className="flex flex-wrap items-center gap-2">
        <div className="flex items-center gap-1 text-xs">
          <Button
            type="button"
            size="sm"
            variant={selectedDoc === 'all' ? 'default' : 'outline'}
            className="h-7 text-xs px-2.5"
            onClick={() => setSelectedDoc('all')}
          >
            All Docs ({chunks.length})
          </Button>
          {status?.sources.map((s) => (
            <Button
              key={s.source}
              type="button"
              size="sm"
              variant={selectedDoc === s.source ? 'default' : 'outline'}
              className="h-7 text-xs px-2"
              onClick={() => setSelectedDoc(s.source)}
            >
              {s.source} ({s.chunks})
            </Button>
          ))}
        </div>

        <div className="relative ml-auto flex-1 max-w-xs min-w-[180px]">
          <Search className="absolute left-2.5 top-2 size-3.5 text-muted-foreground" />
          <Input
            type="search"
            placeholder="Search chunks & sections..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="h-7 text-xs pl-8 pr-2"
          />
        </div>
      </div>

      {/* Chunks List */}
      <section aria-label="Indexed chunks" className="grid gap-2">
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>
            Showing {filteredChunks.length} of {chunks.length} chunks
            {selectedDoc !== 'all' && ` in ${selectedDoc}.md`}
          </span>
          {status?.embeddingModel && (
            <span>
              Embedding: <code className="text-foreground">{status.embeddingModel}</code> ({status.dimensions} dims)
            </span>
          )}
        </div>

        {chunksQuery.isPending ? (
          <p role="status" className="text-muted-foreground text-sm py-4">
            Loading indexed chunks...
          </p>
        ) : chunksQuery.isError ? (
          <p role="alert" className="text-destructive text-sm py-4">
            Failed to load knowledge chunks.
          </p>
        ) : filteredChunks.length === 0 ? (
          <p className="text-muted-foreground text-sm py-4">
            {chunks.length === 0
              ? 'No chunks found in database. Please click Reindex.'
              : 'No chunks matched your search filter.'}
          </p>
        ) : (
          <div
            className={cn(
              'grid gap-2.5 overflow-auto pr-1',
              compact ? 'max-h-[500px]' : 'max-h-[650px]',
            )}
          >
            {filteredChunks.map((chunk) => (
              <ChunkCard key={chunk.id} chunk={chunk} />
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

function ChunkCard({ chunk }: { chunk: KnowledgeChunkItem }) {
  const [expanded, setExpanded] = useState(false);
  const isLong = chunk.text.length > 280;

  return (
    <article
      className="rounded-lg border bg-card p-3 text-xs shadow-xs transition-colors hover:border-primary/40"
      data-testid={`chunk-${chunk.id}`}
    >
      <header className="flex flex-wrap items-center justify-between gap-1.5 pb-1.5 border-b mb-2">
        <div className="flex flex-wrap items-center gap-1.5 font-medium">
          <Badge variant="outline" className="text-[11px] font-semibold text-primary">
            {chunk.source}.md
          </Badge>
          <span className="text-foreground font-semibold text-sm">{chunk.section}</span>
        </div>
        <div className="flex items-center gap-1.5 text-muted-foreground text-[11px]">
          <span>Page {chunk.page}</span>
          <span>•</span>
          <span>Chunk #{chunk.ordinal}</span>
          {chunk.isExclusion && (
            <Badge variant="outline" className="border-warning text-warning-foreground text-[10px]">
              Exclusion
            </Badge>
          )}
          {chunk.hasEmbedding && (
            <Badge variant="secondary" className="text-success text-[10px]">
              Embedded
            </Badge>
          )}
        </div>
      </header>

      <div className="relative">
        <p
          className={cn(
            'whitespace-pre-wrap font-mono text-[11.5px] leading-relaxed text-muted-foreground',
            !expanded && isLong && 'line-clamp-3',
          )}
        >
          {chunk.text}
        </p>
        {isLong && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="h-6 mt-1 px-1.5 text-xs text-primary"
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? 'Show less' : 'Show full text'}
          </Button>
        )}
      </div>
    </article>
  );
}
