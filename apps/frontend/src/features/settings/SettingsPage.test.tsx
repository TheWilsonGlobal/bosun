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
      'GET /api/projects': () => json({ items: [project], total: 1 }),
      'GET /health': () => json({ status: 'ok', databaseOk: true, version: '0.1.0' }),
      'GET /api/projects/proj-1/assistant/knowledge': () =>
        json({ documentsCount: 3, chunksCount: 15, isIndexed: true }),
    });
  });

  it('renders the header and main diagnostic sections', () => {
    renderSettings();

    expect(screen.getByText('Settings & System Telemetry')).toBeInTheDocument();
    expect(screen.getByText(/Live system diagnostics/i)).toBeInTheDocument();

    // Check main section titles
    expect(screen.getByText('LLM & AI Engine Configuration')).toBeInTheDocument();
    expect(screen.getByText('Industrial Marine Protocols & Simulator')).toBeInTheDocument();
    expect(screen.getByText('Database & Storage Architecture')).toBeInTheDocument();
    expect(screen.getByText('Knowledge Base & RAG Vector Engine')).toBeInTheDocument();
    expect(screen.getByText('Safety Governance & Maker-Checker Policy')).toBeInTheDocument();
    expect(screen.getByText('Workspace & UI Customization')).toBeInTheDocument();
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
