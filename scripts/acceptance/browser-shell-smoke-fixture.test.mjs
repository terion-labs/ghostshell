// Deterministic tests use only owned loopback processes. Public health transit is
// an explicit manual acceptance scenario and is never enabled by these tests.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import net from 'node:net';
import { createInterface } from 'node:readline';
import test from 'node:test';
import vm from 'node:vm';

const fixturePath = new URL('./browser-shell-smoke-fixture.mjs', import.meta.url);
const authorization = 'Basic ' + Buffer.from('fixture:fixture').toString('base64');

async function startFixture(t, args = []) {
  const child = spawn(process.execPath, [fixturePath.pathname, ...args], { stdio: ['ignore', 'pipe', 'pipe'] });
  const records = [];
  let stderr = '';
  child.stderr.on('data', bytes => { stderr = (stderr + bytes).slice(-4096); });
  const exited = once(child, 'exit');
  const watchdog = setTimeout(() => child.kill('SIGKILL'), 15000);
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGTERM');
    await exited;
    clearTimeout(watchdog);
    assert.equal(child.exitCode, 0, stderr);
  });
  const lines = createInterface({ input: child.stdout });
  const ready = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', () => reject(new Error('Fixture exited before readiness: ' + stderr)));
    lines.on('line', line => {
      const record = JSON.parse(line);
      records.push(record);
      if (record.origin) resolve(record);
    });
  });
  return { child, exited, records, originPort: Number(new URL(ready.origin).port), proxyPort: Number(new URL(ready.proxy).port) };
}

async function request(fixture, packet) {
  const socket = net.connect(fixture.proxyPort, '127.0.0.1');
  const timeout = setTimeout(() => socket.destroy(new Error('Test response deadline')), 3000);
  try {
    await once(socket, 'connect');
    socket.write(packet);
    let response = '';
    for await (const chunk of socket) response += chunk;
    return response;
  } finally {
    clearTimeout(timeout);
    socket.destroy();
  }
}

function connectPacket(authority, authentication = authorization, head = '') {
  const header = authentication === null ? '' : `Proxy-Authorization: ${authentication}\r\n`;
  return `CONNECT ${authority} HTTP/1.1\r\nHost: ${authority}\r\n${header}\r\n${head}`;
}

test('CONNECT requires one exact proxy authorization before dialing', { timeout: 10000 }, async t => {
  const fixture = await startFixture(t);
  const authority = `127.0.0.1:${fixture.originPort}`;
  for (const supplied of [null, 'Basic incorrect', `${authorization}\r\nProxy-Authorization: ${authorization}`]) {
    const response = await request(fixture, connectPacket(authority, supplied, 'GET / HTTP/1.1\r\n\r\n'));
    assert.match(response, /^HTTP\/1.1 407 /);
    assert.match(response, /Proxy-Authenticate: Basic realm="asura-proxy-fixture"/);
  }
  assert.equal(fixture.records.filter(record => record.source === 'origin').length, 0);
});

test('CONNECT rejects noncanonical and unlisted authorities without DNS or public transit', { timeout: 10000 }, async t => {
  const fixture = await startFixture(t);
  const port = fixture.originPort;
  for (const authority of [
    `localhost:${port}`, `127.1:${port}`, `2130706433:${port}`, `127.0.0.1:0${port}`,
    `user@127.0.0.1:${port}`, `127.0.0.1:${port}/`, `127.0.0.1:${port}?x=1`,
    `[::1]:${port}`, `127.0.0.1:${port + 1}`, `127.0.0.1:+${port}`,
    `127.0.0.1:${port}%20`, '1.1.1.1:443', '1.0.0.1:443', '1.1.1.1:80', 'example.invalid:443',
  ]) {
    assert.match(await request(fixture, connectPacket(authority)), /^HTTP\/1.1 502 /, authority);
  }
  assert.equal(fixture.records.filter(record => record.source === 'origin').length, 0);
});

