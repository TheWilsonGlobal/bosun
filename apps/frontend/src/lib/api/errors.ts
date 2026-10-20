import { ApiError } from './client';
import type { Violation } from './types';

export interface ErrorMessage {
  title: string;
  message: string;
}

function listOf(error: unknown, key: string): Violation[] {
  if (error instanceof ApiError) {
    const v = error.extensions[key];
    if (Array.isArray(v)) return v as Violation[];
  }
  return [];
}

export const violationsOf = (error: unknown) => listOf(error, 'violations');
export const conflictsOf = (error: unknown) => listOf(error, 'conflicts');

/** Maps every stable backend `code` to a specific message (plan s7.8: never a generic "something went wrong"). */
export function describeError(error: unknown): ErrorMessage {
  if (!(error instanceof ApiError)) {
    return {
      title: 'Unexpected error',
      message: error instanceof Error ? error.message : 'An unexpected error occurred.',
    };
  }
  switch (error.code) {
    case 'backend_offline':
      return {
        title: 'Backend offline',
        message: 'The Bosun backend is not reachable. Nothing was saved; retry when it is back.',
      };
    case 'invalid_credentials':
      return { title: 'Sign-in failed', message: 'Invalid username or password.' };
    case 'unauthorized':
      return {
        title: 'Sign-in required',
        message: 'Sign in to make changes. Your session may have expired.',
      };
    case 'invalid_request':
      return {
        title: 'Invalid request',
        message: error.detail || 'The request was rejected as malformed.',
      };
    case 'second_approver_required':
      return {
        title: 'Second approver required',
        message:
          error.extensions.reason === 'same_user'
            ? 'This change set touches SIL/ESD tags. The second approval must come from a different user than the first.'
            : 'This change set touches SIL/ESD tags and has not had its second approval yet.',
      };
    case 'approver_role_required':
      return {
        title: 'Approver role required',
        message:
          'Only a user holding the approver role can give the second approval of a SIL/ESD change set.',
      };
    case 'forbidden':
      return { title: 'Not allowed', message: error.detail || 'You are not allowed to do this.' };
    case 'not_found':
      return { title: 'Not found', message: error.detail || 'The item no longer exists.' };
    case 'invalid_state':
      return {
        title: 'Not allowed in this state',
        message:
          error.detail || 'The change set is not in a state that allows this action. Reload it.',
      };
    case 'concurrency_conflict':
      return {
        title: 'Values changed underneath you',
        message:
          'The project changed since this change set was drafted: some operations no longer match the current values. The affected tags are listed below.',
      };
    case 'concurrent_modification':
      return {
        title: 'Saved by someone else at the same time',
        message: 'Another change was saved at the same moment. Reload and try again.',
      };
    case 'validation_failed':
      return {
        title: 'Validation failed',
        message: 'The change set violates configuration rules. Fix the errors listed below.',
      };
    case 'too_many_ops':
      return {
        title: 'Too many operations',
        message:
          error.detail || 'A change set may contain at most 200 operations. Split the edits.',
      };
    case 'agent_run_in_progress':
      return {
        title: 'A run is already in progress',
        message: 'Only one assistant run can be active per project. Wait for it or cancel it.',
      };
    case 'internal_error':
      return {
        title: 'Server error',
        message:
          'The backend failed while handling this request. It was logged server-side; try again.',
      };
    default:
      return { title: `Request failed (${error.status})`, message: error.detail || error.code };
  }
}
