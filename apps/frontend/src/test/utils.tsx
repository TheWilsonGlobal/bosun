import type { ReactNode } from 'react';
import { render } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { vi } from 'vitest';
import { AppProviders, createQueryClient } from '@/providers';
import type { ChangeSet, Diff, Project, Session } from '@/lib/api/types';

export const json = (body: unknown, status = 200, type = 'application/json') =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': type } });

export const problem = (status: number, code: string, detail: string, extra: object = {}) =>
  json({ status, code, detail, title: detail, ...extra }, status, 'application/problem+json');

/** The Vite proxy's answer for a dead backend: a RESOLVED 502 with a non-JSON body. */
export const proxy502 = () =>
  new Response('', { status: 502, headers: { 'content-type': 'text/plain' } });

type Handler = (req: { url: URL; method: string; body: unknown }) => Response | Promise<Response>;

/** Routes are keyed "METHOD /path" (no query). Unmatched calls fail the test loudly. */
export function mockFetch(routes: Record<string, Handler>) {
  const calls: { method: string; path: string; body: unknown }[] = [];
  const impl = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://test.local');
    const method = init?.method ?? 'GET';
    const body = init?.body ? JSON.parse(String(init.body)) : undefined;
    calls.push({ method, path: url.pathname, body });
    const handler = routes[`${method} ${url.pathname}`];
    if (!handler) throw new Error(`Unmocked request: ${method} ${url.pathname}`);
    return handler({ url, method, body });
  };
  const spy = vi.spyOn(globalThis, 'fetch').mockImplementation(impl as typeof fetch);
  return { calls, spy };
}

export const project: Project = {
  id: 1,
  vesselId: 1,
  name: 'Test project',
  vesselName: 'MV Test',
  imoNumber: null,
  description: null,
  tagCount: 3,
  version: 1,
};

export const makeSession = (username: string, roles: string[]): Session => ({ username, roles });

export function makeChangeSet(over: Partial<ChangeSet> = {}): ChangeSet {
  return {
    id: 7,
    projectId: 1,
    title: 'Raise ME-EXH alarm limits',
    source: 'manual',
    status: 'draft',
    createdBy: 'alice',
    createdAt: '2026-10-01T10:00:00Z',
    ops: [],
    requiresSecondApprover: false,
    pendingSecondApproval: false,
    approvedBy: null,
    approvedAt: null,
    secondApprovedBy: null,
    secondApprovedAt: null,
    appliedBy: null,
    appliedAt: null,
    rejectedBy: null,
    rejectedAt: null,
    rejectReason: null,
    model: null,
    promptVersion: null,
    agentRunId: null,
    version: 1,
    ...over,
  };
}

export const makeDiff = (cs: ChangeSet, over: Partial<Diff> = {}): Diff => ({
  changeSetId: cs.id,
  status: cs.status,
  requiresSecondApprover: cs.requiresSecondApprover,
  rows: [
    {
      index: 0,
      type: 'updateTag',
      tagName: 'ME-EXH-TT01',
      field: 'unit',
      old: 'degC',
      new: 'degF',
      op: { type: 'updateTag', tagName: 'ME-EXH-TT01', field: 'unit', old: 'degC', new: 'degF' },
    },
  ],
  violations: [],
  ...over,
});

export function renderAt(path: string, routePath: string, element: ReactNode) {
  const client = createQueryClient();
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AppProviders client={client}>
        <Routes>
          <Route path={routePath} element={element} />
        </Routes>
      </AppProviders>
    </MemoryRouter>,
  );
}
