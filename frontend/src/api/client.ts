import type { ApiEnvelope } from './types';

const API_BASE = '/api/v1';
const TOKEN_KEY = 'logsphere.token';
const USER_KEY = 'logsphere.user';

export class ApiError extends Error {
  readonly httpStatus: number;
  readonly statusCode: string;
  readonly statusDetails: string;
  readonly correlationId?: string;

  constructor(httpStatus: number, statusCode: string, statusDetails: string, correlationId?: string) {
    super(statusDetails || statusCode || `HTTP ${httpStatus}`);
    this.name = 'ApiError';
    this.httpStatus = httpStatus;
    this.statusCode = statusCode;
    this.statusDetails = statusDetails;
    this.correlationId = correlationId;
  }
}

export function toApiError(e: unknown): ApiError {
  if (e instanceof ApiError) return e;
  if (e instanceof Error) return new ApiError(0, 'LOG-NETWORK-001', e.message || 'Network error');
  return new ApiError(0, 'LOG-UNKNOWN-001', 'Unexpected error');
}

// --- token storage ---------------------------------------------------------

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}
export function setToken(token: string): void {
  localStorage.setItem(TOKEN_KEY, token);
}
export function clearToken(): void {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(USER_KEY);
}
export function getCachedUser<T>(): T | null {
  try {
    const raw = localStorage.getItem(USER_KEY);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}
export function setCachedUser(user: unknown): void {
  localStorage.setItem(USER_KEY, JSON.stringify(user));
}

let unauthorizedHandler: (() => void) | null = null;
/** Registered by the auth provider; called on any 401 so the app can log out + redirect. */
export function setUnauthorizedHandler(fn: (() => void) | null): void {
  unauthorizedHandler = fn;
}

// --- core request ----------------------------------------------------------

type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';

async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const token = getToken();
  if (token) headers['Authorization'] = `Bearer ${token}`;

  let res: Response;
  try {
    res = await fetch(`${API_BASE}${path}`, {
      method,
      headers,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch (e) {
    throw new ApiError(0, 'LOG-NETWORK-001', 'Cannot reach the LogSphere API. Check your connection.');
  }

  // A 401 from an endpoint that *checks a credential you typed* is a field error, not a dead
  // session — logging out there destroys the form (and, on the forced-change screen, traps the
  // user in a login loop). Only an unexpected 401 means the token is gone.
  const checksSubmittedCredential =
    path.startsWith('/auth/login') || path.startsWith('/auth/change-password');

  if (res.status === 401 && !checksSubmittedCredential) {
    clearToken();
    unauthorizedHandler?.();
    throw new ApiError(401, 'LOG-AUTH-001', 'Your session has expired. Please sign in again.');
  }

  let envelope: ApiEnvelope<T> | null = null;
  try {
    envelope = (await res.json()) as ApiEnvelope<T>;
  } catch {
    envelope = null;
  }

  if (!res.ok || !envelope || envelope.success === false) {
    throw new ApiError(
      res.status,
      envelope?.statusCode ?? `HTTP-${res.status}`,
      envelope?.statusDetails ?? `Request failed with HTTP ${res.status}`,
      envelope?.correlationId,
    );
  }
  return envelope.data;
}

/** List endpoints return `{ items: [...] }`; accept a bare array too for resilience. */
function unwrapList<T>(data: unknown): T[] {
  if (Array.isArray(data)) return data as T[];
  if (data && typeof data === 'object' && Array.isArray((data as { items?: unknown }).items)) {
    return (data as { items: T[] }).items;
  }
  return [];
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  getList: <T>(path: string) => request<unknown>('GET', path).then((d) => unwrapList<T>(d)),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
  postList: <T>(path: string, body?: unknown) => request<unknown>('POST', path, body).then((d) => unwrapList<T>(d)),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
  patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body),
  del: <T>(path: string) => request<T>('DELETE', path),
};

/** POST /export/logs — returns a raw file, not the JSON envelope. */
export async function downloadExport(filter: unknown, format: 'csv' | 'json', limit = 10000): Promise<void> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  const token = getToken();
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${API_BASE}/export/logs`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ ...(filter as object), format, limit }),
  });
  if (res.status === 401) {
    clearToken();
    unauthorizedHandler?.();
    throw new ApiError(401, 'LOG-AUTH-001', 'Session expired');
  }
  if (!res.ok) {
    let detail = `Export failed with HTTP ${res.status}`;
    try {
      const env = (await res.json()) as ApiEnvelope<unknown>;
      detail = env.statusDetails || detail;
    } catch {
      /* binary/no body */
    }
    throw new ApiError(res.status, 'LOG-EXPORT-001', detail);
  }
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `logsphere-export-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.${format}`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}
