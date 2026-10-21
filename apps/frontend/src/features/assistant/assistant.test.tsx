import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { act, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  json,
  makeChangeSet,
  makeDiff,
  makeSession,
  mockFetch,
  problem,
  project,
  renderAt,
} from '@/test/utils';
import { AssistantPanel } from './AssistantPanel';
import type { CreateHub, HubHandlers } from './hub';
import { ERROR_CODES, ERROR_MESSAGES, describeErrorCode, describeOutcome } from './messages';
import { RunView } from './RunView';
import { initialRunState, liveBudgets, runReducer } from './runState';
import type { AgentRun, AgentRunStatus, AgentStep } from './types';
import { DEMO_INSTRUCTION } from './types';
import { useAgentRun } from './useAgentRun';

afterEach(() => vi.restoreAllMocks());

const step = (seq: number, kind: AgentStep['kind'], payload: unknown, tokens = 0): AgentStep => ({
  seq,
  kind,
  payload,
  tokens,
  latencyMs: 0,
  createdAt: '2026-10-20T10:00:00Z',
});

const run = (over: Partial<AgentRun> = {}): AgentRun => ({
  id: 5,
  projectId: 1,
  message: 'Add alarms',
  status: 'running',
  phase: 'planning',
  createdBy: 'alice',
  model: 'gemini-test',
  promptVersion: 'p1',
  startedAt: '2026-10-20T10:00:00Z',
  finishedAt: null,
  toolCalls: 0,
  maxToolCalls: 12,
  elapsedMs: 0,
  tokens: { prompt: 0, candidate: 0, thought: 0, total: 0 },
  maxTotalTokens: 150000,
  changeSetId: null,
  resultKind: null,
  resultMessage: null,
  errorCode: null,
  errorMessage: null,
  ...over,
});

const plan = step(1, 'plan', {
  decision: 'proceed',
  title: 'ME-EXH alarms',
  message: '',
  steps: ['Find the sensors', 'Set the H limit'],
  affected_scope: '8 tags',
  risks: ['SIL tags'],
  model: 'gemini-test',
});
const call = step(2, 'tool_call', { name: 'search_tags', args: { query: 'ME-EXH' } }, 900);
const result = step(3, 'tool_result', { name: 'search_tags', ok: true, response: { count: 8 } });

/** A hub the test drives by hand. */
function fakeHub(initial: AgentRun) {
  let handlers!: HubHandlers;
  const stop = vi.fn(async () => undefined);
  const join = vi.fn(async () => initial);
  const createHub: CreateHub = async (h) => {
    handlers = h;
    return { join, stop };
  };
  return { createHub, join, stop, emit: () => handlers };
}

const doneEvent = (status: AgentRunStatus, over = {}) => ({
  runId: 5,
  changeSetId: null,
  status,
  resultKind: null,
  resultMessage: null,
  errorCode: null,
  errorMessage: null,
  ...over,
});

describe('run state from a scripted sequence of hub events', () => {
  it('de-duplicates steps by seq across the snapshot and live events', () => {
    let s = runReducer(initialRunState, {
      type: 'snapshot',
      run: run({ steps: [plan, call] }),
    });
    s = runReducer(s, { type: 'step', event: { ...call, runId: 5 } }); // replay of a snapshot step
    s = runReducer(s, { type: 'step', event: { ...result, runId: 5 } });
    s = runReducer(s, { type: 'step', event: { ...result, runId: 5 } }); // duplicate live event
    s = runReducer(s, { type: 'step', event: { ...plan, runId: 5 } });
    expect(s.steps.map((x) => x.seq)).toEqual([1, 2, 3]);
  });

  it('orders out-of-order steps and tracks budgets from the steps', () => {
    let s = runReducer(initialRunState, { type: 'snapshot', run: run() });
    s = runReducer(s, { type: 'step', event: { ...result, runId: 5 } });
    s = runReducer(s, { type: 'step', event: { ...call, runId: 5 } });
    expect(s.steps.map((x) => x.seq)).toEqual([2, 3]);
    expect(liveBudgets(s)).toMatchObject({ toolCalls: 1, tokens: 900, maxToolCalls: 12 });
  });

  it('does not count finish_task as a tool call', () => {
    const s = runReducer(initialRunState, {
      type: 'snapshot',
      run: run({ steps: [step(1, 'tool_call', { name: 'finish_task', args: {} })] }),
    });
    expect(liveBudgets(s).toolCalls).toBe(0);
  });

  it('status then done close the run; a stale running snapshot cannot reopen it', () => {
    let s = runReducer(initialRunState, { type: 'snapshot', run: run() });
    s = runReducer(s, { type: 'status', event: { runId: 5, status: 'running', phase: 'acting' } });
    expect(s.run?.phase).toBe('acting');
    s = runReducer(s, {
      type: 'done',
      event: doneEvent('awaiting_review', { changeSetId: 9, resultKind: 'draft' }),
    });
    expect(s.run).toMatchObject({ status: 'awaiting_review', changeSetId: 9, phase: null });
    s = runReducer(s, { type: 'snapshot', run: run({ status: 'running' }) });
    s = runReducer(s, { type: 'status', event: { runId: 5, status: 'running', phase: 'acting' } });
    expect(s.run?.status).toBe('awaiting_review');
  });
});

