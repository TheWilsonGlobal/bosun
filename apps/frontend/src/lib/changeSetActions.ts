import type { ChangeSet, Session, Violation } from './api/types';

export type ActionName = 'validate' | 'approve' | 'apply' | 'reject';

export interface ActionState {
  enabled: boolean;
  label: string;
  /** Why the button is disabled (shown as the tooltip / helper text). */
  reason?: string;
}

export type ActionMap = Record<ActionName, ActionState>;

export const APPROVER_ROLE = 'approver';

export const hasErrors = (violations: readonly Violation[]) =>
  violations.some((v) => v.severity === 'error');

/**
 * The change-set state machine as the UI enforces it (the backend re-checks every step):
 *   draft --validate--> validated --approve--> approved --apply--> applied
 *   SIL/ESD: the first approve keeps `validated` + pendingSecondApproval; a DIFFERENT user holding the
 *   approver role approves second. Reject is possible until applied.
 */
export function availableActions(
  cs: ChangeSet,
  session: Session | null,
  violations: readonly Violation[],
): ActionMap {
  const closed = cs.status === 'applied' || cs.status === 'rejected';
  const base = (label: string): ActionState => ({ enabled: false, label });
  const out: ActionMap = {
    validate: base('Validate'),
    approve: base('Approve'),
    apply: base('Apply'),
    reject: base('Reject'),
  };

  if (closed) {
    const why = `This change set is ${cs.status}.`;
    for (const k of Object.keys(out) as ActionName[]) out[k].reason = why;
    return out;
  }
  if (!session) {
    for (const k of Object.keys(out) as ActionName[])
      out[k].reason = 'Sign in to act on this change set.';
    return out;
  }

  out.validate.enabled = true;
  out.reject.enabled = true;

  // approve
  if (cs.pendingSecondApproval) {
    out.approve.label = 'Give second approval';
    if (session.username === cs.approvedBy) {
      out.approve.reason = `${cs.approvedBy} already gave the first approval. A different user must give the second.`;
    } else if (!session.roles.includes(APPROVER_ROLE)) {
      out.approve.reason = 'The second approver must hold the approver role.';
    } else if (hasErrors(violations)) {
      out.approve.reason = 'Fix the validation errors first.';
    } else {
      out.approve.enabled = true;
    }
  } else if (cs.status !== 'validated') {
    out.approve.reason =
      cs.status === 'draft' ? 'Validate the change set first.' : `Already ${cs.status}.`;
  } else if (hasErrors(violations)) {
    out.approve.reason = 'Fix the validation errors first.';
  } else {
    out.approve.enabled = true;
    if (cs.requiresSecondApprover) out.approve.label = 'Approve (1 of 2)';
  }

  // apply
  if (cs.status === 'approved') out.apply.enabled = true;
  else out.apply.reason = 'Only an approved change set can be applied.';

  return out;
}
