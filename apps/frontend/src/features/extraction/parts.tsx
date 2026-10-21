import {
  AlertCircle,
  CheckCircle2,
  CircleDashed,
  FileText,
  Loader2,
  ScanLine,
  ShieldAlert,
  TriangleAlert,
} from 'lucide-react';
import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';
import { describePageError, describeReason } from './messages';
import { BAND_CLASS, BAND_LABEL, BAND_MARK, bandRange } from './bands';
import type {
  BandOrNone,
  BandThresholds,
  ExtractionStatus,
  ExtractionSummary,
  ExtractionTokens,
  PageReport,
} from './types';

const STATUS_STYLE: Record<ExtractionStatus, string> = {
  queued: 'bg-secondary text-secondary-foreground',
  running: 'bg-primary/15 text-primary',
  completed: 'bg-success/20 text-success',
  partial: 'bg-warning/30 text-warning-foreground',
  failed: 'bg-destructive/15 text-destructive',
};
const STATUS_LABEL: Record<ExtractionStatus, string> = {
  queued: 'Queued',
  running: 'Reading',
  completed: 'Completed',
  partial: 'Partial',
  failed: 'Failed',
};

export function ExtractionStatusBadge({ status }: { status: ExtractionStatus }) {
  const Icon =
    status === 'completed'
      ? CheckCircle2
      : status === 'failed'
        ? AlertCircle
        : status === 'partial'
          ? TriangleAlert
          : status === 'running'
            ? Loader2
            : CircleDashed;
  return (
    <Badge variant="outline" className={cn('border-transparent', STATUS_STYLE[status])}>
      <Icon className={cn('size-3', status === 'running' && 'animate-spin')} aria-hidden />
      {STATUS_LABEL[status]}
    </Badge>
  );
}

/** A band as icon-less text plus glyph: colour is never the only cue. */
export function BandTag({ band, className }: { band: BandOrNone; className?: string }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-xs font-medium',
        BAND_CLASS[band],
        className,
      )}
      data-band={band}
    >
      <span aria-hidden>{BAND_MARK[band]}</span>
      {BAND_LABEL[band]}
    </span>
  );
}

export function Legend({ bands }: { bands: BandThresholds }) {
  return (
    <section aria-label="Legend" className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
      <span className="font-semibold">Cell confidence:</span>
      {(['auto', 'review', 'must_fix'] as const).map((b) => (
        <span key={b} className="inline-flex items-center gap-1.5">
          <BandTag band={b} />
          <span className="text-muted-foreground">{bandRange(b, bands)}</span>
        </span>
      ))}
      <span className="inline-flex items-center gap-1.5">
        <ShieldAlert className="text-destructive size-3.5" aria-hidden />
        <span>Suspected injection</span>
      </span>
    </section>
  );
}

/** Counts come from the server summary (it is returned by every PATCH too), never recomputed here. */
export function SummaryBar({ summary }: { summary: ExtractionSummary }) {
  const items: [string, number][] = [
    ['Rows', summary.rows],
    ['Auto', summary.auto],
    ['Review', summary.review],
    ['Must fix', summary.mustFix],
    ['Open must-fix', summary.openMustFix],
    ['Accepted', summary.accepted],
    ['Excluded', summary.excluded],
    ['Human-corrected', summary.humanCorrected],
    ['Injection-flagged', summary.injectionFlagged],
  ];
  return (
    <dl
      aria-label="Extraction summary"
      className="grid grid-cols-3 gap-2 text-sm sm:grid-cols-5 xl:grid-cols-9"
    >
      {items.map(([label, n]) => (
        <div
          key={label}
          className={cn(
            'bg-card rounded-md border px-2 py-1',
            label === 'Open must-fix' && n > 0 && 'border-destructive',
            label === 'Injection-flagged' && n > 0 && 'border-destructive',
          )}
        >
          <dt className="text-muted-foreground text-xs">{label}</dt>
          <dd className="font-semibold tabular-nums" data-testid={`summary-${label}`}>
            {n}
          </dd>
        </div>
      ))}
    </dl>
  );
}

export const COST_TOOLTIP = 'estimated from a placeholder price table';

export function TokensLine({ tokens, model }: { tokens: ExtractionTokens; model: string | null }) {
  return (
    <p className="text-muted-foreground text-xs" data-testid="tokens">
      {model ? `${model} - ` : ''}
      {tokens.llmCalls} model call(s), tokens prompt {tokens.prompt.toLocaleString()} / answer{' '}
      {tokens.candidate.toLocaleString()} / thinking {tokens.thought.toLocaleString()} (total{' '}
      {tokens.total.toLocaleString()}),{' '}
      <span title={COST_TOOLTIP}>est. ${tokens.costUsd.toFixed(4)}</span>
    </p>
  );
}

const PATH_LABEL: Record<string, string> = {
  text: 'TEXT',
  vision: 'VISION',
  skip: 'SKIPPED',
};

export function PagesReport({ pages }: { pages: PageReport[] }) {
  if (pages.length === 0)
    return <p className="text-muted-foreground text-sm">No page report yet.</p>;
  return (
    <div className="overflow-auto rounded-md border">
      <table className="w-full text-left text-xs">
        <caption className="sr-only">Per-page triage report</caption>
        <thead className="bg-muted">
          <tr>
            <th scope="col" className="px-2 py-1">
              Page
            </th>
            <th scope="col" className="px-2 py-1">
              Read path
            </th>
            <th scope="col" className="px-2 py-1">
              Why
            </th>
            <th scope="col" className="px-2 py-1">
              Status
            </th>
            <th scope="col" className="px-2 py-1">
              Rows
            </th>
            <th scope="col" className="px-2 py-1">
              Notes
            </th>
          </tr>
        </thead>
        <tbody>
          {pages.map((p) => (
            <tr key={p.page} className="border-t" data-testid={`page-${p.page}`}>
              <th scope="row" className="px-2 py-1 font-medium">
                {p.page}
              </th>
              <td className="px-2 py-1">
                <span
                  className={cn(
                    'inline-flex items-center gap-1 rounded px-1.5 py-0.5 font-semibold',
                    p.path === 'vision' ? 'bg-primary/15 text-primary' : 'bg-secondary',
                  )}
                >
                  {p.path === 'vision' ? (
                    <ScanLine className="size-3" aria-hidden />
                  ) : (
                    <FileText className="size-3" aria-hidden />
                  )}
                  {PATH_LABEL[p.path] ?? p.path.toUpperCase()}
                </span>
              </td>
              <td className="text-muted-foreground px-2 py-1">
                {p.reasons.map(describeReason).join(', ') || '-'}
              </td>
              <td className="px-2 py-1">{p.status}</td>
              <td className="px-2 py-1 tabular-nums">{p.rows}</td>
              <td className="px-2 py-1">
                {p.errorCode && (
                  <span className="text-destructive block">
                    {describePageError(p.errorCode, p.errorMessage).title}
                  </span>
                )}
                {p.injectionHits.length > 0 && (
                  <span className="text-destructive flex items-center gap-1">
                    <ShieldAlert className="size-3" aria-hidden /> instruction-like text (
                    {p.injectionHits.join(', ')})
                  </span>
                )}
                {p.dualRead && <span className="block">dual-read</span>}
                {p.headerBorrowed && <span className="block">header borrowed</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
