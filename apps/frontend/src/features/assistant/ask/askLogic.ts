import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/errors';
import { ERROR_MESSAGES } from '../messages';

// Wire types and pure helpers of the "Ask the docs" (RAG) mode.

export const MAX_QUESTION_LENGTH = 500;
export const EXAMPLE_QUESTION = 'Why is the delay 10 s and not 5?';

export interface AskCitation {
  source: string;
  section: string;
  page: number;
}

export type RefusalReason = 'below_threshold' | 'model_declined' | 'no_valid_citation';

export interface AskRetrieved extends AskCitation {
  score: number;
  cosine: number | null;
  aboveThreshold: boolean;
  usedForAnswer: boolean;
}

export interface AskUsage {
  promptTokens: number;
  candidateTokens: number;
  thoughtTokens: number;
  totalTokens: number;
  llmCalls: number;
  estimatedCostUsd: number;
}

export interface AskResponse {
  question: string;
  answered: boolean;
  answer: string | null;
  citations: AskCitation[];
  refusalReason: RefusalReason | null;
  droppedCitations: number;
  retrieved: AskRetrieved[];
  model: string | null;
  promptVersion: string | null;
  usage: AskUsage | null;
  latencyMs: number;
}

export interface KnowledgeSource {
  source: string;
  chunks: number;
  pages: number;
}

export interface KnowledgeStatus {
  indexed: boolean;
  documents: number;
  chunks: number;
  embeddedChunks: number;
  documentsOnDisk: number;
  embeddingModel: string | null;
  dimensions: number | null;
  lastIndexedAt: string | null;
  sources: KnowledgeSource[];
}

export interface ReindexResult {
  documents: number;
  chunks: number;
  embedded: number;
  fromStore: number;
  fromCache: number;
  embeddingRequests: number;
  promptTokens: number;
  skipped: number;
  model: string | null;
  dimensions: number | null;
  elapsedMs: number;
}

// ---- answer text -> segments ----

export type Segment =
  { kind: 'text'; text: string } | { kind: 'citation'; text: string; citation: AskCitation };

/** `[doc § section p.N]` exactly as the backend writes it. */
const INLINE = /\[([^[\]§]+?)\s*§\s*([^[\]]+?)\s+p\.\s*(\d+)\s*\]/g;

const same = (a: AskCitation, b: AskCitation) =>
  a.source === b.source && a.section === b.section && a.page === b.page;

/**
 * Splits an answer into plain-text and citation segments. Only a marker that matches an entry of `citations` becomes
 * a chip; every other bracketed text stays plain text. Nothing here produces markup: segments are rendered as text.
 */
export function splitAnswer(answer: string, citations: readonly AskCitation[]): Segment[] {
  const out: Segment[] = [];
  let last = 0;
  for (const m of answer.matchAll(INLINE)) {
    const candidate: AskCitation = {
      source: m[1].trim(),
      section: m[2].trim(),
      page: Number(m[3]),
    };
    const hit = citations.find((c) => same(c, candidate));
    if (!hit) continue; // unknown marker: stays inside the surrounding plain text
    const start = m.index;
    if (start > last) out.push({ kind: 'text', text: answer.slice(last, start) });
    out.push({ kind: 'citation', text: m[0], citation: hit });
    last = start + m[0].length;
  }
  if (last < answer.length) out.push({ kind: 'text', text: answer.slice(last) });
  return out;
}

export const citationLabel = (c: AskCitation) => `${c.source} §${c.section} p.${c.page}`;

// ---- messages ----

export interface Refusal {
  title: string;
  message: string;
}

export const REFUSALS: Record<RefusalReason, Refusal> = {
  below_threshold: {
    title: 'Not in the project documents',
    message:
      'None of the indexed passages is relevant enough to this question, so no answer was generated. Rephrase it, or check that the document that covers it is indexed.',
  },
  model_declined: {
    title: 'The assistant declined to answer',
    message:
      'Relevant passages were found, but the model judged that they do not answer the question. See the retrieved sources below.',
  },
  no_valid_citation: {
    title: 'Answer withheld: no verifiable source',
    message:
      'The model wrote an answer, but none of its citations matched a passage that was really retrieved, so it is not shown. Ask again or rephrase.',
  },
};

export function describeRefusal(reason: RefusalReason | null): Refusal {
  if (reason && REFUSALS[reason]) return REFUSALS[reason];
  return {
    title: 'No answer',
    message: reason
      ? `The backend declined to answer (${reason}).`
      : 'The backend did not return an answer.',
  };
}

export interface AskErrorMessage {
  title: string;
  message: string;
  needsLogin: boolean;
  notIndexed: boolean;
}

/** Every error POST /ask can return, each with its own wording. */
export function describeAskError(error: unknown): AskErrorMessage {
  const base = { needsLogin: false, notIndexed: false };
  if (error instanceof ApiError) {
    if (error.code === 'unauthorized' || error.status === 401)
      return {
        ...base,
        needsLogin: true,
        title: 'Sign-in required',
        message: 'Sign in to ask questions. Your session may have expired.',
      };
    if (error.code === 'knowledge_not_indexed')
      return {
        ...base,
        notIndexed: true,
        title: 'The documents are not indexed',
        message: 'Index the project documents first (Reindex), then ask again.',
      };
    if (error.code === 'invalid_question')
      return {
        ...base,
        title: 'Invalid question',
        message: error.detail || `Enter a question of 1 to ${MAX_QUESTION_LENGTH} characters.`,
      };
    if (error.code === 'not_found')
      return {
        ...base,
        title: 'Project not found',
        message: error.detail || 'Select another project.',
      };
    const llm = ERROR_MESSAGES[error.code];
    if (llm) return { ...base, title: llm.title, message: llm.message };
  }
  return { ...base, ...describeError(error) };
}

export function describeReindexError(error: unknown): AskErrorMessage {
  const base = { needsLogin: false, notIndexed: false };
  if (error instanceof ApiError) {
    if (error.code === 'unauthorized' || error.status === 401)
      return {
        ...base,
        needsLogin: true,
        title: 'Sign-in required',
        message: 'Sign in to reindex the documents.',
      };
    if (error.code === 'reindex_in_progress')
      return {
        ...base,
        title: 'Reindex already running',
        message: 'Another reindex is in progress. Wait for it to finish, then check the status.',
      };
    if (error.code === 'no_documents')
      return {
        ...base,
        title: 'No documents to index',
        message: 'There are no Markdown documents in the docs folder on the backend.',
      };
    const llm = ERROR_MESSAGES[error.code];
    if (llm)
      return {
        ...base,
        title: `Embedding failed: ${llm.title}`,
        message: `${llm.message} Nothing was changed.`,
      };
  }
  return { ...base, ...describeError(error) };
}

export const formatCost = (usd: number) =>
  usd > 0 && usd < 0.0001 ? '< $0.0001' : `$${usd.toFixed(4)}`;
