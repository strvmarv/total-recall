export interface Bootstrap {
  token: string;
  backend: string;
  version: string;
}

declare global {
  interface Window {
    __TR_BOOTSTRAP__?: Bootstrap;
  }
}

/**
 * The single source of the per-launch token and active backend. In production
 * the server injects `window.__TR_BOOTSTRAP__` into index.html. In `vite dev`
 * (SPA served by Vite, not the .NET server) it falls back to env vars so the
 * dev server can talk to a locally-running `total-recall ui --token <t>`.
 */
export function getBootstrap(): Bootstrap {
  const injected = typeof window !== 'undefined' ? window.__TR_BOOTSTRAP__ : undefined;
  if (injected) return injected;
  return {
    token: import.meta.env.VITE_TR_TOKEN ?? '',
    backend: import.meta.env.VITE_TR_BACKEND ?? 'sqlite',
    version: 'dev',
  };
}
