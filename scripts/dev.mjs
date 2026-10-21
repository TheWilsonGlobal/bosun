// `pnpm run dev`: starts the ASP.NET Core API (dotnet run) and both SPAs (apps/frontend, apps/admin) side by side.
// Tiny on purpose: no extra dependency. Ports come from the root .env (5480 API, 5481 frontend, 5482 admin).
import { spawn, spawnSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import net from 'node:net';
import { join } from 'node:path';
import { dotnetEnv, repoRoot } from './dotnet.mjs';

function readEnvFile() {
  const file = join(repoRoot, '.env');
  const values = {};
  if (!existsSync(file)) return values;
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/.exec(line);
    if (m && !line.trimStart().startsWith('#')) values[m[1]] = m[2].replace(/^(['"])(.*)\1$/, '$2');
  }
  return values;
}

const env = { ...readEnvFile(), ...process.env };
const ports = {
  api: Number(env.BACKEND_PORT ?? 5480),
  frontend: Number(env.FRONTEND_PORT ?? 5481),
  admin: Number(env.ADMIN_PORT ?? 5482),
};

function isFree(port) {
  return new Promise((resolve) => {
    const server = net.createServer();
    server.once('error', () => resolve(false));
    server.once('listening', () => server.close(() => resolve(true)));
    server.listen(port, '127.0.0.1');
  });
}

const busy = [];
for (const [name, port] of Object.entries(ports)) {
  if (!(await isFree(port))) busy.push(`${name} (${port})`);
}
if (busy.length > 0) {
  console.error(`Port(s) already in use: ${busy.join(', ')}. Stop whatever holds them and retry.`);
  process.exit(1);
}

const children = [];
let stopping = false;

function start(label, command, args, { critical = true, survivable = false } = {}) {
  // pnpm is a .cmd shim on Windows, which cannot be spawned without a shell (and shell+args is deprecated).
  // When started via `pnpm run`, npm_execpath points at the real pnpm CLI, which we can spawn directly.
  const pnpmPath = process.env.npm_execpath;
  if (command === 'pnpm' && pnpmPath) {
    // pnpm 12 ships a native executable; older versions a JS entry point.
    if (/\.[cm]?js$/.test(pnpmPath)) {
      args = [pnpmPath, ...args];
      command = process.execPath;
    } else {
      command = pnpmPath;
    }
  }

  const child = spawn(command, args, {
    cwd: repoRoot,
    env: dotnetEnv(),
    shell: false,
    detached: process.platform !== 'win32',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const pipe = (stream, out) => {
    let buffer = '';
    stream.on('data', (chunk) => {
      buffer += chunk.toString();
      const lines = buffer.split(/\r?\n/);
      buffer = lines.pop() ?? '';
      for (const line of lines) out.write(`[${label}] ${line}\n`);
    });
  };
  pipe(child.stdout, process.stdout);
  pipe(child.stderr, process.stderr);
  child.on('error', (err) => {
    console.error(`[${label}] failed to start: ${err.message}`);
    stop(1);
  });
  child.on('exit', (code) => {
    if (stopping) return;
    if (survivable) {
      console.error(
        `[${label}] exited with code ${code}. The SPAs are still running (the offline banner can be exercised). Press Ctrl+C to stop everything.`,
      );
      return;
    }
    if (!critical && code === 0) {
      console.log(`[${label}] nothing left to run (exit 0); the API keeps running.`);
      return;
    }
    console.error(`[${label}] exited with code ${code}; stopping the others.`);
    stop(code ?? 1);
  });
  children.push(child);
  return child;
}

function kill(child) {
  if (child.exitCode !== null || !child.pid) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
  } else {
    try {
      process.kill(-child.pid, 'SIGTERM');
    } catch {
      child.kill('SIGTERM');
    }
  }
}

function stop(code = 0) {
  if (stopping) return;
  stopping = true;
  children.forEach(kill);
  setTimeout(() => process.exit(code), 500);
}

process.on('SIGINT', () => stop(0));
process.on('SIGTERM', () => stop(0));

console.log(
  `API http://127.0.0.1:${ports.api}  |  frontend http://127.0.0.1:${ports.frontend}  |  admin http://127.0.0.1:${ports.admin}/admin/`,
);

start('api', 'dotnet', ['run', '--project', 'apps/backend/src/Bosun.Api'], { survivable: true });

// pnpm skips packages that do not exist or have no `dev` script (--if-present), so the API also starts before the SPAs are scaffolded.
start('web', 'pnpm', ['-r', '--parallel', '--if-present', 'run', 'dev'], { critical: false });