test('CONNECT forwards buffered head bytes in order and preserves server authentication', { timeout: 10000 }, async t => {
  const fixture = await startFixture(t);
  const authority = `127.0.0.1:${fixture.originPort}`;
  const unauthenticated = 'GET /auth HTTP/1.1\r\nHost: ' + authority + '\r\nConnection: close\r\n\r\n';
  const challenge = await request(fixture, connectPacket(authority, authorization, unauthenticated));
  assert.match(challenge, /^HTTP\/1.1 200 Connection Established/);
  assert.match(challenge, /HTTP\/1.1 401 /);
  const authenticated = `GET /auth HTTP/1.1\r\nHost: ${authority}\r\nAuthorization: ${authorization}\r\nConnection: close\r\n\r\n`;
  const response = await request(fixture, connectPacket(authority, authorization, authenticated));
  assert.match(response, /^HTTP\/1.1 200 Connection Established/);
  assert.match(response, /Authenticated with fixture credentials/);
  assert.deepEqual(fixture.records.filter(record => record.source === 'origin').map(record => record.authenticated), [false, true]);
});

test('ordinary HTTP proxy requests retain authentication and exact origin restrictions', { timeout: 10000 }, async t => {
  const fixture = await startFixture(t);
  const target = `http://127.0.0.1:${fixture.originPort}/auth`;
  assert.match(await request(fixture, `GET ${target} HTTP/1.1\r\nHost: fixture\r\nConnection: close\r\n\r\n`), /^HTTP\/1.1 407 /);
  const headers = `Host: fixture\r\nProxy-Authorization: ${authorization}\r\nConnection: close\r\n\r\n`;
  assert.match(await request(fixture, `GET ${target} HTTP/1.1\r\n${headers}`), /^HTTP\/1.1 401 /);
  assert.match(await request(fixture, `GET http://example.invalid/ HTTP/1.1\r\n${headers}`), /^HTTP\/1.1 502 /);
});

test('shutdown closes established tunnels and pending idle HTTP sockets', { timeout: 10000 }, async t => {
  const fixture = await startFixture(t);
  const tunnel = net.connect(fixture.proxyPort, '127.0.0.1');
  const idle = net.connect(fixture.originPort, '127.0.0.1');
  t.after(() => { tunnel.destroy(); idle.destroy(); });
  await Promise.all([once(tunnel, 'connect'), once(idle, 'connect')]);
  tunnel.write(connectPacket(`127.0.0.1:${fixture.originPort}`));
  const [header] = await once(tunnel, 'data');
  assert.match(header.toString(), /^HTTP\/1.1 200 /);
  tunnel.resume();
  idle.resume();
  const closed = Promise.all([once(tunnel, 'close'), once(idle, 'close')]);
  fixture.child.kill('SIGTERM');
  await closed;
  assert.deepEqual(await fixture.exited, [0, null]);
});

test('read-phase navigation never seeds a canary; the explicit button does', { timeout: 10000 }, async t => {
  const fixture = await startFixture(t, ['0', 'read', 'owned-read-phase-canary']);
  const page = await (await fetch(`http://127.0.0.1:${fixture.originPort}/persistence`)).text();
  assert.match(page, /onclick="writeCanary\(\)"/);
  const script = page.match(/<script>([\s\S]*?)<\/script>/)?.[1];
  assert.ok(script);
  let cookie = '';
  let stored = null;
  let writes = 0;
  const reports = [];
  const document = {
    get cookie() { return cookie; },
    set cookie(value) { writes++; cookie = value; },
    querySelector: () => ({textContent:''}),
  };
  const browser = vm.createContext({
    document,
    localStorage: {getItem:() => stored, setItem:(_key, value) => { writes++; stored = value; }},
    fetch: url => { reports.push(url); return Promise.resolve(); },
  });
  vm.runInContext(script, browser, {timeout:1000});
  assert.equal(writes, 0);
  assert.equal(reports.at(-1), '/persistence-report?cookie=false&storage=false');
  vm.runInContext('writeCanary()', browser, {timeout:1000});
  assert.equal(writes, 2);
  assert.equal(reports.at(-1), '/persistence-report?cookie=true&storage=true');
});
