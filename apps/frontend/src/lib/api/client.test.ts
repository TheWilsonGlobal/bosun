import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, apiFetch, post, setOfflineListener } from './client';
import { conflictsOf, describeError, violationsOf } from './errors';
import { json, problem, proxy502 } from '@/test/utils';

describe('apiFetch', () => {
  const offline = vi.fn();
  beforeEach(() => {
    offline.mockReset();
    setOfflineListener(offline);
  });
  afterEach(() => {
    setOfflineListener(null);
    vi.restoreAllMocks();
  });

  it('treats a RESOLVED proxy 502 as backend offline (not a generic HTTP error)', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(proxy502());
    const err = await apiFetch('/api/projects').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 502, code: 'backend_offline', isOffline: true });
    expect(offline).toHaveBeenCalledWith(true);
  });

  it('treats a rejected fetch as offline with status 0', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'));
    await expect(apiFetch('/api/projects')).rejects.toMatchObject({
      status: 0,
      code: 'backend_offline',
    });
    expect(offline).toHaveBeenCalledWith(true);
  });

  it('does not call a real problem+json 503 "offline"', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      problem(503, 'internal_error', 'database locked'),
    );
    await expect(apiFetch('/x')).rejects.toMatchObject({ status: 503, code: 'internal_error' });
    expect(offline).not.toHaveBeenCalledWith(true);
  });

  it('carries status, stable code, detail and extension members of RFC 7807 bodies', async () => {
    const conflicts = [
      {
        code: 'OLD_VALUE_MISMATCH',
        severity: 'error',
        tagName: 'A-B-TT01',
        field: 'unit',
        message: 'm',
      },
    ];
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      problem(409, 'concurrency_conflict', 'stale', { conflicts, traceId: 'abc' }),
    );
    const err = (await apiFetch('/x').catch((e: unknown) => e)) as ApiError;
    expect(err).toMatchObject({ status: 409, code: 'concurrency_conflict', detail: 'stale' });
    expect(conflictsOf(err)).toEqual(conflicts);
    expect(err.extensions).not.toHaveProperty('traceId');
    expect(violationsOf(err)).toEqual([]);
  });

  it('falls back to "unauthorized" for a bodyless 401 and http_<n> for unknown codes', async () => {
    const spy = vi.spyOn(globalThis, 'fetch');
    spy.mockResolvedValueOnce(new Response(null, { status: 401 }));
    await expect(apiFetch('/x')).rejects.toMatchObject({ code: 'unauthorized' });
    spy.mockResolvedValueOnce(new Response('teapot', { status: 418 }));
    await expect(apiFetch('/x')).rejects.toMatchObject({ code: 'http_418' });
  });

  it('sends same-origin credentials and JSON bodies, and reports online on success', async () => {
    const spy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ ok: true }));
    await post('/api/auth/login', { username: 'u', password: 'p' });
    const [, init] = spy.mock.calls[0];
    expect(init?.credentials).toBe('same-origin');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ username: 'u', password: 'p' });
    expect(offline).toHaveBeenLastCalledWith(false);
  });

  it('returns undefined for 204', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }));
    await expect(post('/api/auth/logout')).resolves.toBeUndefined();
  });
});

describe('describeError', () => {
  const e = (code: string, status = 400, ext: Record<string, unknown> = {}) =>
    new ApiError(status, code, 'detail text', ext);

  it('gives every backend code its own specific title (no generic fallback)', () => {
    const codes: [string, number][] = [
      ['invalid_request', 400],
      ['unauthorized', 401],
      ['invalid_credentials', 401],
      ['second_approver_required', 403],
      ['approver_role_required', 403],
      ['not_found', 404],
      ['invalid_state', 409],
      ['concurrency_conflict', 409],
      ['concurrent_modification', 409],
      ['validation_failed', 422],
      ['too_many_ops', 422],
      ['internal_error', 500],
      ['backend_offline', 502],
    ];
    const titles = codes.map(([c, s]) => describeError(e(c, s)).title);
    expect(new Set(titles).size).toBe(codes.length);
    for (const t of titles) expect(t).not.toMatch(/something went wrong|request failed/i);
  });

  it('distinguishes same_user from missing for second_approver_required', () => {
    const same = describeError(e('second_approver_required', 403, { reason: 'same_user' }));
    const missing = describeError(e('second_approver_required', 403, { reason: 'missing' }));
    expect(same.message).toMatch(/different user/);
    expect(missing.message).toMatch(/not had its second approval/);
  });

  it('keeps the backend detail for unknown codes', () => {
    expect(describeError(e('weird', 418))).toMatchObject({
      title: 'Request failed (418)',
      message: 'detail text',
    });
  });
});
