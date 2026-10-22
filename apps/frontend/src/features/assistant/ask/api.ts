import { get, post } from '@/lib/api/client';
import type { AskResponse, KnowledgeChunkItem, KnowledgeStatus, ReindexResult } from './askLogic';

export const askDocs = (projectId: number, question: string) =>
  post<AskResponse>(`/api/projects/${projectId}/ask`, { question });
export const fetchKnowledge = (projectId: number, signal?: AbortSignal) =>
  get<KnowledgeStatus>(`/api/projects/${projectId}/knowledge`, signal);
export const fetchKnowledgeChunks = (projectId: number, signal?: AbortSignal) =>
  get<KnowledgeChunkItem[]>(`/api/projects/${projectId}/knowledge/chunks`, signal);
export const reindexKnowledge = (projectId: number) =>
  post<ReindexResult>(`/api/projects/${projectId}/knowledge/reindex`);
