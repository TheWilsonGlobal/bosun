import { resolve } from 'node:path';
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// The single .env lives at the repo root (no per-app .env).
const envDir = resolve(import.meta.dirname, '../..');

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, envDir, '');
  const port = Number(env.ADMIN_PORT ?? 5482);
  const backend = `http://127.0.0.1:${env.BACKEND_PORT ?? 5480}`;
  return {
    base: '/admin/',
    envDir,
    plugins: [react()],
    server: {
      host: '127.0.0.1',
      port,
      strictPort: true,
      proxy: {
        '/api': {
          target: backend,
          changeOrigin: true,
          configure: (proxy) => {
            proxy.on('error', (_err, _req, res) => {
              if (res && 'writeHead' in res && !res.headersSent) {
                res.writeHead(502, { 'content-type': 'text/plain' });
                res.end('');
              }
            });
          },
        },
      },
    },
    preview: { host: '127.0.0.1', port, strictPort: true },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test-setup.ts'],
    },
  };
});
