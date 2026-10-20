import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ChangeSet, Diff, Session, Violation } from '@/lib/api/types';
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
import { ChangeSetDetailPage } from './ChangeSetDetailPage';

const err = (message: string, tagName = 'ME-EXH-TT01'): Violation => ({
  code: 'LIMIT_ORDER',
  severity: 'error',
  tagName,
  field: 'limits.H',
  message,
});

function setup(opts: {
  cs: ChangeSet;
  session: Session | null;
  diff?: Partial<Diff>;
  extra?: Record<string, () => Response>;
}) {
  const handlers = {
    'GET /api/auth/me': () =>
      opts.session ? json(opts.session) : problem(401, 'unauthorized', 'Authentication required'),
    'GET /api/projects': () => json([project]),
    [`GET /api/change-sets/${opts.cs.id}`]: () => json(opts.cs),
    [`GET /api/change-sets/${opts.cs.id}/diff`]: () => json(makeDiff(opts.cs, opts.diff)),
    ...(opts.extra ?? {}),
  };
  const mock = mockFetch(handlers);
  renderAt(`/change-sets/${opts.cs.id}`, '/change-sets/:id', <ChangeSetDetailPage />);
  return mock;
}

const button = (name: RegExp) => screen.getByRole('button', { name });

afterEach(() => vi.restoreAllMocks());

describe('change-set state machine buttons', () => {
  it('draft: only Validate and Reject are available; Approve says to validate first', async () => {
    setup({ cs: makeChangeSet({ status: 'draft' }), session: makeSession('alice', ['admin']) });
    await screen.findByRole('toolbar', { name: /change set actions/i });
    await waitFor(() => expect(button(/^validate$/i)).toBeEnabled());
    expect(button(/^reject$/i)).toBeEnabled();
    expect(button(/^approve$/i)).toBeDisabled();
    expect(button(/^apply$/i)).toBeDisabled();
    expect(screen.getByText(/validate the change set first/i)).toBeInTheDocument();
  });

  it('validated and clean: Approve enabled, Apply still disabled', async () => {
    setup({ cs: makeChangeSet({ status: 'validated' }), session: makeSession('alice', ['admin']) });
    await waitFor(() => expect(button(/^approve$/i)).toBeEnabled());
    expect(button(/^apply$/i)).toBeDisabled();
  });

  it('validated with rule errors: Approve is blocked and the violation is listed with its severity', async () => {
    setup({
      cs: makeChangeSet({ status: 'validated' }),
      session: makeSession('alice', ['admin']),
      diff: { violations: [err('H must be above L')] },
    });
    expect(await screen.findByText('H must be above L')).toBeInTheDocument();
    expect(screen.getByText(/1 error/i)).toBeInTheDocument();
    expect(button(/^approve$/i)).toBeDisabled();
    expect(screen.getByText(/fix the validation errors first/i)).toBeInTheDocument();
  });

  it('approved: Apply is the only forward action', async () => {
    setup({
      cs: makeChangeSet({ status: 'approved', approvedBy: 'alice' }),
      session: makeSession('alice', ['admin']),
    });
    await waitFor(() => expect(button(/^apply$/i)).toBeEnabled());
    expect(button(/^approve$/i)).toBeDisabled();
  });

  it('applied: everything is disabled and the resulting audit entries are shown', async () => {
    const cs = makeChangeSet({ status: 'applied', approvedBy: 'alice', appliedBy: 'alice' });
    setup({
      cs,
      session: makeSession('alice', ['admin']),
      extra: {
        'GET /api/projects/1/audit-entries': () =>
          json([
            {
              id: 1,
              projectId: 1,
              actorType: 'user',
              proposedBy: 'user',
              actor: 'alice',
              approvedBy: 'alice',
              model: null,
              promptVersion: null,
              agentRunId: null,
              changeSetId: 7,
              tagName: 'ME-EXH-TT01',
              opType: 'updateTag',
              diff: { type: 'updateTag', field: 'unit', old: 'degC', new: 'degF' },
              timestamp: '2026-10-01T10:05:00Z',
            },
          ]),
      },
    });
    const audit = await screen.findByRole('table', { name: /audit entries of this change set/i });
    expect(within(audit).getByText('unit: degC -> degF')).toBeInTheDocument();
    for (const name of [/^validate$/i, /^approve$/i, /^apply$/i, /^reject$/i]) {
      expect(button(name)).toBeDisabled();
    }
  });

  it('signed out: nothing is actionable and the reason says to sign in', async () => {
    setup({ cs: makeChangeSet({ status: 'validated' }), session: null });
    await screen.findByRole('toolbar');
    await waitFor(() => expect(screen.getByText(/sign in to act/i)).toBeInTheDocument());
    expect(button(/^approve$/i)).toBeDisabled();
    expect(button(/^validate$/i)).toBeDisabled();
  });
});

