import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { AuditEntry } from '@/lib/api/types';
import { AuditTable } from './AuditTable';

const entry = (over: Partial<AuditEntry>): AuditEntry => ({
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
  diff: null,
  timestamp: '2026-10-01T10:05:00Z',
  ...over,
});

describe('AuditTable proposer', () => {
  it('shows an agent badge with model, prompt version and run id when proposedBy is agent', () => {
    render(
      <AuditTable
        entries={[
          entry({ proposedBy: 'agent', model: 'gemini-x', promptVersion: 'v3', agentRunId: 42 }),
        ]}
      />,
    );
    expect(screen.getByTestId('proposed-by-agent')).toHaveTextContent(
      'proposed by agent (gemini-x, prompt v3)',
    );
    expect(screen.getByText('agent run #42')).toBeInTheDocument();
  });

  it('shows no badge for a human-proposed entry even though actorType is user for both', () => {
    render(<AuditTable entries={[entry({})]} />);
    expect(screen.queryByTestId('proposed-by-agent')).toBeNull();
    expect(screen.queryByText(/agent run/)).toBeNull();
  });
});
