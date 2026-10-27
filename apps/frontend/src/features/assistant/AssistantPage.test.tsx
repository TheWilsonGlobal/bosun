import { screen, fireEvent } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { json, makeSession, mockFetch, project, renderAt } from '@/test/utils';
import { AssistantPage } from './AssistantPage';

describe('AssistantPage', () => {
  beforeEach(() => {
    mockFetch({
      'GET /api/session': () => json(makeSession('alice', ['engineer'])),
      'GET /api/projects': () => json([project]),
      'GET /api/projects/1/assistant/runs': () => json({ items: [], total: 0 }),
      'GET /api/projects/1/knowledge': () =>
        json({
          indexed: true,
          documents: 3,
          chunks: 15,
          embeddedChunks: 15,
          documentsOnDisk: 3,
          embeddingModel: 'gemini-embedding-2',
          dimensions: 768,
          lastIndexedAt: '2026-10-20T10:00:00Z',
          sources: [],
        }),
    });
  });

  it('renders the assistant page with change mode by default', async () => {
    renderAt('/assistant', '/assistant', <AssistantPage />);

    // Assistant panel header
    expect(screen.getByText('Assistant')).toBeInTheDocument();
    expect(screen.getByText('drafts only')).toBeInTheDocument();

    // Check tabs
    expect(screen.getByRole('tab', { name: 'Change' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Ask' })).toBeInTheDocument();

    // Check textarea
    expect(screen.getByLabelText('Describe a change')).toBeInTheDocument();
  });

  it('allows switching to Ask mode to query maritime documentation', async () => {
    renderAt('/assistant', '/assistant', <AssistantPage />);

    const askTab = screen.getByRole('tab', { name: 'Ask' });
    fireEvent.click(askTab);

    expect(await screen.findByPlaceholderText(/what does the alarm philosophy say/i)).toBeInTheDocument();
    expect(screen.getByText(/Answers from the docs/i)).toBeInTheDocument();
  });
});
