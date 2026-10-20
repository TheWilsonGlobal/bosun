import { resolve } from 'node:path';
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// The single .env lives at the repo root (no per-app .env). Ports come from here, never from a --port flag.
const envDir = resolve(import.meta.dirname, '../..');

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, envDir, '');
  const port = Number(env.FRONTEND_PORT ?? 5481);
  const backend = `http://127.0.0.1:${env.BACKEND_PORT ?? 5480}`;
  return {
    base: '/',
    envDir,
    plugins: [react(), tailwindcss()],
    resolve: { alias: { '@': resolve(import.meta.dirname, 'src') } },
    server: {
      host: '127.0.0.1',
      port,
      strictPort: true,
      proxy: {
        '/api': { target: backend, changeOrigin: true },
        '/hubs': { target: backend, changeOrigin: true, ws: true },
      },
    },
    preview: { host: '127.0.0.1', port, strictPort: true },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test-setup.ts'],
      css: false,
    },
  };
});
