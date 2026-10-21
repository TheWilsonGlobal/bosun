import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { json, makeSession, mockFetch, problem, project, renderAt } from '@/test/utils';
import { AssistantPanel } from '../AssistantPanel';
import { ERROR_CODES } from '../messages';
import {
  describeAskError,
  formatWait,
  describeRefusal,
  describeReindexError,
  EXAMPLE_QUESTION,
  splitAnswer,
  type AskResponse,
  type KnowledgeStatus,
} from './askLogic';
import { ApiError, parseRetryAfter } from '@/lib/api/client';

afterEach(() => vi.restoreAllMocks());

const cite = { source: 'alarm-philosophy', section: 'A4 Delays', page: 2 };
const other = { source: 'cause-and-effect', section: 'C1 Trips', page: 1 };
const marker = (c: typeof cite) => `[${c.source} §${c.section} p.${c.page}]`;

const knowledge = (over: Partial<KnowledgeStatus> = {}): KnowledgeStatus => ({
  indexed: true,
  documents: 3,
  chunks: 15,
  embeddedChunks: 15,
  documentsOnDisk: 3,
  embeddingModel: 'gemini-embedding-2',
  dimensions: 768,
  lastIndexedAt: '2026-10-21T08:00:00Z',
  sources: [{ source: 'alarm-philosophy', chunks: 7, pages: 3 }],
  ...over,
});

const answer = (over: Partial<AskResponse> = {}): AskResponse => ({
  question: EXAMPLE_QUESTION,
  answered: true,
  answer: `A 10 s delay filters transient spikes ${marker(cite)}. Unlisted ${marker(other)} and <b>bold</b> [note].`,
  citations: [cite],
  refusalReason: null,
  droppedCitations: 0,
  unsupportedSentencesRemoved: 0,
  retrieved: [
    { ...cite, score: 0.0321, cosine: 0.81, aboveThreshold: true, usedForAnswer: true, flagged: false },
    { ...other, score: 0.0102, cosine: 0.52, aboveThreshold: false, usedForAnswer: false, flagged: false },
  ],
  model: 'gemini-test',
  promptVersion: 'rag@1',
  usage: {
    promptTokens: 900,
    candidateTokens: 80,
    thoughtTokens: 0,
    totalTokens: 980,
    llmCalls: 1,
    estimatedCostUsd: 0.00042,
  },
  latencyMs: 1830,
  ...over,
});

function mount(
  opts: { session?: boolean; knowledge?: KnowledgeStatus | Response } = {},
  routes: Parameters<typeof mockFetch>[0] = {},
) {
  const k = opts.knowledge ?? knowledge();
  const m = mockFetch({
    'GET /api/auth/me': () =>
      opts.session === false
        ? problem(401, 'unauthorized', 'no')
        : json(makeSession('alice', ['admin'])),
    'GET /api/projects': () => json([project]),
    'GET /api/projects/1/knowledge': () => (k instanceof Response ? k : json(k)),
    'GET /api/projects/1/agent/runs': () => json({ items: [], total: 0, page: 1, pageSize: 20 }),
    ...routes,
  });
  renderAt('/', '/', <AssistantPanel />);
  return m;
}

async function openAsk(user = userEvent.setup()) {
  await user.click(await screen.findByRole('tab', { name: 'Ask' }));
  return user;
}

