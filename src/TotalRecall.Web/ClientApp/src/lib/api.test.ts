import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError } from './api';

function mockFetch(status: number, body: string, contentType = 'application/json') {
  return vi.fn().mockResolvedValue(
    new Response(body, { status, headers: { 'Content-Type': contentType } }),
  );
}

describe('api', () => {
  beforeEach(() => {
    (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__ = {
      token: 'tok-1', backend: 'sqlite', version: 't',
    };
  });
  afterEach(() => {
    delete (window as unknown as { __TR_BOOTSTRAP__?: unknown }).__TR_BOOTSTRAP__;
    vi.restoreAllMocks();
  });

  it('tool() POSTs to /api/tool/{name} with the token header and parses JSON', async () => {
    const f = mockFetch(200, '{"ok":true}');
    vi.stubGlobal('fetch', f);

    const result = await api.tool<{ ok: boolean }>('status', { tier: 'warm' });

    expect(result).toEqual({ ok: true });
    const [url, init] = f.mock.calls[0];
    expect(url).toBe('/api/tool/status');
    expect(init.method).toBe('POST');
    expect(new Headers(init.headers).get('X-Total-Recall-Token')).toBe('tok-1');
    expect(init.body).toBe(JSON.stringify({ tier: 'warm' }));
  });

  it('health() GETs /api/health', async () => {
    const f = mockFetch(200, '{"status":"ok","backend":"sqlite","version":"t"}');
    vi.stubGlobal('fetch', f);

    const h = await api.health();

    expect(h.status).toBe('ok');
    expect(h.backend).toBe('sqlite');
    expect(h.version).toBe('t');
    expect(f.mock.calls[0][0]).toBe('/api/health');
    expect(f.mock.calls[0][1]?.method).toBeUndefined(); // GET (no method override)
  });

  it('tool() sends no body when args is omitted', async () => {
    const f = mockFetch(200, '{}');
    vi.stubGlobal('fetch', f);

    await api.tool('ping');

    expect(f.mock.calls[0][1].body).toBeUndefined();
  });

  it('throws ApiError on non-2xx', async () => {
    vi.stubGlobal('fetch', mockFetch(404, '{"error":"unknown_tool"}'));
    await expect(api.tool('nope')).rejects.toBeInstanceOf(ApiError);
  });

  it('throws ApiError with message from JSON body field when body is {"error":"...","message":"..."}', async () => {
    vi.stubGlobal('fetch', mockFetch(400, '{"error":"invalid_arguments","message":"id is required"}'));
    const err = await api.tool('bad').catch((e) => e) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.message).toBe('id is required');
    expect(err.status).toBe(400);
  });

  it('throws ApiError with trimmed raw text when body is non-JSON', async () => {
    vi.stubGlobal('fetch', mockFetch(400, 'bad things happened', 'text/plain'));
    const err = await api.tool('bad').catch((e) => e) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.message).toBe('bad things happened');
  });
});
