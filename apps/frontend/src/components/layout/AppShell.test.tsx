import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AppProviders, createQueryClient } from '@/providers';
import { mockFetch, project, makeSession, json } from '@/test/utils';
import { AppShell } from './AppShell';

describe('AppShell sidebar collapse/expand', () => {
  beforeEach(() => {
    localStorage.clear();
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
});