describe('pure helpers', () => {
  it('turns only markers that match a returned citation into chips; everything else stays text', () => {
    const segs = splitAnswer(`x ${marker(cite)} y ${marker(other)} z [note]`, [cite]);
    expect(segs.map((s) => s.kind)).toEqual(['text', 'citation', 'text']);
    expect(segs[1]).toMatchObject({ kind: 'citation', citation: cite });
    expect(segs[2]).toMatchObject({ text: expect.stringContaining(marker(other)) });
    expect(segs.map((s) => s.text).join('')).toBe(`x ${marker(cite)} y ${marker(other)} z [note]`);
  });
  it('a page number or section mismatch is not a citation', () => {
    expect(splitAnswer(marker({ ...cite, page: 3 }), [cite])).toEqual([
      { kind: 'text', text: marker({ ...cite, page: 3 }) },
    ]);
  });
  it('has a different message for each refusal reason', () => {
    const titles = (['below_threshold', 'model_declined', 'no_valid_citation'] as const).map(
      (r) => describeRefusal(r).title,
    );
    expect(new Set(titles).size).toBe(3);
    expect(titles[0]).toBe('Not in the project documents');
  });
  it('maps every ask error to its own message, reusing the assistant llm_* wording', () => {
    const e = (status: number, code: string) => describeAskError(new ApiError(status, code, ''));
    expect(e(409, 'knowledge_not_indexed')).toMatchObject({ notIndexed: true });
    expect(e(401, 'unauthorized')).toMatchObject({ needsLogin: true });
    expect(e(400, 'invalid_question').title).toBe('Invalid question');
    expect(e(404, 'not_found').title).toBe('Project not found');
    for (const code of ERROR_CODES.filter((c) => c.startsWith('llm_'))) {
      expect(e(502, code).title).toBeTruthy();
      expect(e(502, code).title).not.toMatch(/^Request failed/);
    }
    expect(e(429, 'rate_limited').title).toBe('Too many questions');
    expect(e(429, 'rate_limited').message).not.toMatch(/Wait \d/);
    const denied = describeAskError(
      new ApiError(502, 'llm_auth_failed', 'Your project has been denied access.'),
    );
    expect(denied.detail).toBe('Your project has been denied access.');
    expect(e(502, 'llm_auth_failed').detail).toBeUndefined();
    expect(parseRetryAfter('30')).toBe(30);
    expect(parseRetryAfter('Wed, 21 Oct 2026 10:00:45 GMT', Date.parse('2026-10-21T10:00:00Z'))).toBe(45);
    expect(parseRetryAfter('soon')).toBeNull();
    expect(parseRetryAfter(null)).toBeNull();
    expect(formatWait(1)).toBe('1 second');
    expect(formatWait(120)).toBe('2 minutes');
    expect(describeReindexError(new ApiError(409, 'reindex_in_progress', '')).title).toBe(
      'Reindex already running',
    );
    expect(describeReindexError(new ApiError(422, 'no_documents', '')).title).toBe(
      'No documents to index',
    );
  });
});

describe('mode switch', () => {
  it('has Change (default, the agent) and Ask tabs', async () => {
    mount();
    const change = await screen.findByRole('tab', { name: 'Change' });
    expect(change).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByLabelText('Describe a change')).toBeInTheDocument();
    await openAsk();
    expect(screen.getByRole('tab', { name: 'Ask' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByLabelText('Describe a change')).toBeNull();
    expect(screen.getByLabelText('Ask the project documents')).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('tab', { name: 'Change' }));
    expect(screen.getByLabelText('Describe a change')).toBeInTheDocument();
  });
});

