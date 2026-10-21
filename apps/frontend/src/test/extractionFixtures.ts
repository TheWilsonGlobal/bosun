import { FIELD_KEYS } from '@/features/extraction/types';
import type {
  ExtractedRow,
  Extraction,
  ExtractionSummary,
  FieldValue,
  PageReport,
} from '@/features/extraction/types';

export const makeField = (
  value: FieldValue['value'],
  over: Partial<FieldValue> = {},
): FieldValue => ({
  value,
  raw: value === null ? null : String(value),
  confidence: 0.95,
  band: 'auto',
  source: 'text',
  flags: [],
  violations: [],
  ...over,
});

export function makeRow(
  id: number,
  tag: string,
  over: Partial<ExtractedRow> = {},
  fields: Record<string, FieldValue> = {},
): ExtractedRow {
  const base: Record<string, FieldValue> = Object.fromEntries(
    FIELD_KEYS.map((k) => [k, makeField(null, { confidence: null, band: null })]),
  );
  base.tagName = makeField(tag);
  base.signalType = makeField('AI');
  base.unit = makeField('degC');
  return {
    id,
    seq: id,
    page: 1,
    sourcePath: 'text',
    sourceRef: `r${id}`,
    snippet: `${tag} Exhaust gas temperature`,
    bbox: [0.1, 0.2, 0.9, 0.25],
    state: 'pending',
    band: 'auto',
    score: 0.95,
    humanCorrected: false,
    flags: [],
    violations: [],
    fields: { ...base, ...fields },
    changeSetId: null,
    version: 1,
    ...over,
  };
}

export const makeSummary = (over: Partial<ExtractionSummary> = {}): ExtractionSummary => ({
  rows: 0,
  auto: 0,
  review: 0,
  mustFix: 0,
  accepted: 0,
  excluded: 0,
  humanCorrected: 0,
  injectionFlagged: 0,
  openMustFix: 0,
  ...over,
});

export const makePage = (
  page: number,
  path = 'text',
  over: Partial<PageReport> = {},
): PageReport => ({
  page,
  path,
  reasons: path === 'vision' ? ['no text layer'] : ['text layer ok'],
  charCount: 100,
  wordCount: 20,
  garbageRatio: 0,
  horizontalLines: 3,
  verticalLines: 3,
  tableLikeRows: 5,
  imageAreaRatio: 0,
  status: 'done',
  rows: 1,
  reconstructedRows: 1,
  unmatchedRows: [],
  headerBorrowed: false,
  dualRead: false,
  dualReadOnly: [],
  injectionHits: [],
  errorCode: null,
  errorMessage: null,
  ...over,
});

export function makeExtraction(over: Partial<Extraction> = {}): Extraction {
  const rows = over.rows ?? [];
  return {
    id: 5,
    projectId: 1,
    documentId: 3,
    document: {
      id: 3,
      projectId: 1,
      displayName: 'io-list.pdf',
      sizeBytes: 2048,
      pageCount: 2,
      uploadedBy: 'alice',
      uploadedAt: '2026-10-01T10:00:00Z',
      latestExtraction: null,
    },
    status: 'completed',
    progress: { phase: null, pagesDone: 2, pagesTotal: 2 },
    createdAt: '2026-10-01T10:00:00Z',
    startedAt: '2026-10-01T10:00:01Z',
    finishedAt: '2026-10-01T10:00:09Z',
    model: 'test-model',
    promptVersion: 'v1',
    errorCode: null,
    errorMessage: null,
    tokens: {
      prompt: 1200,
      candidate: 300,
      thought: 50,
      total: 1550,
      llmCalls: 2,
      costUsd: 0.0012,
    },
    pages: [makePage(1, 'text'), makePage(2, 'vision')],
    summary: makeSummary({ rows: rows.length }),
    rows,
    changeSetIds: [],
    bands: { autoAtLeast: 0.9, reviewAtLeast: 0.6 },
    ...over,
  };
}
