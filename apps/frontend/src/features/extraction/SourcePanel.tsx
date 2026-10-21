import { useQuery } from '@tanstack/react-query';
import { ImageOff } from 'lucide-react';
import { fetchPageImage } from './api';
import { describeImageError } from './messages';
import { flagLabel } from './bands';
import type { ExtractedRow } from './types';

/**
 * The bbox is [x0, y0, x1, y1] as FRACTIONS of the page with the origin at the top-left (ExtractionPipeline.Bbox:
 * PDF text coordinates, y flipped, divided by the page size, padded 0.004). The image endpoint without rowId
 * renders the whole page, so the fractions map straight onto the displayed image as percentages.
 * A vision-path row has no bbox; then only the snippet is shown.
 */
export function validBbox(b: number[] | null | undefined): [number, number, number, number] | null {
  if (!b || b.length !== 4 || !b.every((n) => Number.isFinite(n))) return null;
  const [x0, y0, x1, y1] = b;
  if (x1 <= x0 || y1 <= y0 || x0 < 0 || y0 < 0 || x1 > 1.0001 || y1 > 1.0001) return null;
  return [x0, y0, x1, y1];
}

export function highlightStyle(b: [number, number, number, number]) {
  const [x0, y0, x1, y1] = b;
  return {
    left: `${x0 * 100}%`,
    top: `${y0 * 100}%`,
    width: `${(x1 - x0) * 100}%`,
    height: `${(y1 - y0) * 100}%`,
  };
}

/** A data URL needs no revoking and lets the query cache own the image (the blob itself is not kept). */
async function blobToDataUrl(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return `data:${blob.type || 'image/png'};base64,${btoa(binary)}`;
}

export function SourcePanel({
  extractionId,
  row,
}: {
  extractionId: number;
  row: ExtractedRow | null;
}) {
  const page = row?.page ?? null;
  const image = useQuery({
    queryKey: ['page-image', extractionId, page],
    queryFn: async ({ signal }) => blobToDataUrl(await fetchPageImage(extractionId, page!, signal)),
    enabled: page !== null,
    staleTime: Infinity,
  });
  const url = image.data ?? null;
  const box = validBbox(row?.bbox);

  return (
    <aside aria-label="Source page" className="bg-card flex h-full min-h-0 flex-col gap-2 p-2">
      {!row ? (
        <p className="text-muted-foreground p-2 text-sm">
          Select a row to see where it came from in the document.
        </p>
      ) : (
        <>
          <div className="text-sm">
            <p className="font-semibold">
              Row {row.seq} - page {row.page}{' '}
              <span className="text-muted-foreground text-xs font-normal">
                ({row.sourcePath === 'vision' ? 'VISION' : 'TEXT'} path
                {row.sourceRef ? `, ${row.sourceRef}` : ''})
              </span>
            </p>
            {row.snippet ? (
              <>
                <p className="text-muted-foreground mt-1 text-xs">
                  Source text (plain text, never interpreted):
                </p>
                <p
                  className="bg-muted mt-0.5 rounded p-1.5 font-mono text-xs break-words whitespace-pre-wrap"
                  data-testid="snippet"
                >
                  {row.snippet}
                </p>
              </>
            ) : (
              <p className="text-muted-foreground mt-1 text-xs">No source text recorded.</p>
            )}
            {row.flags.length > 0 && (
              <ul className="mt-1 list-disc pl-4 text-xs">
                {row.flags.map((f) => (
                  <li key={f}>{flagLabel(f)}</li>
                ))}
              </ul>
            )}
          </div>
          <div className="min-h-0 flex-1 overflow-auto rounded border">
            {image.isPending ? (
              <p role="status" className="text-muted-foreground p-3 text-sm">
                Loading page {row.page}...
              </p>
            ) : image.isError ? (
              <div role="alert" className="text-destructive flex items-start gap-2 p-3 text-sm">
                <ImageOff className="mt-0.5 size-4 shrink-0" aria-hidden />
                <div>
                  <p className="font-semibold">{describeImageError(image.error).title}</p>
                  <p>{describeImageError(image.error).message}</p>
                </div>
              </div>
            ) : url ? (
              <div className="relative w-full">
                <img
                  src={url}
                  alt={`Page ${row.page} of the document`}
                  className="block w-full"
                  data-testid="page-image"
                />
                {box && (
                  <div
                    data-testid="bbox-highlight"
                    role="img"
                    aria-label={`Highlighted region of row ${row.seq}`}
                    className="border-destructive bg-destructive/15 pointer-events-none absolute border-2"
                    style={highlightStyle(box)}
                  />
                )}
              </div>
            ) : (
              <p className="text-muted-foreground p-3 text-sm">Page image loaded.</p>
            )}
          </div>
          {!box && (
            <p className="text-muted-foreground text-xs">
              No position was recorded for this row (typical for the vision path), so no region is
              highlighted.
            </p>
          )}
        </>
      )}
    </aside>
  );
}
