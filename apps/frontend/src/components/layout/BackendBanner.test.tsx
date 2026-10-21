import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/providers';
import { apiFetch } from '@/lib/api/client';
import { resetConnection } from '@/lib/connection';
import { json, proxy502 } from '@/test/utils';
import { BackendBanner, OFFLINE_RECHECK_MS, ONLINE_RECHECK_MS } from './BackendBanner';

const health = (databaseOk = true) =>
  json({ status: databaseOk ? 'ok' : 'degraded', databaseOk, version: '0.1.0' });

function mount(props: { offlineMs?: number; onlineMs?: number } = {}) {
  return render(
    <QueryClientProvider client={createQueryClient()}>
      <BackendBanner offlineMs={30} onlineMs={60_000} {...props} />
    </QueryClientProvider>,
  );
}

describe('BackendBanner', () => {
  beforeEach(() => resetConnection());
  afterEach(() => vi.restoreAllMocks());

  it('shows "Backend offline" when /api/health comes back as a RESOLVED 502 (no rejection)', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(proxy502());
    mount();
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/backend offline/i);
    expect(spy).toHaveBeenCalledWith('/api/health', expect.anything());
  });

  it('shows the banner when the fetch itself rejects', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'));
    mount();
    expect(await screen.findByRole('alert')).toHaveTextContent(/backend offline/i);
  });

  it('renders nothing while the backend is healthy', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(health());
    mount();
    await waitFor(() => expect(spy).toHaveBeenCalled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('re-checks periodically and clears itself when the backend returns', async () => {
    const spy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(proxy502())
      .mockResolvedValue(health());
    mount();
    await screen.findByRole('alert');
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
    expect(spy.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it('flips to offline as soon as any API call sees a proxy 502, without waiting for the poll', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(health());
    mount();
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    spy.mockResolvedValue(proxy502());
    await expect(apiFetch('/api/projects/1/tags')).rejects.toMatchObject({
      code: 'backend_offline',
    });
    expect(await screen.findByRole('alert')).toHaveTextContent(/backend offline/i);
  });

  it('polls every 8 s while online and every 4 s while offline', () => {
    expect(ONLINE_RECHECK_MS).toBe(8000);
    expect(OFFLINE_RECHECK_MS).toBe(4000);
  });

  it('re-checks immediately when the window regains focus', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(health());
    mount();
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    spy.mockResolvedValue(proxy502());
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });
    expect(await screen.findByRole('alert')).toHaveTextContent(/backend offline/i);
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it('re-checks when the tab becomes visible, but not while it is hidden', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(health());
    mount();
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    const visibility = vi.spyOn(document, 'visibilityState', 'get');
    visibility.mockReturnValue('hidden');
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await new Promise((r) => setTimeout(r, 30));
    expect(spy).toHaveBeenCalledTimes(1);
    visibility.mockReturnValue('visible');
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(2));
  });

  it('probes again right away when an API call resolves as backend_offline', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(health());
    mount();
    await waitFor(() => expect(spy).toHaveBeenCalledTimes(1));
    spy.mockResolvedValue(proxy502());
    await expect(apiFetch('/api/x')).rejects.toMatchObject({ code: 'backend_offline' });
    // one call from apiFetch itself, one from the banner probe it triggered (not 60 s later)
    await waitFor(() =>
      expect(spy.mock.calls.filter((c) => c[0] === '/api/health')).toHaveLength(2),
    );
  });

  it('warns separately when the backend is up but its database is not', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(health(false));
    mount();
    expect(await screen.findByRole('alert')).toHaveTextContent(/database is unavailable/i);
  });
});
