// Wire types of the extraction API (camelCase JSON). Mirrors apps/backend/src/Bosun.Extraction/Service/ExtractionDtos.cs.

export type ExtractionStatus = 'queued' | 'running' | 'completed' | 'partial' | 'failed';
export type RowStateName = 'pending' | 'accepted' | 'excluded';
export type Band = 'auto' | 'review' | 'must_fix';
export type BandOrNone = Band | 'none';

/** Field keys exactly as the backend sends them (Fields.cs). There is no sourceRef field: that is a row member. */
export const FIELD_KEYS = [
  'tagName',
  'description',
  'signalType',
  'unit',
  'rangeLow',
  'rangeHigh',
  'alarmHh',
  'alarmH',
  'alarmL',
  'alarmLl',
  'delayS',
] as const;
export type FieldKey = (typeof FIELD_KEYS)[number];

export const NUMERIC_FIELDS: ReadonlySet<string> = new Set([
  'rangeLow',
  'rangeHigh',
  'alarmHh',
  'alarmH',
  'alarmL',
  'alarmLl',
  'delayS',
]);

export interface RowViolation {
  code: string;
  severity: 'error' | 'warning' | 'info';
  field: string | null;
  message: string;
}

export interface ExtractionSummary {
  rows: number;
  auto: number;
  review: number;
  mustFix: number;
  accepted: number;
  excluded: number;
  humanCorrected: number;
  injectionFlagged: number;
  openMustFix: number;
}

export interface ExtractionBrief {
  id: number;
  status: ExtractionStatus;
  createdAt: string;
  summary: ExtractionSummary;
}

export interface DocumentItem {
  id: number;
  projectId: number;
  displayName: string;
  sizeBytes: number;
  pageCount: number;
  uploadedBy: string;
  uploadedAt: string;
  latestExtraction: ExtractionBrief | null;
}

export interface UploadResult {
  documentId: number;
  extractionId: number;
}

export interface FieldValue {
  value: string | number | null;
  raw: string | null;
  confidence: number | null;
  band: string | null;
  source: string;
  flags: string[];
  violations: RowViolation[];
}

export interface ExtractedRow {
  id: number;
  seq: number;
  page: number;
  sourcePath: string;
  sourceRef: string | null;
  snippet: string | null;
  /** [x0, y0, x1, y1] as fractions of the page, origin top-left (ExtractionPipeline.Bbox); null when unknown. */
  bbox: number[] | null;
  state: RowStateName;
  band: string;
  score: number;
  humanCorrected: boolean;
  flags: string[];
  violations: RowViolation[];
  fields: Record<string, FieldValue>;
  changeSetId: number | null;
  version: number;
}

export interface PageReport {
  page: number;
  path: string; // text | vision | skip
  reasons: string[];
  charCount: number;
  wordCount: number;
  garbageRatio: number;
  horizontalLines: number;
  verticalLines: number;
  tableLikeRows: number;
  imageAreaRatio: number;
  status: string;
  rows: number;
  reconstructedRows: number;
  unmatchedRows: string[];
  headerBorrowed: boolean;
  dualRead: boolean;
  dualReadOnly: string[];
  injectionHits: string[];
  errorCode: string | null;
  errorMessage: string | null;
}

export interface BandThresholds {
  autoAtLeast: number;
  reviewAtLeast: number;
}

export interface ExtractionTokens {
  prompt: number;
  candidate: number;
  thought: number;
  total: number;
  llmCalls: number;
  costUsd: number;
}

export interface Extraction {
  id: number;
  projectId: number;
  documentId: number;
  document: DocumentItem;
  status: ExtractionStatus;
  progress: { phase: string | null; pagesDone: number; pagesTotal: number };
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  model: string | null;
  promptVersion: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  tokens: ExtractionTokens;
  pages: PageReport[];
  summary: ExtractionSummary;
  rows: ExtractedRow[];
  changeSetIds: number[];
  bands: BandThresholds;
}

export interface PatchBody {
  fields?: Record<string, string | number | null>;
  excluded?: boolean;
}

export interface PatchResult {
  row: ExtractedRow;
  alsoChanged: ExtractedRow[];
  summary: ExtractionSummary;
}

export interface AcceptResult {
  extractionId: number;
  changeSets: { id: number; title: string; opCount: number; rowIds: number[] }[];
  acceptedRowIds: number[];
}

export const isActive = (s: ExtractionStatus) => s === 'queued' || s === 'running';