describe('useAgentRun transports', () => {
  it('joins over the hub, applies events, and ignores other runs', async () => {
    const hub = fakeHub(run({ steps: [plan] }));
    const { result, unmount } = renderHook(() =>
      useAgentRun(5, true, { createHub: hub.createHub }),
    );
    await waitFor(() => expect(result.current.transport).toBe('live'));
    expect(hub.join).toHaveBeenCalledWith(5);
    act(() => {
      hub.emit().step({ ...call, runId: 5 });
      hub.emit().step({ ...call, runId: 5 });
      hub.emit().step({ ...result.current.state.steps[0], seq: 99, runId: 77 });
    });
    expect(result.current.state.steps.map((s) => s.seq)).toEqual([1, 2]);
    unmount();
    expect(hub.stop).toHaveBeenCalled();
  });

  it('falls back to polling GET /api/agent/runs/{id} when the hub cannot connect', async () => {
    let polls = 0;
    mockFetch({
      'GET /api/agent/runs/5': () => {
        polls += 1;
        return json(
          polls < 2
            ? run({ steps: [plan] })
            : run({ status: 'awaiting_review', changeSetId: 9, steps: [plan, call] }),
        );
      },
    });
    const createHub: CreateHub = () => Promise.reject(new Error('negotiate failed'));
    const { result } = renderHook(() => useAgentRun(5, true, { createHub, pollMs: 10 }));
    await waitFor(() => expect(result.current.state.run?.status).toBe('awaiting_review'));
    expect(result.current.transport).toBe('polling');
    expect(result.current.state.steps.map((s) => s.seq)).toEqual([1, 2]);
    const seen = polls;
    await new Promise((r) => setTimeout(r, 60));
    expect(polls).toBe(seen); // stops once terminal
  });

  it('polling checks the HTTP status: a proxied 502 is an error, not a run', async () => {
    mockFetch({
      'GET /api/agent/runs/5': () =>
        new Response('', { status: 502, headers: { 'content-type': 'text/plain' } }),
    });
    const createHub: CreateHub = () => Promise.reject(new Error('no ws'));
    const { result } = renderHook(() => useAgentRun(5, true, { createHub, pollMs: 10 }));
    await waitFor(() => expect(result.current.error).not.toBeNull());
    expect(result.current.state.run).toBeNull();
  });

  it('falls back to polling when the hub closes for good mid-run', async () => {
    const hub = fakeHub(run());
    mockFetch({
      'GET /api/agent/runs/5': () => json(run({ status: 'failed', errorCode: 'llm_timeout' })),
    });
    const { result } = renderHook(() =>
      useAgentRun(5, true, { createHub: hub.createHub, pollMs: 10 }),
    );
    await waitFor(() => expect(result.current.transport).toBe('live'));
    act(() => hub.emit().closed());
    await waitFor(() => expect(result.current.state.run?.status).toBe('failed'));
    expect(result.current.transport).toBe('polling');
  });

  it('a finished run from history is fetched once, without a hub', async () => {
    const createHub = vi.fn();
    mockFetch({
      'GET /api/agent/runs/5': () => json(run({ status: 'refused', resultMessage: 'No.' })),
    });
    const { result } = renderHook(() => useAgentRun(5, false, { createHub }));
    await waitFor(() => expect(result.current.state.run?.status).toBe('refused'));
    expect(createHub).not.toHaveBeenCalled();
  });
});