describe('Ask mode', () => {
  it('shows the knowledge status line and the example chip fills the box', async () => {
    mount();
    const user = await openAsk();
    const line = await screen.findByTestId('knowledge-status');
    expect(line).toHaveTextContent(
      'Indexed: 15 chunks from 3 documents, gemini-embedding-2 (768 dims)',
    );
    await user.click(screen.getByRole('button', { name: EXAMPLE_QUESTION }));
    expect(screen.getByLabelText('Ask the project documents')).toHaveValue(EXAMPLE_QUESTION);
  });

  it('answers as plain text with citation chips only for returned citations; HTML is not rendered', async () => {
    const { calls } = mount({}, { 'POST /api/projects/1/ask': () => json(answer()) });
    const user = await openAsk();
    await user.click(await screen.findByRole('button', { name: EXAMPLE_QUESTION }));
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    const text = await screen.findByTestId('ask-answer');
    expect(calls.find((c) => c.method === 'POST' && c.path.endsWith('/ask'))?.body).toEqual({
      question: EXAMPLE_QUESTION,
    });
    const chips = within(text).getAllByTestId('citation-chip');
    expect(chips).toHaveLength(1);
    expect(chips[0]).toHaveTextContent('alarm-philosophy §A4 Delays p.2');
    // the unlisted marker, the tag-looking text and [note] are literal text
    expect(text).toHaveTextContent(marker(other));
    expect(text).toHaveTextContent('<b>bold</b>');
    expect(text.querySelector('b')).toBeNull();
    expect(text).toHaveTextContent('[note]');
  });

  it('lists retrieved sources collapsed, with score and used / not used, and an est. cost', async () => {
    mount({}, { 'POST /api/projects/1/ask': () => json(answer()) });
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'why?');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    const details = await screen.findByTestId('sources-retrieved');
    expect(details.tagName).toBe('DETAILS');
    expect(details).not.toHaveAttribute('open');
    expect(details).toHaveTextContent('Sources retrieved (2, 1 used)');
    expect(within(details).getByText('used')).toBeInTheDocument();
    expect(within(details).getByText('not used')).toBeInTheDocument();
    expect(details).toHaveTextContent('score 0.032');
    expect(details).toHaveTextContent('below relevance threshold');
    expect(screen.getByTestId('ask-usage')).toHaveTextContent('est. $0.0004');
    expect(screen.getByTestId('ask-usage')).toHaveTextContent('980 tokens');
  });

  it('notes dropped citations', async () => {
    mount({}, { 'POST /api/projects/1/ask': () => json(answer({ droppedCitations: 2 })) });
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'why?');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    expect(await screen.findByTestId('dropped-citations')).toHaveTextContent(
      '2 citations given by the model did not match a retrieved passage and were removed',
    );
  });

  it('notes removed unsupported sentences only when there are some', async () => {
    let n = 0;
    mount(
      {},
      {
        'POST /api/projects/1/ask': () =>
          json(answer({ unsupportedSentencesRemoved: ++n === 1 ? 0 : 2 })),
      },
    );
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'why?');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    await screen.findByTestId('ask-answer');
    expect(screen.queryByTestId('unsupported-removed')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    expect(await screen.findByTestId('unsupported-removed')).toHaveTextContent(
      '2 sentences of the model',
    );
  });

  it('marks a flagged source in the Sources list and only that one', async () => {
    const base = answer();
    mount(
      {},
      {
        'POST /api/projects/1/ask': () =>
          json(
            answer({
              retrieved: [{ ...base.retrieved[0], flagged: true }, base.retrieved[1]],
            }),
          ),
      },
    );
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'why?');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    const details = await screen.findByTestId('sources-retrieved');
    const marks = within(details).getAllByTestId('flagged-source');
    expect(marks).toHaveLength(1);
    expect(marks[0]).toHaveTextContent('flagged as instruction-like');
  });

  it('429 rate_limited shows the wait from Retry-After', async () => {
    mount(
      {},
      {
        'POST /api/projects/1/ask': () => {
          const body = { status: 429, code: 'rate_limited', detail: 'Too many', title: 'Too many' };
          return new Response(JSON.stringify(body), {
            status: 429,
            headers: { 'content-type': 'application/problem+json', 'retry-after': '17' },
          });
        },
      },
    );
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    const box = await screen.findByTestId('ask-error');
    expect(box).toHaveTextContent('Too many questions');
    expect(box).toHaveTextContent('Wait 17 seconds');
  });

  it('keeps focus on the textarea while asking (readOnly + aria-busy, not disabled)', async () => {
    let release: (r: Response) => void = () => {};
    mount(
      {},
      {
        'POST /api/projects/1/ask': () =>
          new Promise<Response>((resolve) => {
            release = resolve;
          }),
      },
    );
    const user = await openAsk();
    const box = screen.getByLabelText('Ask the project documents');
    await user.type(box, 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    await screen.findByText('Searching the documents...');
    expect(box).not.toBeDisabled();
    expect(box).toHaveAttribute('readonly');
    expect(box).toHaveAttribute('aria-busy', 'true');
    expect(box).toHaveFocus();
    release(json(answer()));
    await screen.findByTestId('ask-answer');
    expect(box).not.toHaveAttribute('readonly');
  });

  it('the answer region is a polite live region and spinners honour reduced motion', async () => {
    mount({}, { 'POST /api/projects/1/ask': () => json(answer()) });
    const user = await openAsk();
    expect(screen.getByTestId('ask-live')).toHaveAttribute('aria-live', 'polite');
    await user.type(screen.getByLabelText('Ask the project documents'), 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    expect(within(screen.getByTestId('ask-live')).getByTestId('ask-answer')).toBeInTheDocument();
  });

  it.each([
    ['below_threshold', 'Not in the project documents'],
    ['model_declined', 'The assistant declined to answer'],
    ['no_valid_citation', 'Answer withheld: no verifiable source'],
  ] as const)('refusal %s has its own wording and shows no answer text', async (reason, title) => {
    mount(
      {},
      {
        'POST /api/projects/1/ask': () =>
          json(
            answer({
              answered: false,
              answer: 'SHOULD NOT BE SHOWN',
              citations: [],
              refusalReason: reason,
              retrieved: [],
            }),
          ),
      },
    );
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    const box = await screen.findByTestId('ask-refusal');
    expect(box).toHaveAttribute('data-reason', reason);
    expect(box).toHaveTextContent(title);
    expect(screen.queryByTestId('ask-answer')).toBeNull();
    expect(screen.queryByText('SHOULD NOT BE SHOWN')).toBeNull();
    expect(screen.queryByTestId('sources-retrieved')).toBeNull();
  });

  it('409 knowledge_not_indexed shows the not-indexed state, not a generic error', async () => {
    mount(
      {
        knowledge: knowledge({
          indexed: false,
          chunks: 0,
          documents: 0,
          embeddedChunks: 0,
          embeddingModel: null,
          dimensions: null,
          lastIndexedAt: null,
        }),
      },
      { 'POST /api/projects/1/ask': () => problem(409, 'knowledge_not_indexed', 'not indexed') },
    );
    const user = await openAsk();
    expect(await screen.findByTestId('not-indexed')).toBeInTheDocument();
    expect(screen.getByTestId('knowledge-status')).toHaveTextContent(
      'Not indexed (3 documents on disk)',
    );
    expect(screen.getByRole('button', { name: 'Index documents' })).toBeInTheDocument();
    await user.type(screen.getByLabelText('Ask the project documents'), 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    expect(await screen.findByTestId('ask-error')).toHaveTextContent(
      'The documents are not indexed',
    );
  });

  it('400 invalid_question and an llm error show their specific text', async () => {
    let n = 0;
    mount(
      {},
      {
        'POST /api/projects/1/ask': () =>
          ++n === 1
            ? problem(400, 'invalid_question', 'Question must be at most 500 characters.')
            : problem(429, 'llm_rate_limited', 'quota'),
      },
    );
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    expect(await screen.findByTestId('ask-error')).toHaveTextContent(
      'Question must be at most 500',
    );
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    await waitFor(() =>
      expect(screen.getByTestId('ask-error')).toHaveTextContent('Model is rate limited'),
    );
  });

  it('shows the server detail under Details for a provider auth failure', async () => {
    mount(
      {},
      {
        'POST /api/projects/1/ask': () =>
          problem(502, 'llm_auth_failed', 'Your project has been denied access.'),
      },
    );
    const user = await openAsk();
    await user.type(screen.getByLabelText('Ask the project documents'), 'q');
    await user.click(screen.getByRole('button', { name: 'Ask' }));
    const box = await screen.findByTestId('ask-error');
    expect(box).toHaveTextContent('Model rejected the API key');
    expect(within(box).getByText('Details')).toBeInTheDocument();
    expect(box).toHaveTextContent('Your project has been denied access.');
  });

  it('limits the question to 500 characters', async () => {
    mount();
    await openAsk();
    expect(await screen.findByLabelText('Ask the project documents')).toHaveAttribute(
      'maxlength',
      '500',
    );
  });

  it('without a session: ask is disabled behind a sign-in button', async () => {
    mount({ session: false });
    const user = await openAsk();
    expect(screen.getByLabelText('Ask the project documents')).toBeDisabled();
    await user.click(screen.getByRole('button', { name: /sign in to ask/i }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });

  it('Reindex posts with no body, reports the result and refetches the status', async () => {
    let status = knowledge({ chunks: 15 });
    const { calls } = mount(
      {},
      {
        'GET /api/projects/1/knowledge': () => json(status),
        'POST /api/projects/1/knowledge/reindex': () => {
          status = knowledge({ chunks: 18 });
          return json({
            documents: 3,
            chunks: 18,
            embedded: 3,
            fromStore: 15,
            fromCache: 0,
            embeddingRequests: 1,
            promptTokens: 500,
            skipped: 0,
            model: 'gemini-embedding-2',
            dimensions: 768,
            elapsedMs: 900,
          });
        },
      },
    );
    const user = await openAsk();
    await user.click(await screen.findByRole('button', { name: 'Reindex' }));
    expect(
      await screen.findByText(/3 embedded \(15 unchanged, 0 from cache\)/),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByTestId('knowledge-status')).toHaveTextContent('18 chunks'),
    );
    const post = calls.find((c) => c.path.endsWith('/knowledge/reindex'));
    expect(post?.method).toBe('POST');
    expect(post?.body).toBeUndefined();
  });

  it('Reindex 409 reindex_in_progress and 422 no_documents each have their own message', async () => {
    let n = 0;
    mount(
      {},
      {
        'POST /api/projects/1/knowledge/reindex': () =>
          ++n === 1 ? problem(409, 'reindex_in_progress', 'x') : problem(422, 'no_documents', 'y'),
      },
    );
    const user = await openAsk();
    await user.click(await screen.findByRole('button', { name: 'Reindex' }));
    expect(await screen.findByText('Reindex already running.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Reindex' }));
    expect(await screen.findByText('No documents to index.')).toBeInTheDocument();
  });

  it('shows a specific error when the status cannot be read', async () => {
    mount({ knowledge: problem(500, 'internal_error', 'boom') });
    await openAsk();
    expect(await screen.findByText(/Server error/)).toBeInTheDocument();
  });
});
