// Disposable shell fixture. External forwarding is opt-in and limited to the two
// production TLS health peers; it never terminates TLS or changes certificate checks.
import http from 'node:http';
import net from 'node:net';

// Optional reproducible persistence run: fixed port, write/read phase, unique canary.
// Read-phase navigation never writes storage. The labeled button writes only on
// an explicit test-user click, so route changes cannot silently seed a canary.
const [portArgument = '0', persistencePhase = '', persistenceCanary = '', healthMode = ''] = process.argv.slice(2);
const requestedPort = Number(portArgument);
if (!Number.isInteger(requestedPort) || requestedPort < 0 || requestedPort > 65535
    || (persistencePhase !== '' && persistencePhase !== 'write' && persistencePhase !== 'read')
    || (persistencePhase !== '' && !/^[a-zA-Z0-9-]{16,80}$/.test(persistenceCanary))
    || (healthMode !== '' && healthMode !== '--allow-health-peers') || process.argv.length > 6) {
  throw new Error('Expected optional port, write/read phase, unique canary and --allow-health-peers.');
}
const sockets = new Set();
const delayedResponses = new Set();
const trackSocket = socket => {
  sockets.add(socket);
  socket.setTimeout(30000, () => socket.destroy());
  socket.on('error', () => socket.destroy());
  socket.once('close', () => sockets.delete(socket));
  return socket;
};
const credentials = 'Basic ' + Buffer.from('fixture:fixture').toString('base64');
const counters = {originRequests:0, serverAuthenticated:0, proxyRequests:0, proxyAuthenticated:0, connectAttempts:0, connectAuthenticated:0, connectAllowed:0};
const proxyAuthenticated = request => {
  let count = 0;
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index].toLowerCase() === 'proxy-authorization') count++;
  }
  return count === 1 && request.headers['proxy-authorization'] === credentials;
};
const html = (body) => `<!doctype html><meta charset="utf-8"><style>body{font:18px system-ui;background:#eef3f8;color:#142638;padding:22px}button{font:inherit;padding:12px;margin:8px}pre{white-space:pre-wrap}h1{font-size:26px}</style>${body}`;
const parent = html(`<h1>Asura disposable browser fixture</h1>
<p>Fixture pages are loopback-only. Fake credentials: fixture / fixture.</p>
<button id="open" onclick="popup=window.open('', 'fixture');status('WindowProxy: '+!!popup);setTimeout(()=>{if(popup&&!popup.closed)popup.location='/child'},450)">Open blank popup, then navigate</button>
<button onclick="count++;status('Parent clicks: '+count)">Parent remains interactive</button>
<button onclick="popup?.postMessage('reply',location.origin)">Reply to popup</button>
<button onclick="popup=window.open('', 'pending');setTimeout(()=>{if(popup&&!popup.closed)popup.location='/slow'},250)">Open slow popup (close it)</button>
<button onclick="location='/auth'">Server authentication</button><pre id="status">Ready</pre>
<script>let popup,count=0;function status(v){document.querySelector('#status').textContent+='\\n'+v}addEventListener('message',e=>{if(e.origin===location.origin)status('Message: '+e.data)})</script>`);
const child = html(`<h1>Hosted child: visible origin above</h1><p id="proof"></p>
<button onclick="window.close()">window.close()</button>
<button onclick="window.open('/nested','nested')">Nested popup</button><pre id="messages"></pre>
<script>document.querySelector('#proof').textContent='Opener exists: '+!!opener+'; cookie: '+document.cookie;opener?.postMessage('child-ready',location.origin);addEventListener('message',e=>{if(e.origin===location.origin){document.querySelector('#messages').textContent+=e.data+'\\n';opener?.postMessage('child-received-'+e.data,location.origin)}})</script>`);
const persistence = html(`<h1>Disposable session persistence</h1><button onclick="document.cookie='persistentSmoke=verified; Max-Age=86400; Path=/; SameSite=Lax';localStorage.setItem('smoke','verified');show()">Sign in with synthetic session</button><pre id="state"></pre><script>function show(){document.querySelector('#state').textContent='Cookie: '+document.cookie+'; storage: '+localStorage.getItem('smoke')}show()</script>`);
const canaryPersistence = html(`<h1>Disposable persistence ${persistencePhase} proof</h1><button id="write-canary" onclick="writeCanary()">Write this synthetic canary</button><pre id="state"></pre><script>
const expected=${JSON.stringify(persistenceCanary)};
function writeCanary(){document.cookie='sealCanary='+expected+'; Max-Age=86400; Path=/; SameSite=Lax';localStorage.setItem('sealCanary',expected);show()}
${persistencePhase === 'write' ? "document.cookie='sealCanary='+expected+'; Max-Age=86400; Path=/; SameSite=Lax';localStorage.setItem('sealCanary',expected);" : ''}
function show(){
const cookie=document.cookie.split('; ').find(value=>value.startsWith('sealCanary='))?.slice(11)===expected;
const storage=localStorage.getItem('sealCanary')===expected;
document.querySelector('#state').textContent='Cookie matches: '+cookie+'; localStorage matches: '+storage;
fetch('/persistence-report?cookie='+cookie+'&storage='+storage,{cache:'no-store'});
}show();
</script>`);
const server = http.createServer((request, response) => {
  counters.originRequests++;
  if (request.headers.authorization === credentials) counters.serverAuthenticated++;
  console.log(JSON.stringify({source:'origin', method:request.method, url:request.url, authenticated:request.headers.authorization===credentials}));
  if (request.url === '/fixture-status') {
    response.writeHead(200, {'Content-Type':'application/json', 'Cache-Control':'no-store'});
    response.end(JSON.stringify(counters)); return;
  }
  if (request.url === '/auth' && request.headers.authorization !== credentials) {
    response.writeHead(401, {'WWW-Authenticate':'Basic realm="asura-fixture"', 'Content-Type':'text/html'});
    response.end(html('<h1>Authentication required</h1><p>No credentials were supplied to this fixture origin.</p>'));
    return;
  }
  response.setHeader('Content-Type', 'text/html; charset=utf-8');
  response.setHeader('Cache-Control', 'no-store');
  if (request.url?.startsWith('/persistence-report?')) {
    const report = new URL(request.url, 'http://127.0.0.1');
    console.log(JSON.stringify({source:'persistence', phase:persistencePhase,
      cookie:report.searchParams.get('cookie')==='true', storage:report.searchParams.get('storage')==='true',
      serverCookie:request.headers.cookie?.split('; ').includes('sealCanary='+persistenceCanary)===true}));
    response.end('Recorded'); return;
  }
  if (request.url === '/') response.setHeader('Set-Cookie','smoke=loopback; Path=/; SameSite=Lax');
  const body = request.url === '/auth' ? html('<h1>Authenticated with fixture credentials</h1>')
    : request.url === '/persistence' ? (persistencePhase ? canaryPersistence : persistence) : request.url === '/' ? parent : child;
  if (request.url === '/slow') {
    const timer = setTimeout(() => { delayedResponses.delete(timer); response.end(body); }, 2500);
    delayedResponses.add(timer);
  }
  else response.end(body);
});
server.on('connection', trackSocket);
await new Promise(resolve => server.listen(requestedPort, '127.0.0.1', resolve));
const originPort = server.address().port;
const proxy = http.createServer((request, response) => {
  counters.proxyRequests++;
  if (proxyAuthenticated(request)) counters.proxyAuthenticated++;
  console.log(JSON.stringify({source:'proxy', method:request.method, url:request.url, authenticated:proxyAuthenticated(request)}));
  if (!proxyAuthenticated(request)) {
    response.writeHead(407, {'Proxy-Authenticate':'Basic realm="asura-proxy-fixture"'}); response.end(); return;
  }
  let url;
  try { url = new URL(request.url); } catch { response.writeHead(400); response.end(); return; }
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || Number(url.port) !== originPort) {
    response.writeHead(502); response.end('Fixture refuses non-loopback destinations'); return;
  }
  const headers = {...request.headers}; delete headers['proxy-authorization'];
  const upstream = http.request({host:'127.0.0.1', port:originPort, path:url.pathname+url.search, method:request.method, headers}, incoming => {
    response.writeHead(incoming.statusCode, incoming.headers); incoming.pipe(response);
  });
  upstream.on('socket', trackSocket);
  upstream.on('error', () => { response.writeHead(502); response.end(); }); request.pipe(upstream);
});
// Exact string lookup deliberately rejects alternate numeric hosts, user-info,
// DNS names, encoded authority text and noncanonical ports before opening a socket.
const connectDestinations = new Map([
  [`127.0.0.1:${originPort}`, {host:'127.0.0.1', port:originPort}],
]);
if (healthMode === '--allow-health-peers') {
  connectDestinations.set('1.1.1.1:443', {host:'1.1.1.1', port:443});
  connectDestinations.set('1.0.0.1:443', {host:'1.0.0.1', port:443});
}
proxy.on('connection', trackSocket);
proxy.on('connect', (request, socket, head) => {
  const authenticated = proxyAuthenticated(request);
  const destination = connectDestinations.get(request.url);
  counters.connectAttempts++;
  if (authenticated) counters.connectAuthenticated++;
  if (authenticated && destination) counters.connectAllowed++;
  console.log(JSON.stringify({source:'connect', authenticated, allowed:destination !== undefined}));
  if (!authenticated) {
    socket.end('HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm="asura-proxy-fixture"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
    return;
  }
  if (!destination) {
    socket.end('HTTP/1.1 502 Fixture refuses destination\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
    return;
  }
  const upstream = trackSocket(net.connect(destination));
  const deadline = setTimeout(() => upstream.destroy(new Error('Fixture connect deadline')), 5000);
  let connected = false;
  socket.once('close', () => upstream.destroy());
  upstream.once('close', () => { clearTimeout(deadline); socket.destroy(); });
  upstream.once('error', () => {
    if (!connected) socket.end('HTTP/1.1 502 Fixture connection failed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
  });
  upstream.once('connect', () => {
    clearTimeout(deadline);
    if (socket.destroyed) { upstream.destroy(); return; }
    connected = true;
    socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    // Node hands bytes following the CONNECT headers to this callback. Do not
    // drop a pipelined TLS ClientHello or HTTP canary already in that buffer.
    if (head.length) upstream.write(head);
    socket.pipe(upstream);
    upstream.pipe(socket);
  });
});
await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
console.log(JSON.stringify({origin:`http://127.0.0.1:${originPort}/`, proxy:`http://127.0.0.1:${proxy.address().port}`, username:'fixture',password:'fixture', healthPeers:healthMode === '--allow-health-peers'}));
const lifetime = setTimeout(stop, 30 * 60 * 1000);
function stop() {
  clearTimeout(lifetime);
  for (const timer of delayedResponses) clearTimeout(timer);
  delayedResponses.clear();
  server.close();
  proxy.close();
  for (const socket of sockets) socket.destroy();
}
for (const signal of ['SIGINT','SIGTERM']) process.on(signal, stop);
