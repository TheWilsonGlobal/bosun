import { get, post } from '@/lib/api/client';
import type { Paged } from '@/lib/api/types';
import type { AgentRun } from './types';

// The assistant's whole REST surface. There is deliberately nothing here that approves or applies a change set:
// the human does that on the existing change-set page (a test asserts it).

export const startRun = (projectId: number, message: string) =>
  post<{ runId: number }>(`/api/projects/${projectId}/agent/runs`, { message });
export const fetchRun = (id: number, signal?: AbortSignal) =>
  get<AgentRun>(`/api/agent/runs/${id}`, signal);
export const fetchRuns = (projectId: number, page = 1, pageSize = 20) =>
  get<Paged<AgentRun>>(`/api/projects/${projectId}/agent/runs?page=${page}&pageSize=${pageSize}`);
export const cancelRun = (id: number) => post<AgentRun>(`/api/agent/runs/${id}/cancel`);
