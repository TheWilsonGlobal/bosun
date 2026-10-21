import type { BandOrNone, BandThresholds, ExtractedRow, FieldValue } from './types';

/**
 * Band of a score. The thresholds come from the extraction payload (`bands`); they are never hard-coded here.
 * Rounded to 4 places like the backend (Bands.Of) so the two sides agree on a boundary value.
 */
export function bandOf(
  score: number | null | undefined,
  fallback: string | null | undefined,
  t: BandThresholds,
): BandOrNone {
  if (typeof score === 'number' && Number.isFinite(score)) {
    const r = Math.round(score * 10000) / 10000;
    return r >= t.autoAtLeast ? 'auto' : r >= t.reviewAtLeast ? 'review' : 'must_fix';
  }
  return fallback === 'auto' || fallback === 'review' || fallback === 'must_fix'
    ? fallback
    : 'none';
}

export const rowBand = (row: ExtractedRow, t: BandThresholds) => bandOf(row.score, row.band, t);
export const fieldBand = (f: FieldValue | undefined, t: BandThresholds): BandOrNone =>
  f ? bandOf(f.confidence, f.band, t) : 'none';

export const BAND_LABEL: Record<BandOrNone, string> = {
  auto: 'Auto',
  review: 'Review',
  must_fix: 'Must fix',
  none: 'Empty',
};

/** Cell colours. Colour is never the only cue: every cell also carries the BAND_MARK glyph. */
export const BAND_CLASS: Record<BandOrNone, string> = {
  auto: 'bg-success/15',
  review: 'bg-warning/25',
  must_fix: 'bg-destructive/20',
  none: '',
};
export const BAND_MARK: Record<BandOrNone, string> = {
  auto: '✓',
  review: '!',
  must_fix: '✕',
  none: '',
};

export const fmtThreshold = (n: number) => n.toFixed(2);

export function bandRange(band: 'auto' | 'review' | 'must_fix', t: BandThresholds): string {
  switch (band) {
    case 'auto':
      return `score >= ${fmtThreshold(t.autoAtLeast)}`;
    case 'review':
      return `${fmtThreshold(t.reviewAtLeast)} to < ${fmtThreshold(t.autoAtLeast)}`;
    case 'must_fix':
      return `score < ${fmtThreshold(t.reviewAtLeast)}`;
  }
}

export const FLAG_LABEL: Record<string, string> = {
  suspected_injection: 'Suspected prompt injection: a cell contains instruction-like text',
  page_injection_text: 'Instruction-like text elsewhere on this page',
  tag_not_in_source: 'The tag name does not occur in the page text (invented or injected?)',
  exact_duplicate: 'Exact duplicate of an earlier row',
  dual_read_missing: 'Text read and vision read disagree about whether this row exists',
  dual_read_agree: 'Second read (vision) agrees',
  dual_read_disagree: 'Second read (vision) disagrees',
  not_in_source: 'Value not found in the page text',
  normalisation_guess: 'The value was normalised with a guess (check it)',
  model_unclear: 'The model reported this value as hard to read',
  human_edited: 'Edited by a person',
  processing_failed: 'row could not be processed',
};

export const flagLabel = (flag: string) => FLAG_LABEL[flag] ?? flag;

export type RowFilter = 'all' | 'auto' | 'review' | 'must_fix' | 'injection';
export const ROW_FILTERS: { value: RowFilter; label: string }[] = [
  { value: 'all', label: 'All rows' },
  { value: 'auto', label: 'Auto' },
  { value: 'review', label: 'Review' },
  { value: 'must_fix', label: 'Must fix' },
  { value: 'injection', label: 'Injection-flagged' },
];

export const isInjection = (row: ExtractedRow) => row.flags.includes('suspected_injection');

export function filterRows(
  rows: ExtractedRow[],
  filter: RowFilter,
  t: BandThresholds,
): ExtractedRow[] {
  switch (filter) {
    case 'all':
      return rows;
    case 'injection':
      return rows.filter(isInjection);
    default:
      return rows.filter((r) => rowBand(r, t) === filter);
  }
}

export function countBy(rows: ExtractedRow[], t: BandThresholds) {
  const out = { all: rows.length, auto: 0, review: 0, must_fix: 0, injection: 0 };
  for (const r of rows) {
    const b = rowBand(r, t);
    if (b !== 'none') out[b] += 1;
    if (isInjection(r)) out.injection += 1;
  }
  return out;
}

/** Everything worth saying about a cell, one line each (tooltip + screen-reader text). */
export function cellNotes(f: FieldValue | undefined): string[] {
  if (!f) return [];
  const notes: string[] = [];
  for (const v of f.violations) notes.push(`${v.severity}: ${v.message}`);
  for (const flag of f.flags) notes.push(flagLabel(flag));
  if (f.raw !== null && f.raw !== undefined && String(f.value ?? '') !== f.raw) {
    notes.push(`Printed in the document as: ${f.raw}`);
  }
  return notes;
}

export function formatValue(v: string | number | null | undefined): string {
  return v === null || v === undefined ? '' : String(v);
}
