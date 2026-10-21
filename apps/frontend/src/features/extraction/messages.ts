import { ApiError } from '@/lib/api/client';
import { describeError, type ErrorMessage } from '@/lib/api/errors';
import { describeErrorCode, type Outcome } from '@/features/assistant/messages';

// Plan s7.8: every stable code has its own message. LLM codes reuse the assistant's wording (one place to change it).

const num = (v: unknown) => (typeof v === 'number' ? v : null);
const ids = (v: unknown): number[] =>
  Array.isArray(v) ? v.filter((x): x is number => typeof x === 'number') : [];

export function describeUploadError(error: unknown): ErrorMessage {
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'no_file':
        return { title: 'No file received', message: 'Choose a PDF file to upload.' };
      case 'not_a_pdf':
        return {
          title: 'Not a PDF',
          message:
            'The file does not start with the PDF signature, whatever its name says. Export or print the document as a PDF and try again.',
        };
      case 'document_too_large':
        return {
          title: 'File too large',
          message:
            'The file is larger than 20 MB. Split the document or reduce the scan resolution.',
        };
      case 'too_many_pages': {
        const pages = num(error.extensions.pages);
        const max = num(error.extensions.max);
        return {
          title: 'Too many pages',
          message: `${pages !== null ? `The document has ${pages} pages` : 'The document has too many pages'}${max !== null ? `; the limit is ${max}` : ''}. Upload it in parts.`,
        };
      }
      case 'pdf_unreadable':
        return {
          title: 'PDF cannot be read',
          message: 'The file is damaged or not a valid PDF. Open it in a viewer and re-export it.',
        };
      case 'page_too_large': {
        const page = num(error.extensions.page);
        const maxPoints = num(error.extensions.maxPoints);
        return {
          title: 'Page too large',
          message: `${page !== null ? `Page ${page}` : 'A page'} is larger than the supported page size${maxPoints !== null ? ` (${maxPoints} points on the longest side)` : ''}. Re-export the document with a smaller paper size and upload it again.`,
        };
      }
      case 'pdf_too_complex':
        return {
          title: 'PDF is too complex',
          message:
            'The document has more drawing content than can be read safely. Print it to a new PDF or split it, then upload again.',
        };
      case 'pdf_encrypted':
        return {
          title: 'PDF is password protected',
          message: 'Remove the password protection and upload the document again.',
        };
    }
  }
  return describeError(error);
}

export function describeRerunError(error: unknown): ErrorMessage {
  if (error instanceof ApiError && error.code === 'extraction_already_running') {
    return {
      title: 'Already being read',
      message: 'This document is already being read. Wait for it to finish, then read it again.',
    };
  }
  return describeError(error);
}

export function describeDeleteError(error: unknown): ErrorMessage & { refetch: boolean } {
  if (error instanceof ApiError) {
    if (error.code === 'extraction_already_running')
      return {
        title: 'Document is being read',
        message:
          'This document is being read right now. Wait for the extraction to finish, then delete it.',
        refetch: true,
      };
    if (error.status === 404)
      return {
        title: 'Document already deleted',
        message: 'This document no longer exists. The list was reloaded.',
        refetch: true,
      };
  }
  return { ...describeError(error), refetch: false };
}

export interface PatchFailure extends ErrorMessage {
  /** invalid_field: the column to show the message under. */
  field: string | null;
  /** The loaded data is stale: re-read the extraction. */
  refetch: boolean;
}

export function describePatchError(error: unknown): PatchFailure {
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'invalid_field': {
        const field = typeof error.extensions.field === 'string' ? error.extensions.field : null;
        return {
          title: 'Value rejected',
          message: error.detail || 'That value is not valid for this column.',
          field,
          refetch: false,
        };
      }
      case 'concurrent_modification':
        return {
          title: 'Saved by someone else at the same time',
          message: 'The row changed while you were editing it. It was reloaded; edit it again.',
          field: null,
          refetch: true,
        };
      case 'row_already_accepted':
        return {
          title: 'Row already accepted',
          message: 'This row is already part of a change set. Edit the change set instead.',
          field: null,
          refetch: true,
        };
      case 'extraction_not_ready':
        return {
          title: 'Extraction not ready',
          message: 'Rows can be edited once the extraction has completed.',
          field: null,
          refetch: true,
        };
    }
  }
  return { ...describeError(error), field: null, refetch: false };
}