describe('terminal statuses render a specific message', () => {
  const cases: [AgentRunStatus, Partial<AgentRun>, RegExp][] = [
    [
      'awaiting_review',
      { changeSetId: 9, resultMessage: 'Added 8 alarms.' },
      /draft ready for review/i,
    ],
    ['needs_clarification', { resultMessage: 'Which engine?' }, /needs more information/i],
    [
      'refused',
      { resultKind: 'refusal', resultMessage: 'Not allowed on this vessel.' },
      /declined this request/i,
    ],
    ['refused', { resultKind: 'no_changes', resultMessage: 'Already set.' }, /nothing to change/i],
    ['budget_exceeded', { errorCode: 'budget_tool_calls' }, /tool-call budget used up/i],
    ['failed', { errorCode: 'llm_rate_limited' }, /model is rate limited/i],
    ['cancelled', {}, /run cancelled/i],
  ];

  it.each(cases)('%s', (status, over, title) => {
    const outcome = describeOutcome(run({ status, ...over }));
    expect(outcome?.title).toMatch(title);
    expect(outcome?.message.length).toBeGreaterThan(20);
  });

  it('shows the question in the clarification and the reason in the refusal', () => {
    expect(
      describeOutcome(run({ status: 'needs_clarification', resultMessage: 'Which engine?' }))
        ?.message,
    ).toContain('Which engine?');
    expect(
      describeOutcome(
        run({ status: 'refused', resultKind: 'refusal', resultMessage: 'SIL tags are locked.' }),
      )?.message,
    ).toContain('SIL tags are locked.');
  });

  it('a budget stop and a cancel say whether a partial draft was kept', () => {
    expect(
      describeOutcome(
        run({ status: 'budget_exceeded', errorCode: 'budget_tokens', changeSetId: 3 }),
      )?.message,
    ).toMatch(/partial draft was kept/i);
    expect(
      describeOutcome(run({ status: 'budget_exceeded', errorCode: 'budget_tokens' }))?.message,
    ).toMatch(/no draft was created/i);
    expect(describeOutcome(run({ status: 'cancelled', changeSetId: 3 }))?.message).toMatch(
      /partial draft/i,
    );
  });

  it('running has no outcome', () => {
    expect(describeOutcome(run())).toBeNull();
  });
});

describe('error codes', () => {
  const all = [
    'budget_tool_calls',
    'budget_wall_clock',
    'budget_tokens',
    'llm_rate_limited',
    'llm_auth_failed',
    'llm_safety_blocked',
    'llm_malformed_output',
    'llm_timeout',
    'llm_unavailable',
    'llm_truncated',
    'llm_bad_request',
    'llm_not_configured',
    'internal_error',
    'server_restarted',
  ];

  it('covers every documented code', () => {
    expect([...ERROR_CODES].sort()).toEqual([...all].sort());
  });

  it('maps each code to a distinct title and message', () => {
    const titles = new Set(all.map((c) => describeErrorCode(c).title));
    const messages = new Set(all.map((c) => describeErrorCode(c).message));
    expect(titles.size).toBe(all.length);
    expect(messages.size).toBe(all.length);
  });

  it('an unknown code is shown with the code itself, not a generic message', () => {
    expect(describeErrorCode('brand_new', 'Boom').title).toContain('brand_new');
  });
});

