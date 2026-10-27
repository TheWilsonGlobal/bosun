import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AppProviders, createQueryClient } from '@/providers';
import { mockFetch, project, makeSession, json } from '@/test/utils';
import { registerAssistantPanel } from '@/features/assistant/AssistantSlot';
import { AppShell } from './AppShell';

describe('AppShell sidebar collapse/expand', () => {
  beforeEach(() => {
    localStorage.clear();
    registerAssistantPanel(() => <div>Assistant content</div>);
    mockFetch({
      'GET /api/session': () => json(makeSession('admin', ['admin'])),
      'GET /api/projects': () => json({ items: [project], total: 1 }),
      'GET /health': () => json({ status: 'ok', databaseOk: true }),
    });
  });

  const renderShell = () =>
    render(
      <AppProviders client={createQueryClient()}>
        <MemoryRouter initialEntries={['/monitoring']}>
          <AppShell />
        </MemoryRouter>
      </AppProviders>,
    );

  it('renders expanded sidebar by default with w-52', async () => {
    renderShell();
    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(nav).toHaveClass('w-52');
    expect(screen.getByText('Bosun')).toBeInTheDocument();
    expect(screen.getByText('Live')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /assistant/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Collapse sidebar' })).toBeInTheDocument();
  });

  it('collapses sidebar to w-14 and persists in localStorage when collapse button is clicked', () => {
    renderShell();
    const collapseBtn = screen.getByRole('button', { name: 'Collapse sidebar' });
    fireEvent.click(collapseBtn);

    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(nav).toHaveClass('w-14');
    expect(localStorage.getItem('bosun.sidebar.collapsed')).toBe('true');

    // In collapsed state, Expand sidebar button appears
    const expandBtn = screen.getAllByRole('button', { name: /expand sidebar/i })[0];
    expect(expandBtn).toBeInTheDocument();

    // Clicking expand restores w-52
    fireEvent.click(expandBtn);
    expect(nav).toHaveClass('w-52');
    expect(localStorage.getItem('bosun.sidebar.collapsed')).toBe('false');
  });

  it('restores collapsed state on mount if saved in localStorage', () => {
    localStorage.setItem('bosun.sidebar.collapsed', 'true');
    renderShell();

    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(nav).toHaveClass('w-14');
  });

  it('hides the right-side Assistant drawer when navigated to /assistant so assistant is full screen', () => {
    render(
      <AppProviders client={createQueryClient()}>
        <MemoryRouter initialEntries={['/assistant']}>
          <AppShell />
        </MemoryRouter>
      </AppProviders>,
    );

    expect(screen.queryByRole('complementary', { name: 'Assistant' })).not.toBeInTheDocument();
  });

  it('toggles assistant side panel visibility with the header button and persists in localStorage', () => {
    renderShell();

    // Assistant panel is visible by default
    expect(screen.getByRole('complementary', { name: 'Assistant' })).toBeInTheDocument();
    const toggleBtn = screen.getByRole('button', { name: 'Hide Assistant panel' });
    expect(toggleBtn).toBeInTheDocument();

    // Click to hide
    fireEvent.click(toggleBtn);
    expect(screen.queryByRole('complementary', { name: 'Assistant' })).not.toBeInTheDocument();
    expect(localStorage.getItem('bosun.assistant.visible')).toBe('false');

    // Button label updates to Show Assistant panel
    const showBtn = screen.getByRole('button', { name: 'Show Assistant panel' });
    expect(showBtn).toBeInTheDocument();

    // Click to show again
    fireEvent.click(showBtn);
    expect(screen.getByRole('complementary', { name: 'Assistant' })).toBeInTheDocument();
    expect(localStorage.getItem('bosun.assistant.visible')).toBe('true');
  });

  it('respects persisted assistant hidden state from localStorage on initial render', () => {
    localStorage.setItem('bosun.assistant.visible', 'false');
    renderShell();

    expect(screen.queryByRole('complementary', { name: 'Assistant' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show Assistant panel' })).toBeInTheDocument();
  });
});
