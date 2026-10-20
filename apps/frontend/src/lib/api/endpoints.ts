import { ApiError, get, post } from './client';
import type {
  AlarmGroup,
  AuditEntry,
  ChangeOp,
  ChangeSet,
  ChangeSetStatus,
  ChangeSetSummary,
  Diff,
  Health,
  Paged,
  Project,
  Session,
  Tag,
  TagFacets,
  ValidationResult,
} from './types';

/** GET /api/health. Returns the body even for a degraded 503 {status,databaseOk:false}. */
export async function fetchHealth(signal?: AbortSignal): Promise<Health> {
  const res = await fetch('/api/health', { credentials: 'same-origin', signal }).catch(() => null);
  if (!res) throw new Error('offline');
  const json = (res.headers.get('content-type') ?? '').includes('json');
  if (!json) throw new Error(`offline (${res.status})`); // proxy-generated 502
  return (await res.json()) as Health;
}

export const login = (username: string, password: string) =>
  post<Session>('/api/auth/login', { username, password });
export const logout = () => post<void>('/api/auth/logout');
export const fetchSession = async (): Promise<Session | null> => {
  try {
    return await get<Session>('/api/auth/me');
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) return null;
    throw e;
  }
};

export const fetchProjects = () => get<Project[]>('/api/projects');

export const fetchTags = (projectId: number, query: string, signal?: AbortSignal) =>
  get<Paged<Tag>>(`/api/projects/${projectId}/tags${query ? `?${query}` : ''}`, signal);
export const fetchTagFacets = (projectId: number) =>
  get<TagFacets>(`/api/projects/${projectId}/tag-facets`);
export const fetchAlarmGroups = (projectId: number) =>
  get<AlarmGroup[]>(`/api/projects/${projectId}/alarm-groups`);

export const fetchChangeSets = (
  projectId: number,
  opts: { status?: ChangeSetStatus; page?: number; pageSize?: number } = {},
) => {
  const q = new URLSearchParams();
  if (opts.status) q.set('status', opts.status);
  q.set('page', String(opts.page ?? 1));
  q.set('pageSize', String(opts.pageSize ?? 50));
  return get<Paged<ChangeSetSummary>>(`/api/projects/${projectId}/change-sets?${q}`);
};
export const fetchChangeSet = (id: number) => get<ChangeSet>(`/api/change-sets/${id}`);
export const fetchDiff = (id: number) => get<Diff>(`/api/change-sets/${id}/diff`);

export const fetchAudit = (
  projectId: number,
  opts: { changeSetId?: number; tagName?: string; take?: number } = {},
) => {
  const q = new URLSearchParams();
  if (opts.changeSetId) q.set('changeSetId', String(opts.changeSetId));
  if (opts.tagName) q.set('tagName', opts.tagName);
  q.set('take', String(opts.take ?? 100));
  return get<AuditEntry[]>(`/api/projects/${projectId}/audit-entries?${q}`);
};

export const createChangeSet = (projectId: number, title: string, ops: ChangeOp[]) =>
  post<ChangeSet>(`/api/projects/${projectId}/change-sets`, { title, source: 'manual', ops });
export const addOps = (id: number, ops: ChangeOp[]) =>
  post<ChangeSet>(`/api/change-sets/${id}/ops`, { ops });
export const validateChangeSet = (id: number) =>
  post<ValidationResult>(`/api/change-sets/${id}/validate`);
export const approveChangeSet = (id: number) => post<ChangeSet>(`/api/change-sets/${id}/approve`);
export const applyChangeSet = (id: number) => post<ChangeSet>(`/api/change-sets/${id}/apply`);
export const rejectChangeSet = (id: number, reason?: string) =>
  post<ChangeSet>(`/api/change-sets/${id}/reject`, { reason: reason || undefined });
