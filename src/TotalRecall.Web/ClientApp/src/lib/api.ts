import { getBootstrap } from './bootstrap';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
    this.name = 'ApiError';
  }
}

export interface Health {
  status: string;
  backend: string;
  version: string;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const { token } = getBootstrap();
  const headers = new Headers(init?.headers);
  if (token) headers.set('X-Total-Recall-Token', token);

  const resp = await fetch(`/api${path}`, { ...init, headers });
  const text = await resp.text();
  if (!resp.ok) {
    throw new ApiError(resp.status, text || `Request failed: ${resp.status}`);
  }
  return (text ? JSON.parse(text) : null) as T;
}

export const api = {
  health: () => request<Health>('/health'),

  /** Dispatch an allowlisted MCP tool by name and parse its JSON result. */
  tool: <T>(name: string, args?: unknown): Promise<T> =>
    request<T>(`/tool/${name}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: args === undefined ? undefined : JSON.stringify(args),
    }),
};
