// Live acceptance: the demo instruction N times through the real HTTP API + agent. Usage: node scripts/agent-demo.mjs [count]
// Reads ADMIN_USERNAME / ADMIN_PASSWORD from the environment or the root .env; never prints them.
import { readFileSync } from 'node:fs';

const base = process.env.BOSUN_URL ?? 'http://127.0.0.1:5480';
const count = Number(process.argv[2] ?? 5);
const MESSAGE =
  'Add high-temperature alarms at 95 °C with a 10 s delay to all main engine exhaust gas temperature sensors, and put them in alarm group ME-EXH.';

function env(key) {
  if (process.env[key]) return process.env[key];
  try {
    const line = readFileSync(new URL('../.env', import.meta.url), 'utf8')
      .split(/\r?\n/)
      .find((l) => l.startsWith(`${key}=`));
    return line?.slice(key.length + 1).trim();
  } catch {
    return undefined;
  }
}

let cookie = '';
async function call(method, path, body) {
  const res = await fetch(base + path, {
    method,
    headers: { 'content-type': 'application/json', ...(cookie ? { cookie } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const setCookie = res.headers.getSetCookie?.() ?? [];
  if (setCookie.length) cookie = setCookie.map((c) => c.split(';')[0]).join('; ');
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : null };
}

const login = await call('POST', '/api/auth/login', {
  username: env('ADMIN_USERNAME'),
  password: env('ADMIN_PASSWORD'),
});
if (login.status !== 200) throw new Error(`login failed: ${login.status}`);
const projects = await call('GET', '/api/projects');
const projectId = (projects.json.items ?? projects.json)[0].id;

const results = [];
for (let i = 1; i <= count; i++) {
  const started = await call('POST', `/api/projects/${projectId}/agent/runs`, { message: MESSAGE });
  if (started.status !== 202)
    throw new Error(`start failed: ${started.status} ${JSON.stringify(started.json)}`);
  const runId = started.json.runId;
  let run;
  for (;;) {
    run = (await call('GET', `/api/agent/runs/${runId}`)).json;
    if (!['queued', 'running'].includes(run.status)) break;
    await new Promise((r) => setTimeout(r, 1500));
  }
  let ok = false;
  let detail = '';
  if (run.changeSetId) {
    const cs = (await call('GET', `/api/change-sets/${run.changeSetId}`)).json;
    const limits = new Map();
    const groups = new Map();
    for (const op of cs.ops) {
      const t = op.tagName;
      if (op.type === 'setAlarmLimit')
        limits.set(`${t}|${op.level}`, op.new ? `${op.new.value}/${op.new.delayS}` : 'removed');
      else if (op.type === 'assignGroup') groups.set(t, op.new);
      else detail += ` other:${op.type}:${t}`;
    }
    const tags = [1, 2, 3, 4, 5, 6, 7, 8].map((n) => `ME-EXH-TT0${n}`);
    const good =
      limits.size === 8 &&
      groups.size === 8 &&
      tags.every((t) => limits.get(`${t}|H`) === '95/10' && groups.get(t) === 'ME-EXH') &&
      !detail;
    const validation = await call('POST', `/api/change-sets/${run.changeSetId}/validate`);
    const valid = validation.status === 200 && validation.json?.valid === true;
    ok = good && run.status === 'awaiting_review' && valid;
    detail += ` limits=${limits.size} groups=${groups.size} valid=${valid} validationStatus=${validation.status}`;
    if (!valid && i === 1) detail += ' ' + JSON.stringify(validation.json).slice(0, 300);
  }
  results.push({
    run: i,
    runId,
    ok,
    status: run.status,
    errorCode: run.errorCode,
    toolCalls: run.toolCalls,
    tokens: run.tokens,
    elapsedMs: run.elapsedMs,
    detail,
  });
  console.log(JSON.stringify(results.at(-1)));
}
console.log(`PASSED ${results.filter((r) => r.ok).length}/${count}`);
