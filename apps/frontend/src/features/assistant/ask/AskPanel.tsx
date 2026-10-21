import { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, BookOpen, Database, LogIn, RefreshCw, Search } from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { useAuth } from '@/features/auth/session';
import { describeError } from '@/lib/api/errors';
import { cn } from '@/lib/utils';
import { askDocs, fetchKnowledge, reindexKnowledge } from './api';
import {
  citationLabel,
  describeAskError,
  describeRefusal,
  describeReindexError,
  EXAMPLE_QUESTION,
  formatCost,
  MAX_QUESTION_LENGTH,
  splitAnswer,
  type AskResponse,
  type KnowledgeStatus,
} from './askLogic';

const knowledgeKey = (projectId: number) => ['knowledge', projectId] as const;

/** The knowledge status line plus the Reindex button. */
export function KnowledgeLine({
  projectId,
  status,
}: {
  projectId: number;
  status: KnowledgeStatus;
}) {
  const { session, openLogin } = useAuth();
  const qc = useQueryClient();
  const [notice, setNotice] = useState<{
    tone: 'ok' | 'error';
    title: string;
    text: string;
  } | null>(null);
  const reindex = useMutation({
    mutationFn: () => reindexKnowledge(projectId),
    onMutate: () => setNotice(null),
    onSuccess: (r) => {
      setNotice({
        tone: 'ok',
        title: 'Reindexed',
        text: `${r.chunks} chunks from ${r.documents} documents: ${r.embedded} embedded (${r.fromStore} unchanged, ${r.fromCache} from cache), ${r.embeddingRequests} embedding request${r.embeddingRequests === 1 ? '' : 's'}.`,
      });
      void qc.invalidateQueries({ queryKey: knowledgeKey(projectId) });
    },
    onError: (e) => {
      const m = describeReindexError(e);
      setNotice({ tone: 'error', title: m.title, text: m.message });
      if (m.needsLogin) openLogin();
    },
  });

  return (
    <div className="grid gap-1.5 text-xs" data-testid="knowledge-status">
      <div className="flex flex-wrap items-center gap-2">
        <Database className="text-muted-foreground size-3.5" aria-hidden />
        {status.indexed ? (
          <span>
            Indexed: {status.chunks} chunks from {status.documents} document
            {status.documents === 1 ? '' : 's'}
            {status.embeddingModel && `, ${status.embeddingModel}`}
            {status.dimensions ? ` (${status.dimensions} dims)` : ''}
          </span>
        ) : (
          <span className="font-medium">
            Not indexed
            {status.documentsOnDisk > 0 && ` (${status.documentsOnDisk} documents on disk)`}
          </span>
        )}
        {session ? (
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="ml-auto h-7 px-2 text-xs"
            disabled={reindex.isPending}
            onClick={() => reindex.mutate()}
          >
            <RefreshCw
              className={cn(
                'size-3.5',
                reindex.isPending && 'animate-spin motion-reduce:animate-none',
              )}
              aria-hidden
            />
            {reindex.isPending ? 'Reindexing...' : status.indexed ? 'Reindex' : 'Index documents'}
          </Button>
        ) : (
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="ml-auto h-7 px-2 text-xs"
            onClick={openLogin}
          >
            <LogIn className="size-3.5" aria-hidden /> Sign in to reindex
          </Button>
        )}
      </div>
      {status.indexed && status.lastIndexedAt && (
        <span className="text-muted-foreground">
          Last indexed {new Date(status.lastIndexedAt).toLocaleString()}
        </span>
      )}
      {notice && (
        <p
          role={notice.tone === 'error' ? 'alert' : 'status'}
          className={cn(notice.tone === 'error' && 'text-destructive')}
        >
          <span className="font-semibold">{notice.title}.</span> {notice.text}
        </p>
      )}
    </div>
  );
}

/** The answer as plain text; a marker that matches a returned citation becomes a chip. Never markup. */
export function AnswerText({ response }: { response: AskResponse }) {
  const segments = splitAnswer(response.answer ?? '', response.citations);
  return (
    <p className="text-sm break-words whitespace-pre-wrap" data-testid="ask-answer">
      {segments.map((s, i) =>
        s.kind === 'text' ? (
          <span key={i}>{s.text}</span>
        ) : (
          <span
            key={i}
            data-testid="citation-chip"
            title={citationLabel(s.citation)}
            className="bg-secondary text-secondary-foreground mx-0.5 inline-block rounded-full border px-2 py-0.5 align-baseline text-xs"
          >
            {citationLabel(s.citation)}
          </span>
        ),
      )}
    </p>
  );
}

