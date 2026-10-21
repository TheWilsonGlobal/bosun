import { get, post } from '@/lib/api/client';
import type { AskResponse, KnowledgeStatus, ReindexResult } from './askLogic';

export const askDocs = (projectId: number, question: string) =>
  post<AskResponse>(`/api/projects/${projectId}/ask`, { question });
export const fetchKnowledge = (projectId: number, signal?: AbortSignal) =>
  get<KnowledgeStatus>(`/api/projects/${projectId}/knowledge`, signal);
export const reindexKnowledge = (projectId: number) =>
  post<ReindexResult>(`/api/projects/${projectId}/knowledge/reindex`);