describe('SIL/ESD second approver', () => {
  const sil = (over: Partial<ChangeSet> = {}) =>
    makeChangeSet({ status: 'validated', requiresSecondApprover: true, ...over });

  it('before any approval: banner explains two approvers; the button says 1 of 2', async () => {
    setup({ cs: sil(), session: makeSession('alice', ['admin']) });
    const banner = await screen.findByTestId('second-approver-banner');
    expect(banner).toHaveTextContent(/requires second approver/i);
    expect(banner).toHaveTextContent(/needs two approvals/i);
    await waitFor(() => expect(button(/approve \(1 of 2\)/i)).toBeEnabled());
  });

  const pending = () => sil({ pendingSecondApproval: true, approvedBy: 'alice' });

  it('pending: the first approver cannot give the second approval', async () => {
    setup({ cs: pending(), session: makeSession('alice', ['admin', 'approver']) });
    expect(await screen.findByTestId('second-approver-banner')).toHaveTextContent(
      /first approval given by alice/i,
    );
    expect(button(/give second approval/i)).toBeDisabled();
    expect(screen.getByText(/different user must give the second/i)).toBeInTheDocument();
    expect(button(/^apply$/i)).toBeDisabled();
  });

  it('pending: a different user WITHOUT the approver role is blocked', async () => {
    setup({ cs: pending(), session: makeSession('carol', ['admin']) });
    await screen.findByTestId('second-approver-banner');
    expect(button(/give second approval/i)).toBeDisabled();
    expect(screen.getByText(/must hold the approver role/i)).toBeInTheDocument();
  });

  it('pending: a different user with the approver role can approve, which posts /approve', async () => {
    const user = userEvent.setup();
    const { calls } = setup({
      cs: pending(),
      session: makeSession('bob', ['approver']),
      extra: {
        'POST /api/change-sets/7/approve': () =>
          json(sil({ status: 'approved', approvedBy: 'alice', secondApprovedBy: 'bob' })),
      },
    });
    await waitFor(() => expect(button(/give second approval/i)).toBeEnabled());
    await user.click(button(/give second approval/i));
    await waitFor(() =>
      expect(
        calls.some((c) => c.method === 'POST' && c.path === '/api/change-sets/7/approve'),
      ).toBe(true),
    );
  });

  it('approved by two: the banner names both approvers', async () => {
    setup({
      cs: sil({ status: 'approved', approvedBy: 'alice', secondApprovedBy: 'bob' }),
      session: makeSession('alice', ['admin']),
    });
    expect(await screen.findByTestId('second-approver-banner')).toHaveTextContent(
      /approved by alice and bob/i,
    );
  });

  it('shows no banner for an ordinary change set', async () => {
    setup({ cs: makeChangeSet({ status: 'validated' }), session: makeSession('alice', ['admin']) });
    await screen.findByRole('toolbar');
    expect(screen.queryByTestId('second-approver-banner')).not.toBeInTheDocument();
  });
});

describe('apply failures', () => {
  it('409 concurrency_conflict names the tags that changed underneath', async () => {
    const user = userEvent.setup();
    setup({
      cs: makeChangeSet({ status: 'approved', approvedBy: 'alice' }),
      session: makeSession('alice', ['admin']),
      extra: {
        'POST /api/change-sets/7/apply': () =>
          problem(409, 'concurrency_conflict', 'The project changed', {
            conflicts: [
              {
                code: 'OLD_VALUE_MISMATCH',
                severity: 'error',
                tagName: 'ME-EXH-TT01',
                field: 'unit',
                message: "Expected 'degC' but found 'K'.",
              },
            ],
          }),
      },
    });
    await waitFor(() => expect(button(/^apply$/i)).toBeEnabled());
    await user.click(button(/^apply$/i));
    const conflicts = await screen.findByTestId('conflicts');
    expect(screen.getByText('Values changed underneath you')).toBeInTheDocument();
    expect(within(conflicts).getByRole('link', { name: 'ME-EXH-TT01' })).toHaveAttribute(
      'href',
      '/tags?filter=ME-EXH-TT01',
    );
    expect(conflicts).toHaveTextContent(/expected 'degC' but found 'K'/i);
  });

  it('422 validation_failed lists the blocking findings', async () => {
    const user = userEvent.setup();
    setup({
      cs: makeChangeSet({ status: 'approved', approvedBy: 'alice' }),
      session: makeSession('alice', ['admin']),
      extra: {
        'POST /api/change-sets/7/apply': () =>
          problem(422, 'validation_failed', 'bad', {
            violations: [err('HH below H', 'ME-EXH-TT02')],
          }),
      },
    });
    await waitFor(() => expect(button(/^apply$/i)).toBeEnabled());
    await user.click(button(/^apply$/i));
    expect(await screen.findByText('Validation failed')).toBeInTheDocument();
    expect(screen.getByText('HH below H')).toBeInTheDocument();
  });
});

describe('two-approver warning on drafts', () => {
  it('shows the SIL/ESD banner on a draft that already requires a second approver', async () => {
    setup({
      cs: makeChangeSet({ status: 'draft', requiresSecondApprover: true }),
      session: makeSession('alice', ['admin']),
    });
    const banner = await screen.findByTestId('second-approver-banner');
    expect(banner).toHaveTextContent(/requires second approver/i);
    expect(banner).toHaveTextContent(/two approvals/i);
  });
});