function Retrieved({ response }: { response: AskResponse }) {
  if (response.retrieved.length === 0) return null;
  const used = response.retrieved.filter((r) => r.usedForAnswer).length;
  return (
    <details className="text-xs" data-testid="sources-retrieved">
      <summary className="cursor-pointer font-medium">
        Sources retrieved ({response.retrieved.length}, {used} used)
      </summary>
      <ol className="mt-1.5 grid gap-1">
        {response.retrieved.map((r, i) => (
          <li key={i} className="flex flex-wrap items-center gap-x-2 rounded border px-2 py-1">
            <span className="font-medium">{citationLabel(r)}</span>
            <span className="text-muted-foreground tabular-nums">
              score {r.score.toFixed(3)}
              {r.cosine !== null && `, cosine ${r.cosine.toFixed(3)}`}
            </span>
            <Badge
              variant="outline"
              className={cn(
                'ml-auto font-normal',
                r.usedForAnswer ? 'border-success/60 text-success' : 'text-muted-foreground',
              )}
            >
              {r.usedForAnswer ? 'used' : 'not used'}
            </Badge>
            {r.flagged && (
              <Badge
                variant="outline"
                data-testid="flagged-source"
                title="This passage contains text that reads like an instruction to the model. It was treated as data only."
                className="border-warning text-warning-foreground gap-1 font-normal"
              >
                <AlertTriangle className="size-3" aria-hidden /> flagged as instruction-like
              </Badge>
            )}
            {!r.aboveThreshold && (
              <span className="text-muted-foreground">below relevance threshold</span>
            )}
          </li>
        ))}
      </ol>
    </details>
  );
}

export function AskResult({ response }: { response: AskResponse }) {
  const refused = !response.answered || response.refusalReason !== null;
  const refusal = refused ? describeRefusal(response.refusalReason) : null;
  return (
    <section aria-label="Answer" className="grid gap-2">
      <p className="bg-muted/50 rounded-md px-3 py-2 text-sm break-words">{response.question}</p>
      {refusal ? (
        <div
          role="status"
          data-testid="ask-refusal"
          data-reason={response.refusalReason ?? 'none'}
          className="border-warning bg-warning/20 flex gap-2 rounded-md border px-3 py-2 text-sm"
        >
          <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
          <div>
            <p className="font-semibold">{refusal.title}</p>
            <p>{refusal.message}</p>
          </div>
        </div>
      ) : (
        <AnswerText response={response} />
      )}
      {response.droppedCitations > 0 && (
        <p role="note" data-testid="dropped-citations" className="text-muted-foreground text-xs">
          {response.droppedCitations} citation{response.droppedCitations === 1 ? '' : 's'} given by
          the model did not match a retrieved passage and{' '}
          {response.droppedCitations === 1 ? 'was' : 'were'} removed.
        </p>
      )}
      {(response.unsupportedSentencesRemoved ?? 0) > 0 && (
        <p role="note" data-testid="unsupported-removed" className="text-muted-foreground text-xs">
          {response.unsupportedSentencesRemoved} sentence
          {response.unsupportedSentencesRemoved === 1 ? '' : 's'} of the model's answer cited a
          source that was not retrieved and{' '}
          {response.unsupportedSentencesRemoved === 1 ? 'was' : 'were'} removed. Check the answer
          against the sources.
        </p>
      )}
      <Retrieved response={response} />
      <p className="text-muted-foreground text-xs" data-testid="ask-usage">
        {response.model}
        {response.promptVersion && `, prompt ${response.promptVersion}`}
        {response.usage && (
          <>
            {' '}
            - {response.usage.totalTokens.toLocaleString()} tokens, est.{' '}
            {formatCost(response.usage.estimatedCostUsd)}
          </>
        )}{' '}
        - {(response.latencyMs / 1000).toFixed(1)} s
      </p>
    </section>
  );
}

