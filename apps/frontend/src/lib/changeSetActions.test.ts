import { describe, expect, it } from 'vitest';
import { availableActions } from './changeSetActions';
import { makeChangeSet, makeSession } from '@/test/utils';

describe('availableActions after the server demotes an approved SIL/ESD change set', () => {
  // Re-validating an approved change set that became SIL/ESD-affecting puts it back to `validated`.
  const demoted = makeChangeSet({
    status: 'validated',
    requiresSecondApprover: true,
    pendingSecondApproval: false,
    approvedBy: null,
  });

  it('enables Approve again (as step 1 of 2) and keeps Apply disabled', () => {
    const a = availableActions(demoted, makeSession('alice', ['admin']), []);
    expect(a.approve.enabled).toBe(true);
    expect(a.approve.label).toBe('Approve (1 of 2)');
    expect(a.apply.enabled).toBe(false);
  });

  it('a draft that requires two approvers still needs validation first', () => {
    const a = availableActions(
      makeChangeSet({ status: 'draft', requiresSecondApprover: true }),
      makeSession('alice', ['admin']),
      [],
    );
    expect(a.approve.enabled).toBe(false);
    expect(a.approve.reason).toMatch(/validate/i);
  });
});