describe('RunView', () => {
  const setup = (initial: AgentRun, extra: Record<string, () => Response> = {}) => {
    const hub = fakeHub(initial);
    mockFetch({
      'GET /api/auth/me': () => json(makeSession('alice', ['admin'])),
      'GET /api/projects': () => json([project]),
      ...extra,
    });
    renderAt('/', '/', <RunView runId={5} live createHub={hub.createHub} />);
    return hub;
  };

  it('streams plan, tool rows and verify, then the draft with a review link and no apply control', async () => {
    const cs = makeChangeSet({ id: 9, status: 'validated', requiresSecondApprover: true });
    const hub = setup(run({ phase: 'planning' }), {
      'GET /api/agent/runs/5': () =>
        json(run({ status: 'awaiting_review', changeSetId: 9, toolCalls: 1, steps: [] })),
      'GET /api/change-sets/9/diff': () => json(makeDiff(cs)),
    });
    await screen.findByText(/Planning/);
    act(() => {
      hub.emit().step({ ...plan, runId: 5 });
      hub.emit().step({ ...call, runId: 5 });
      hub.emit().step({ ...result, runId: 5 });
      hub.emit().step({
        ...step(4, 'verify', {
          round: 0,
          valid: false,
          error_count: 1,
          requires_second_approver: true,
          violations: [
            {
              code: 'LIMIT_ORDER',
              severity: 'error',
              tag_name: 'T1',
              field: 'H',
              message: 'H must exceed L',
            },
          ],
        }),
        runId: 5,
      });
    });
    const checklist = screen.getByRole('list', { name: /plan checklist/i });
    expect(within(checklist).getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getAllByText('search_tags', { selector: 'span.font-mono' })).toHaveLength(2);
    expect(screen.getByText(/1 rule error/i)).toBeInTheDocument();
    expect(screen.getByText('H must exceed L')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /cancel/i })).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Tool calls' })).toHaveAttribute(
      'aria-valuenow',
      '1',
    );

    act(() =>
      hub.emit().done(
        doneEvent('awaiting_review', {
          changeSetId: 9,
          resultKind: 'draft',
          resultMessage: 'Added 8 alarms.',
        }),
      ),
    );
    expect(await screen.findByText(/draft ready for review/i)).toBeInTheDocument();
    expect(await screen.findByText('ME-EXH-TT01')).toBeInTheDocument();
    const link = screen.getByRole('link', { name: /review & approve/i });
    expect(link).toHaveAttribute('href', '/change-sets/9');
    expect(screen.queryByRole('button', { name: /^(approve|apply)/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /cancel/i })).toBeNull();
  });

  it('a failed run shows its specific error message', async () => {
    const hub = setup(run({ phase: 'acting' }), { 'GET /api/agent/runs/5': () => json(run()) });
    await screen.findByText(/Working/);
    act(() =>
      hub
        .emit()
        .done(doneEvent('failed', { errorCode: 'llm_safety_blocked', errorMessage: 'blocked' })),
    );
    const outcome = await screen.findByTestId('run-outcome');
    expect(outcome).toHaveAttribute('data-error-code', 'llm_safety_blocked');
    expect(outcome).toHaveTextContent(/safety filter/i);
  });

  it('a failed run shows the friendly message once, with the raw provider text collapsed under Details', async () => {
    const raw = 'RESOURCE_EXHAUSTED: Quota exceeded for metric generate_content_free_tier';
    const friendly = ERROR_MESSAGES.llm_rate_limited.message;
    const failed = run({
      status: 'failed',
      phase: null,
      errorCode: 'llm_rate_limited',
      errorMessage: raw,
      steps: [plan, step(2, 'error', { code: 'llm_rate_limited', message: raw })],
    });
    setup(failed, { 'GET /api/agent/runs/5': () => json(failed) });
    const outcome = await screen.findByTestId('run-outcome');
    expect(screen.getAllByText(friendly)).toHaveLength(1);
    expect(outcome).toHaveTextContent('Model is rate limited');
    const rawNodes = screen.getAllByText(raw);
    expect(rawNodes.length).toBeGreaterThan(0);
    for (const node of rawNodes) {
      expect(node.closest('details')).not.toBeNull();
      expect(node).not.toBeVisible();
    }
    expect(within(outcome).getByText('Details')).toBeInTheDocument();
    await userEvent.setup().click(within(outcome).getByText('Details'));
    expect(within(outcome).getByText(raw)).toBeVisible();
  });

  it('an unknown error code still shows its raw text (there is no friendly text to prefer)', async () => {
    const failed = run({
      status: 'failed',
      phase: null,
      errorCode: 'weird_code',
      errorMessage: 'something odd happened',
      steps: [],
    });
    setup(failed, { 'GET /api/agent/runs/5': () => json(failed) });
    const outcome = await screen.findByTestId('run-outcome');
    expect(outcome).toHaveTextContent('something odd happened');
    expect(outcome.querySelector('details')).toBeNull();
  });

  it('Cancel posts to the cancel endpoint', async () => {
    const hub = setup(run(), {
      'GET /api/agent/runs/5': () => json(run()),
      'POST /api/agent/runs/5/cancel': () => json(run(), 202),
    });
    void hub;
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: /cancel/i }));
    await waitFor(() =>
      expect(
        vi
          .mocked(globalThis.fetch)
          .mock.calls.some(([u, i]) => String(u).endsWith('/cancel') && i?.method === 'POST'),
      ).toBe(true),
    );
  });
});

const typeable = async () => {
  const box = await screen.findByLabelText('Describe a change');
  await waitFor(() => expect(box).toBeEnabled());
  return box;
};