export interface AcceptFailure extends ErrorMessage {
  kind: string;
  /** unresolved_must_fix / rows_already_decided / unknown_rows / duplicate_tags_chosen: the rows concerned. */
  rowIds: number[];
  tags: string[];
  /** The loaded data is stale: re-read the extraction. */
  refetch: boolean;
}

export function describeAcceptError(error: unknown): AcceptFailure {
  const base = { rowIds: [] as number[], tags: [] as string[], refetch: false };
  if (error instanceof ApiError) {
    const rowIds = ids(error.extensions.rowIds);
    switch (error.code) {
      case 'unresolved_must_fix':
        return {
          ...base,
          kind: error.code,
          rowIds,
          title: 'Must-fix rows are still selected',
          message: `${rowIds.length || 'Some'} selected row(s) are below the review threshold. Correct them, exclude them, or leave them out of the selection, then accept again.`,
        };
      case 'flagged_rows_need_override':
        return {
          ...base,
          kind: error.code,
          rowIds,
          title: 'Flagged rows need your confirmation',
          message: `${rowIds.length || 'Some'} selected row(s) contain suspected instruction-like text from the document. Review them and confirm before they are accepted.`,
        };
      case 'extraction_not_ready':
        return {
          ...base,
          kind: error.code,
          refetch: true,
          title: 'Extraction not ready',
          message: `The extraction is ${String(error.extensions.status ?? 'not finished')}. Rows can be accepted once it has completed.`,
        };
      case 'no_rows_to_accept':
        return {
          ...base,
          kind: error.code,
          title: 'Nothing to accept',
          message: 'There are no open rows left. Every row is already accepted or excluded.',
        };
      case 'unknown_rows':
        return {
          ...base,
          kind: error.code,
          rowIds,
          refetch: true,
          title: 'Unknown rows',
          message: 'Some selected rows do not belong to this extraction. The list was reloaded.',
        };
      case 'rows_already_decided':
        return {
          ...base,
          kind: error.code,
          rowIds,
          refetch: true,
          title: 'Rows already decided',
          message:
            'Some selected rows were already accepted or excluded (maybe in another window). The list was reloaded; review the selection.',
        };
      case 'duplicate_tags_chosen': {
        const tags = Array.isArray(error.extensions.tags)
          ? error.extensions.tags.filter((t): t is string => typeof t === 'string')
          : [];
        return {
          ...base,
          kind: error.code,
          rowIds,
          tags,
          title: 'Duplicate tag names selected',
          message: `Several selected rows carry the same tag name${tags.length ? ` (${tags.join(', ')})` : ''}. Exclude the repeats and accept again.`,
        };
      }
    }
    return { ...base, kind: error.code, ...describeError(error) };
  }
  return { ...base, kind: 'unknown', ...describeError(error) };
}

const EXTRACTION_CODES: Record<string, Outcome> = {
  extraction_budget_exceeded: {
    tone: 'warning',
    title: 'Extraction budget used up',
    message:
      'The extraction hit its model-call or token budget, so the remaining pages were not read. The rows from the pages that were read are kept.',
  },
  page_too_large: {
    tone: 'error',
    title: 'Page too large',
    message: 'This page is larger than the supported page size and was skipped.',
  },
  page_render_failed: {
    tone: 'error',
    title: 'Page could not be rendered',
    message: 'The page could not be turned into an image for reading and was skipped.',
  },
};

const REASON_LABELS: Record<string, string> = {
  page_content_capped:
    'content capped: only the first part of this page was read (it has too much content)',
};
export const describeReason = (reason: string) => REASON_LABELS[reason] ?? reason;

/** Page-level error: extraction codes first, then the assistant's llm_* wording. */
export function describePageError(code: string | null, detail: string | null): Outcome {
  return (code && EXTRACTION_CODES[code]) || describeErrorCode(code, detail);
}

/** The headline for a failed / partial extraction (llm_* codes: the assistant's messages). */
export function describeExtractionFailure(code: string | null, detail: string | null): Outcome {
  if (code === 'server_restarted') {
    return {
      tone: 'error',
      title: 'Server restarted during the extraction',
      message:
        'The backend restarted while this document was being read, so the run was stopped. Read the document again.',
    };
  }
  return describePageError(code, detail);
}

export function describeImageError(error: unknown): ErrorMessage {
  if (error instanceof ApiError && error.code === 'page_out_of_range') {
    return {
      title: 'Page not available',
      message: error.detail || 'That page does not exist in this document.',
    };
  }
  return describeError(error);
}
