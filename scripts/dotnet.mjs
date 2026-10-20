// Runs `dotnet <args>` from the repo root, adding the default Windows install dir to PATH when `dotnet` is not on it.
import { spawn, spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { delimiter, dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

export function dotnetEnv() {
  const env = { ...process.env };
  const probe = spawnSync('dotnet', ['--version'], { env, stdio: 'ignore', shell: false });
  if (probe.error) {
    const candidates = ['C:/Program Files/dotnet', '/usr/share/dotnet', '/usr/local/share/dotnet'];
    const dir = candidates.find((d) => existsSync(d));
    if (dir) {
      env.PATH = `${dir}${delimiter}${env.PATH ?? ''}`;
    }
  }
  env.DOTNET_NOLOGO = '1';
  env.DOTNET_CLI_TELEMETRY_OPTOUT = '1';
  return env;
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  const child = spawn('dotnet', process.argv.slice(2), {
    cwd: repoRoot,
    env: dotnetEnv(),
    stdio: 'inherit',
  });
  child.on('exit', (code, signal) => process.exit(code ?? (signal ? 1 : 0)));
}