describe('AssistantPanel', () => {
  const base = (session: ReturnType<typeof makeSession> | null) => ({
    'GET /api/auth/me': () =>
      session ? json(session) : problem(401, 'unauthorized', 'Authentication required'),
    'GET /api/projects': () => json([project]),
    'GET /api/projects/1/agent/runs': () =>
      json({
        items: [
          run({
            id: 4,
            status: 'refused',
            message: 'Old request',
            resultKind: 'refusal',
            resultMessage: 'No.',
          }),
        ],
        total: 1,
        page: 1,
        pageSize: 20,
      }),
  });

  it('lists the project history and fills the input from the example chip', async () => {
    mockFetch(base(makeSession('alice', ['admin'])));
    renderAt('/', '/', <AssistantPanel />);
    expect(await screen.findByText('Old request')).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(
      screen.getByRole('button', { name: new RegExp(DEMO_INSTRUCTION.slice(0, 30)) }),
    );
    expect(screen.getByLabelText('Describe a change')).toHaveValue(DEMO_INSTRUCTION);
  });

  it('starts a run with the typed message', async () => {
    const hub = fakeHub(run());
    const m = mockFetch({
      ...base(makeSession('alice', ['admin'])),
      'POST /api/projects/1/agent/runs': () => json({ runId: 5 }, 202),
      'GET /api/agent/runs/5': () => json(run()),
    });
    renderAt('/', '/', <AssistantPanel createHub={hub.createHub} />);
    const user = userEvent.setup();
    await user.type(await typeable(), 'Add alarms');
    await waitFor(() => expect(screen.getByRole('button', { name: /send/i })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: /send/i }));
    await screen.findByRole('region', { name: 'Run 5' });
    expect(m.calls.find((c) => c.method === 'POST')?.body).toEqual({ message: 'Add alarms' });
  });

  it('409 agent_run_in_progress explains it and offers to watch the running run', async () => {
    const hub = fakeHub(run({ id: 3, message: 'Other run' }));
    mockFetch({
      ...base(makeSession('alice', ['admin'])),
      'POST /api/projects/1/agent/runs': () =>
        problem(409, 'agent_run_in_progress', 'An agent run is already in progress', { runId: 3 }),
      'GET /api/agent/runs/3': () => json(run({ id: 3, message: 'Other run' })),
    });
    renderAt('/', '/', <AssistantPanel createHub={hub.createHub} />);
    const user = userEvent.setup();
    await user.type(await typeable(), 'Another');
    await waitFor(() => expect(screen.getByRole('button', { name: /send/i })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: /send/i }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/already in progress/i);
    await user.click(within(alert).getByRole('button', { name: /watch run #3/i }));
    await screen.findByRole('region', { name: 'Run 3' });
    expect(hub.join).toHaveBeenCalledWith(3);
  });

  it('signed out: asks to sign in and cannot send', async () => {
    mockFetch(base(null));
    renderAt('/', '/', <AssistantPanel />);
    expect(
      await screen.findByRole('button', { name: /sign in to use the assistant/i }),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Describe a change')).toBeDisabled();
    expect(screen.queryByRole('button', { name: /^send/i })).toBeNull();
  });

  it('401 on start (expired session) opens the sign-in dialog', async () => {
    mockFetch({
      ...base(makeSession('alice', ['admin'])),
      'POST /api/projects/1/agent/runs': () =>
        problem(401, 'unauthorized', 'Authentication required'),
    });
    renderAt('/', '/', <AssistantPanel />);
    const user = userEvent.setup();
    await user.type(await typeable(), 'Add');
    await waitFor(() => expect(screen.getByRole('button', { name: /send/i })).toBeEnabled());
    await user.click(screen.getByRole('button', { name: /send/i }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(screen.getByRole('alert', { hidden: true })).toHaveTextContent(/sign-in required/i);
  });
});

describe('safety: the assistant cannot approve or apply', () => {
  const dir = __dirname;
  const files = readdirSync(dir).filter((f) => /\.(ts|tsx)$/.test(f) && !f.includes('.test.'));

  it('has source files to check', () => {
    expect(files).toEqual(
      expect.arrayContaining(['api.ts', 'AssistantPanel.tsx', 'RunResult.tsx']),
    );
  });

  it.each(files)('%s references no approve/apply endpoint or helper', (file) => {
    const src = readFileSync(join(dir, file), 'utf8');
    expect(src).not.toMatch(/approveChangeSet|applyChangeSet|rejectChangeSet|validateChangeSet/);
    expect(src).not.toMatch(/\/(approve|apply|reject)\b/);
    expect(src).not.toMatch(/method:\s*'(PUT|DELETE|PATCH)'/);
  });

  it('only imports the read helpers of the change-set endpoints', () => {
    for (const file of files) {
      const src = readFileSync(join(dir, file), 'utf8');
      for (const m of src.matchAll(/import\s*\{([^}]*)\}\s*from\s*'@\/lib\/api\/endpoints'/g)) {
        expect(
          m[1]
            .split(',')
            .map((s) => s.trim())
            .filter(Boolean),
        ).toEqual(['fetchDiff']);
      }
    }
  });
});
