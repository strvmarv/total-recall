/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

// The SPA is always served from the loopback origin root, so base '/' keeps
// asset URLs absolute (/assets/...) and stable across client-side routes.
export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: resolve(__dirname, '../wwwroot'),
    emptyOutDir: true,
    assetsDir: 'assets',
  },
  server: {
    port: 5173,
    proxy: {
      // Dev: vite serves the SPA; API calls proxy to `total-recall ui` (default 5577).
      '/api': { target: 'http://127.0.0.1:5577', changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
