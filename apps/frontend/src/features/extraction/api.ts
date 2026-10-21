import { del, fetchBlob, get, patch, post } from '@/lib/api/client';
import type {
  AcceptResult,
  DocumentItem,
  Extraction,
  PatchBody,
  PatchResult,
  UploadResult,
} from './types';

export const fetchDocuments = (projectId: number) =>
  get<DocumentItem[]>(`/api/projects/${projectId}/documents`);

export const uploadDocument = (projectId: number, file: File) => {
  const form = new FormData();
  form.append('file', file);
  return post<UploadResult>(`/api/projects/${projectId}/documents`, form);
};

export const extractAgain = (documentId: number) =>
  post<UploadResult>(`/api/documents/${documentId}/extractions`);

export const fetchExtraction = (id: number, signal?: AbortSignal) =>
  get<Extraction>(`/api/extractions/${id}`, signal);

export const patchRow = (extractionId: number, rowId: number, body: PatchBody) =>
  patch<PatchResult>(`/api/extractions/${extractionId}/rows/${rowId}`, body);

/** allowFlagged: suspected_injection rows the person explicitly reviewed; never sent unless they confirmed. */
export const acceptRows = (extractionId: number, rowIds?: number[], allowFlagged?: number[]) =>
  post<AcceptResult>(`/api/extractions/${extractionId}/accept`, {
    ...(rowIds ? { rowIds } : {}),
    ...(allowFlagged && allowFlagged.length > 0 ? { allowFlagged } : {}),
  });

export const deleteDocument = (documentId: number) => del(`/api/documents/${documentId}`);

export const fetchPageImage = (extractionId: number, page: number, signal?: AbortSignal) =>
  fetchBlob(`/api/extractions/${extractionId}/pages/${page}/image`, signal);
