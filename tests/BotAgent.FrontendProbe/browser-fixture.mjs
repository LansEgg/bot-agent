// Independent synthetic DOM acceptance. Never serves the production panel or data.
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
const here = path.dirname(fileURLToPath(import.meta.url));
const appPath = path.resolve(here, "../../src/BotAgent.Headless/wwwroot/app.js");
export function extractSessionSource(js) {
  const section = (start, end) => {
    const first = js.indexOf(start), last = js.indexOf(end, first + start.length);
    if (first < 0 || last < 0 || js.indexOf(start, first + start.length) >= 0)
      throw new Error(`Missing or ambiguous source boundary: ${start}`);
    return js.slice(first, last);
  };
  return [section("  function escapeHtml(value)", "  /*"),
    section("  async function api(path, options)", "  ///"),
    section("    let agentSessionsCache = [];", '    $("agentPromptReset")')].join("\n");
}
const html = `<!doctype html><html><head><meta charset="utf-8"><title>Issue 48 synthetic acceptance</title></head><body>
<select id="agentSessionChat"></select><button id="agentSessionRefresh">Refresh</button><button id="agentSessionImport">Import</button>
<div id="agentSessionTable"></div><pre id="acceptanceResult">Running</pre>
<script src="/acceptance.js"></script><script src="/source.js"></script>
<script>runAcceptance().then(r => { window.acceptanceResult = r; document.getElementById('acceptanceResult').textContent = JSON.stringify(r, null, 2); });</script>
</body></html>`;
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const server = http.createServer((req, res) => {
    res.setHeader("Cache-Control", "no-store");
    if (req.url === "/") { res.setHeader("Content-Type", "text/html; charset=utf-8"); res.end(html); }
    else if (req.url === "/source.js") { res.setHeader("Content-Type", "text/javascript; charset=utf-8"); res.end(extractSessionSource(fs.readFileSync(appPath, "utf8"))); }
    else if (req.url === "/acceptance.js") { res.setHeader("Content-Type", "text/javascript; charset=utf-8"); res.end(fs.readFileSync(path.join(here, "browser-acceptance.js"), "utf8")); }
    else { res.writeHead(404); res.end(); }
  });
  server.listen(0, "127.0.0.1", () => console.log(`SYNTHETIC_FIXTURE_URL=http://127.0.0.1:${server.address().port}/`));
}