export function AskPanel({ projectId }: { projectId: number | null }) {
  const { session, openLogin, loading: sessionLoading } = useAuth();
  const [text, setText] = useState('');
  const [response, setResponse] = useState<AskResponse | null>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  const knowledge = useQuery({
    queryKey: knowledgeKey(projectId ?? 0),
    queryFn: () => fetchKnowledge(projectId!),
    enabled: projectId !== null,
  });

  const ask = useMutation({
    mutationFn: (question: string) => askDocs(projectId!, question),
    onMutate: () => setResponse(null),
    onSuccess: (r) => setResponse(r),
    onError: (e) => {
      const m = describeAskError(e);
      if (m.needsLogin) openLogin();
      if (m.notIndexed) void knowledge.refetch();
    },
  });

  const question = text.trim();
  const notIndexed = knowledge.data ? !knowledge.data.indexed : false;
  const canAsk = !!session && projectId !== null && question.length > 0 && !ask.isPending;
  const error = ask.error ? describeAskError(ask.error) : null;

  const submit = () => {
    if (!canAsk) return;
    ask.mutate(question);
    // The box stays readOnly (not disabled) while asking, and focus returns to it when the button was used.
    inputRef.current?.focus();
  };

  return (
    <div className="grid gap-4" data-testid="ask-mode">
      {knowledge.isPending && projectId !== null && (
        <p className="text-muted-foreground text-xs">Checking the document index...</p>
      )}
      {knowledge.isError && (
        <p role="alert" className="text-destructive text-xs">
          {describeError(knowledge.error).title}: {describeError(knowledge.error).message}
        </p>
      )}
      {knowledge.data && projectId !== null && (
        <KnowledgeLine projectId={projectId} status={knowledge.data} />
      )}
      {notIndexed && (
        <p
          role="status"
          data-testid="not-indexed"
          className="border-warning bg-warning/20 rounded-md border px-3 py-2 text-sm"
        >
          The project documents are not indexed yet, so questions cannot be answered. Use the button
          above to index them.
        </p>
      )}

      <form
        className="grid gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        <label htmlFor="ask-input" className="text-sm font-medium">
          Ask the project documents
        </label>
        <Textarea
          id="ask-input"
          value={text}
          maxLength={MAX_QUESTION_LENGTH}
          rows={3}
          ref={inputRef}
          disabled={!session}
          readOnly={ask.isPending}
          aria-busy={ask.isPending}
          placeholder="e.g. what does the alarm philosophy say about delays?"
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
              e.preventDefault();
              submit();
            }
          }}
        />
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            className="bg-secondary text-secondary-foreground hover:bg-secondary/80 focus-visible:ring-ring/50 max-w-full rounded-full px-3 py-1 text-left text-xs outline-none focus-visible:ring-[3px] disabled:opacity-50"
            disabled={!session || ask.isPending}
            onClick={() => setText(EXAMPLE_QUESTION)}
            title="Use this example"
          >
            {EXAMPLE_QUESTION}
          </button>
        </div>
        <div className="flex items-center gap-2">
          <span className="text-muted-foreground text-xs tabular-nums">
            {text.length} / {MAX_QUESTION_LENGTH}
          </span>
          {session ? (
            <Button type="submit" size="sm" className="ml-auto" disabled={!canAsk}>
              <Search aria-hidden /> {ask.isPending ? 'Asking...' : 'Ask'}
            </Button>
          ) : (
            <Button
              type="button"
              size="sm"
              className="ml-auto"
              onClick={openLogin}
              disabled={sessionLoading}
            >
              <LogIn aria-hidden /> Sign in to ask
            </Button>
          )}
        </div>
        {error && (
          <div
            role="alert"
            data-testid="ask-error"
            className="border-destructive/50 bg-destructive/10 rounded-md border px-3 py-2 text-sm"
          >
            <p className="font-semibold">{error.title}</p>
            <p>{error.message}</p>
            {error.detail && (
              <details className="mt-1 text-xs">
                <summary className="cursor-pointer">Details</summary>
                <p className="text-muted-foreground mt-1 break-words">{error.detail}</p>
              </details>
            )}
          </div>
        )}
      </form>

      {ask.isPending && (
        <p className="text-muted-foreground flex items-center gap-1.5 text-sm" role="status">
          <BookOpen className="size-4 animate-pulse motion-reduce:animate-none" aria-hidden />{' '}
          Searching the documents...
        </p>
      )}
      <div aria-live="polite" data-testid="ask-live">
        {response && <AskResult response={response} />}
      </div>
    </div>
  );
}
