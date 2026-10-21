// The one fetch wrapper. The Vite proxy answers a dead backend with a *resolved* 502, so every response is
// checked by status and content type; a rejected fetch is only the second way to be offline.

export type OfflineListener = (offline: boolean) => void;

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly detail: string;
  /** Extra RFC 7807 members (violations, conflicts, reason, ...). */
  readonly extensions: Record<string, unknown>;

  constructor(
    status: number,
    code: string,
    detail: string,
    extensions: Record<string, unknown> = {},
  ) {
    super(detail || code);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.detail = detail;
    this.extensions = extensions;
  }

  get isOffline(): boolean {
    return this.code === 'backend_offline';
  }
}

let offlineListener: OfflineListener | null = null;

/** The connection store registers here so a failed call flips the banner immediately. */
export function setOfflineListener(listener: OfflineListener | null) {
  offlineListener = listener;
}

const OFFLINE_STATUSES = new Set([502, 503, 504]);

function isJson(res: Response): boolean {
  return (res.headers.get('content-type') ?? '').includes('json');
}

async function toError(res: Response): Promise<ApiError> {
  // A proxy-generated 502/503/504 is not JSON; a real backend problem+json 503 is not "offline".
  if (OFFLINE_STATUSES.has(res.status) && !isJson(res)) {
    return new ApiError(res.status, 'backend_offline', 'The backend is not reachable.');
  }
  let body: Record<string, unknown> = {};
  if (isJson(res)) {
    try {
      const parsed: unknown = await res.json();
      if (parsed && typeof parsed === 'object') body = parsed as Record<string, unknown>;
    } catch {
      /* malformed body: fall through to defaults */
    }
  }
  const { code, detail, title, ...rest } = body;
  delete rest.type;
  delete rest.status;
  delete rest.traceId;
  const resolvedCode =
    typeof code === 'string' ? code : res.status === 401 ? 'unauthorized' : `http_${res.status}`;
  const resolvedDetail =
    typeof detail === 'string' ? detail : typeof title === 'string' ? title : res.statusText;
  return new ApiError(res.status, resolvedCode, resolvedDetail, rest);
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH' | 'DELETE';
  /** A FormData body is sent as multipart (the browser sets the boundary); anything else as JSON. */
  body?: unknown;
  signal?: AbortSignal;
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, signal } = options;
  let res: Response;
  const multipart = typeof FormData !== 'undefined' && body instanceof FormData;
  try {
    res = await fetch(path, {
      method,
      credentials: 'same-origin',
      signal,
      headers: {
        Accept: 'application/json, application/problem+json',
        ...(body !== undefined && !multipart ? { 'Content-Type': 'application/json' } : {}),
      },
      body: body === undefined ? undefined : multipart ? (body as FormData) : JSON.stringify(body),
    });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause;
    offlineListener?.(true);
    throw new ApiError(0, 'backend_offline', 'The backend is not reachable.');
  }
  if (!res.ok) {
    const error = await toError(res);
    if (error.isOffline) offlineListener?.(true);
    throw error;
  }
  offlineListener?.(false);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

export const get = <T>(path: string, signal?: AbortSignal) => apiFetch<T>(path, { signal });
export const post = <T>(path: string, body?: unknown) =>
  apiFetch<T>(path, { method: 'POST', body: body ?? undefined });
export const patch = <T>(path: string, body: unknown) =>
  apiFetch<T>(path, { method: 'PATCH', body });

export const del = (path: string) => apiFetch<void>(path, { method: 'DELETE' });

/** GET a binary body (a page image). Same status / offline semantics as apiFetch. */
export async function fetchBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  let res: Response;
  try {
    res = await fetch(path, { credentials: 'same-origin', signal });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') throw cause;
    offlineListener?.(true);
    throw new ApiError(0, 'backend_offline', 'The backend is not reachable.');
  }
  if (!res.ok) {
    const error = await toError(res);
    if (error.isOffline) offlineListener?.(true);
    throw error;
  }
  offlineListener?.(false);
  if (!(res.headers.get('content-type') ?? '').includes('image/')) {
    throw new ApiError(res.status, 'not_an_image', 'The backend did not return an image.');
  }
  return res.blob();
}
