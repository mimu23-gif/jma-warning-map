'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const { createGasMocks } = require('./gas-mocks');
const { prefetchAll } = require('./prefetch');

const PORT = process.env.PORT || 8787;
const REPO_ROOT = path.join(__dirname, '..');
const CONTEXT_TTL_MS = 60 * 1000; // Code.js 内のWARN_MAP_R8キャッシュ(60秒)と揃える

function buildContext(prefetched) {
  const mocks = createGasMocks(prefetched);
  const sandbox = {
    console, JSON, Date, Map, Set, Array, Object, String, Number, Boolean, RegExp, Math,
    ...mocks
  };
  const context = vm.createContext(sandbox);
  for (const fname of ['Code.js', 'Points.js']) {
    const src = fs.readFileSync(path.join(REPO_ROOT, fname), 'utf8');
    vm.runInContext(src, context, { filename: fname });
  }
  return context;
}

let cachedContext = null;
let cachedAt = 0;

async function getContext(forceRefresh) {
  const now = Date.now();
  if (!forceRefresh && cachedContext && now - cachedAt < CONTEXT_TTL_MS) return cachedContext;
  const { prefetched } = await prefetchAll();
  cachedContext = buildContext(prefetched);
  cachedAt = now;
  return cachedContext;
}

// google.script.run の withSuccessHandler/withFailureHandler チェーンを
// fetch('/api/call/<関数名>') に置き換える最小限のシム。
const RUN_SHIM = `
<script>
(function(){
  function createRunner(successFn, failureFn) {
    var runner = {
      withSuccessHandler: function(fn) { return createRunner(fn, failureFn); },
      withFailureHandler: function(fn) { return createRunner(successFn, fn); }
    };
    return new Proxy(runner, {
      get: function(target, prop) {
        if (prop in target) return target[prop];
        return function() {
          var args = Array.prototype.slice.call(arguments);
          fetch('/api/call/' + prop, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(args)
          })
            .then(function(r) { return r.json(); })
            .then(function(data) {
              if (data && data.__error__) {
                if (failureFn) failureFn(data.__error__); else console.error(data.__error__);
              } else if (successFn) {
                successFn(data);
              }
            })
            .catch(function(err) {
              if (failureFn) failureFn(err); else console.error(err);
            });
        };
      }
    });
  }
  window.google = window.google || {};
  window.google.script = window.google.script || {};
  window.google.script.run = createRunner(null, null);
})();
</script>
`;

function serveMapHtml(res) {
  const html = fs.readFileSync(path.join(REPO_ROOT, 'MAP.html'), 'utf8');
  const withShim = html.replace('<head>', '<head>' + RUN_SHIM);
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(withShim);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => resolve(body));
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  try {
    if (req.method === 'GET' && (req.url === '/' || req.url === '/index.html')) {
      serveMapHtml(res);
      return;
    }

    if (req.method === 'POST' && req.url.startsWith('/api/call/')) {
      const fname = decodeURIComponent(req.url.slice('/api/call/'.length));
      const bodyText = await readBody(req);
      const args = bodyText ? JSON.parse(bodyText) : [];

      console.log(`[server] call ${fname}(${args.map(a => JSON.stringify(a)).join(', ')})`);

      let context;
      try {
        context = await getContext(false);
      } catch (e) {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ __error__: 'プリフェッチ失敗: ' + String(e) }));
        return;
      }

      const fn = context[fname];
      if (typeof fn !== 'function') {
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ __error__: '未知の関数: ' + fname }));
        return;
      }

      try {
        const result = fn.apply(null, args);
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify(result));
      } catch (e) {
        console.error(e);
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ __error__: String(e && e.stack || e) }));
      }
      return;
    }

    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('not found');
  } catch (e) {
    console.error(e);
    res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('internal error: ' + String(e));
  }
});

server.listen(PORT, () => {
  console.log(`Local GAS preview server: http://localhost:${PORT}`);
});
