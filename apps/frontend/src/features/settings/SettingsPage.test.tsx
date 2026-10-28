import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { AppProviders, createQueryClient } from '@/providers';
import { mockFetch, project, makeSession, json } from '@/test/utils';
import SettingsPage from './SettingsPage';

function renderSettings() {
  return render(
    <AppProviders client={createQueryClient()}>
      <MemoryRouter initialEntries={['/settings']}>
        <SettingsPage />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe('SettingsPage', () => {
  beforeEach(() => {
    localStorage.clear();
    mockFetch({
      'GET /api/session': () => json(makeSession('admin', ['admin'])),
      'GET /api/projects': () => json([project]),
      'GET /health': () => json({ status: 'ok', databaseOk: true, version: '0.1.0' }),
      'GET /api/projects/1/assistant/knowledge': () =>
        json({ documentsCount: 3, chunksCount: 15, isIndexed: true }),
      'GET /api/projects/1/agent/runs': () =>
        json({
          items: [
            {
              id: 1,
              projectId: 1,
              message: 'Find all coolant tags',
              status: 'awaiting_review',
              createdBy: 'test-user',
              model: 'gemini-3.5-flash-lite',
              promptVersion: 'planner.system@1c3b647a',
              startedAt: '2026-10-28T10:00:00Z',
              finishedAt: '2026-10-28T10:00:02Z',
              toolCalls: 1,
              maxToolCalls: 10,
              elapsedMs: 200,
              tokens: { prompt: 100, candidate: 50, thought: 10, total: 160 },
              maxTotalTokens: 100000,
              changeSetId: 1,
              resultKind: 'draft',
              resultMessage: null,
              errorCode: null,
              errorMessage: null,
            },
          ],
          total: 1,
        }),
      'GET /api/agent/runs/1': () =>
        json({
          id: 1,
          projectId: 1,
          message: 'Find all coolant tags',
          status: 'awaiting_review',
          createdBy: 'test-user',
          model: 'gemini-3.5-flash-lite',
          promptVersion: 'planner.system@1c3b647a',
          startedAt: '2026-10-28T10:00:00Z',
          finishedAt: '2026-10-28T10:00:02Z',
          toolCalls: 1,
          maxToolCalls: 10,
          elapsedMs: 200,
          tokens: { prompt: 100, candidate: 50, thought: 10, total: 160 },
          maxTotalTokens: 100000,
          changeSetId: 1,
          resultKind: 'draft',
          resultMessage: null,
          errorCode: null,
          errorMessage: null,
          steps: [
            {
              seq: 1,
              kind: 'tool_call',
              latencyMs: 120,
              tokens: 20,
              createdAt: '2026-10-28T10:00:01Z',
              payload: {
                name: 'search_tags',
                args: { query: 'coolant' },
              },
            },
            {
              seq: 2,
              kind: 'tool_result',
              latencyMs: 45,
              tokens: 30,
              createdAt: '2026-10-28T10:00:01.5Z',
              payload: {
                name: 'search_tags',
                ok: true,
                content: [{ tag: 'ME_COOL_TEMP' }],
              },
            },
          ],
        }),
    });
  });

  it('renders the header and main diagnostic sections', () => {
    renderSettings();

    expect(screen.getByText('Settings & System Telemetry')).toBeInTheDocument();

    // Check main section titles
    expect(screen.getByText('LLM & AI Engine Configuration')).toBeInTheDocument();
    expect(screen.getByText('Industrial Marine Protocols & Simulator')).toBeInTheDocument();
    expect(screen.getByText('Database & Storage Architecture')).toBeInTheDocument();
    expect(screen.getByText('Knowledge Base & RAG Vector Engine')).toBeInTheDocument();
    expect(screen.getByText('Safety Governance & Maker-Checker Policy')).toBeInTheDocument();
    expect(screen.getByText('AI Agent Tool Registry & Execution Telemetry')).toBeInTheDocument();
    expect(screen.getByText('Workspace & UI Customization')).toBeInTheDocument();
  });

  it('renders the agent tool registry catalogue and supports category filtering', () => {
    renderSettings();

    // Catalogue is shown by default
    expect(screen.getByText('search_tags')).toBeInTheDocument();
    expect(screen.getByText('propose_create_tag')).toBeInTheDocument();
    expect(screen.getByText('validate_draft')).toBeInTheDocument();

    // Filter to write-only tools
    const writeFilter = screen.getByRole('button', { name: 'Write (4)' });
    fireEvent.click(writeFilter);

    // Read tools should be filtered out
    expect(screen.queryByText('search_tags')).not.toBeInTheDocument();
    // Write tool should remain
    expect(screen.getByText('propose_create_tag')).toBeInTheDocument();
    expect(screen.getByText('propose_update_tag')).toBeInTheDocument();
  });

  it('allows switching to execution history tab to inspect recorded tool calls', async () => {
    renderSettings();

    const historyTab = screen.getByRole('button', { name: /Recent Invocations/i });
    fireEvent.click(historyTab);

    // Wait for the mock run prompt to appear
    expect(await screen.findByText(/Find all coolant tags/i)).toBeInTheDocument();
    expect(await screen.findByText(/search_tags/)).toBeInTheDocument();
  });

  it('displays active operational badges and metrics', () => {
    renderSettings();

    expect(screen.getByText('gemini-3.5-flash-lite')).toBeInTheDocument();
    expect(screen.getByText('127.0.0.1:5483')).toBeInTheDocument();
    expect(screen.getByText('127.0.0.1:5484')).toBeInTheDocument();
    expect(screen.getByText('PRAGMA journal_mode=WAL')).toBeInTheDocument();
    expect(screen.getByText('5,000 ms')).toBeInTheDocument();
  });

  it('allows collapsing and expanding sections', () => {
    renderSettings();

    const llmToggle = screen.getByRole('button', { name: /LLM & AI Engine Configuration/i });
    expect(screen.getByText('gemini-3.5-flash-lite')).toBeInTheDocument();

    // Collapse
    fireEvent.click(llmToggle);
    expect(screen.queryByText('gemini-3.5-flash-lite')).not.toBeInTheDocument();

    // Expand
    fireEvent.click(llmToggle);
    expect(screen.getByText('gemini-3.5-flash-lite')).toBeInTheDocument();
  });

  it('allows resetting assistant panel width in preferences', () => {
    localStorage.setItem('bosun.assistant.width', '600');
    renderSettings();

    expect(screen.getByText(/Current width:/i)).toHaveTextContent('600px');

    const resetButton = screen.getByRole('button', { name: /Reset to Default/i });
    expect(resetButton).toBeInTheDocument();

    fireEvent.click(resetButton);
    expect(localStorage.getItem('bosun.assistant.width')).toBe('384');
    expect(screen.getByText(/Current width:/i)).toHaveTextContent('384px');
  });
});
