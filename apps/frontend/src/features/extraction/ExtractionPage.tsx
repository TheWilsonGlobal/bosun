import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, FileSearch } from 'lucide-react';
import { Status } from '@/components/Status';
import { Button } from '@/components/ui/button';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import { describeExtractionFailure } from './messages';
import { ExtractionStatusBadge, PagesReport, TokensLine } from './parts';
import { ReviewWorkspace } from './ReviewWorkspace';
import { useExtraction } from './useExtraction';
import { isActive, type Extraction } from './types';

function Progress({ ext }: { ext: Extraction }) {
  const { phase, pagesDone, pagesTotal } = ext.progress;
  const pct = pagesTotal > 0 ? Math.round((pagesDone / pagesTotal) * 100) : 0;
  return (
    <div className="grid gap-1" data-testid="progress">
      <p className="text-sm">
        {ext.status === 'queued' ? 'Waiting to start...' : 'Reading the document'}
        {phase ? ` (${phase})` : ''}: page {pagesDone} of {pagesTotal}
      </p>
      <div
        role="progressbar"
        aria-label="Extraction progress"
        aria-valuemin={0}
        aria-valuemax={Math.max(pagesTotal, 1)}
        aria-valuenow={pagesDone}
        className="bg-muted h-2 w-full max-w-md overflow-hidden rounded"
      >
        <div className="bg-primary h-full transition-[width]" style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

export function ExtractionPage() {
  const id = Number(useParams().id);
  const query = useExtraction(id);
  const ext = query.data;

  if (!Number.isInteger(id))
    return (
      <Status tone="error" title="Not found">
        Invalid extraction id.
      </Status>
    );
  if (query.isPending) return <Status>Loading extraction...</Status>;
  if (!ext) {
    const m = describeError(query.error);
    return (
      <Status tone="error" title={m.title}>
        <span className="block">{m.message}</span>
        {!(query.error instanceof ApiError && query.error.status === 404) && (
          <Button className="mt-3" size="sm" variant="outline" onClick={() => void query.refetch()}>
            Try again
          </Button>
        )}
      </Status>
    );
  }

  const failure =
    ext.status === 'failed' || ext.status === 'partial' || ext.errorCode
      ? describeExtractionFailure(ext.errorCode, ext.errorMessage)
      : null;
  const showRows = ext.status === 'completed' || ext.status === 'partial';
  const stale = query.isError && isActive(ext.status);

  return (
    <div className="flex h-full flex-col gap-3 overflow-auto p-4">
      <div>
        <Link
          to="/documents"
          className="text-muted-foreground inline-flex items-center gap-1 text-sm hover:underline"
        >
          <ArrowLeft className="size-3.5" aria-hidden /> Documents
        </Link>
        <div className="mt-1 flex flex-wrap items-center gap-3">
          <FileSearch className="size-5" aria-hidden />
          <h1 className="text-lg font-semibold">
            Extraction #{ext.id}: {ext.document.displayName}
          </h1>
          <ExtractionStatusBadge status={ext.status} />
          <span className="text-muted-foreground text-sm">
            {ext.document.pageCount} page(s), uploaded by {ext.document.uploadedBy}
          </span>
        </div>
      </div>

      {stale && (
        <p
          role="status"
          className="text-warning-foreground bg-warning/30 rounded px-3 py-1 text-sm"
        >
          Lost contact with the backend; still waiting for the extraction (retrying).
        </p>
      )}

      {isActive(ext.status) && <Progress ext={ext} />}

      {failure && (
        <div
          role="alert"
          className={
            ext.status === 'partial'
              ? 'border-warning bg-warning/20 rounded-md border px-3 py-2 text-sm'
              : 'border-destructive bg-destructive/10 rounded-md border px-3 py-2 text-sm'
          }
        >
          <p className="font-semibold">
            {ext.status === 'partial'
              ? `Some pages could not be read: ${failure.title}`
              : failure.title}
          </p>
          <p>{failure.message}</p>
          {ext.status === 'partial' && (
            <p className="mt-1">
              The rows from the pages that were read are usable. Read the document again from the{' '}
              <Link className="underline" to="/documents">
                documents list
              </Link>{' '}
              to retry the missing pages.
            </p>
          )}
          {ext.status === 'failed' && (
            <p className="mt-1">
              Nothing was extracted. Read the document again from the{' '}
              <Link className="underline" to="/documents">
                documents list
              </Link>
              .
            </p>
          )}
        </div>
      )}

      <TokensLine tokens={ext.tokens} model={ext.model} />

      <details className="text-sm" open={ext.status === 'failed' || ext.status === 'partial'}>
        <summary className="cursor-pointer font-medium">
          Page triage ({ext.pages.length} page(s))
        </summary>
        <div className="mt-2">
          <PagesReport pages={ext.pages} />
        </div>
      </details>

      {ext.changeSetIds.length > 0 && (
        <p className="text-sm" data-testid="change-set-links">
          Change sets from this extraction:{' '}
          {ext.changeSetIds.map((csId, i) => (
            <span key={csId}>
              {i > 0 && ', '}
              <Link className="underline" to={`/change-sets/${csId}`}>
                #{csId}
              </Link>
            </span>
          ))}
        </p>
      )}

      {showRows && (
        <div className="flex min-h-[28rem] flex-1 flex-col">
          <ReviewWorkspace extraction={ext} />
        </div>
      )}
    </div>
  );
}
