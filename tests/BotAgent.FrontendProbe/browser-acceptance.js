// Test glue/assertions only. Production rendering/handlers are extracted unchanged.
const $ = (id) => document.getElementById(id);
const withToken = (value) => value;
const authHeaders = (headers) => headers;
const toast = () => {};
const showPanelAuth = () => { throw new Error("Unexpected authentication path"); };
window.confirm = () => true;
window.__xssExecuted = 0;
const errors = [];
window.addEventListener("error", (e) => { errors.push(e.message); e.preventDefault(); });
window.addEventListener("unhandledrejection", (e) => { errors.push(String(e.reason)); e.preventDefault(); });
const hostile = `\"'><img data-attacker src=x onerror="window.__xssExecuted++"><svg data-attacker onload="window.__xssExecuted++"></svg><script data-attacker>window.__xssExecuted++</script>&`;
const chatKey = `synthetic-chat:${hostile}[]:#\\`;
const ids = [`quote\"'<>[]:#\\ &`, `slash\\41 [id]#.:>+~*`, `x\"], [data-sess-runs-box=\"other`, hostile];
const piId = `synthetic-pi:${hostile}[]:#\\`;
const piSessions = [
  { id: piId, title: hostile, cwd: `cwd:${hostile}` },
  { id: `${piId}:compact`, title: '<svg onload=__xssExecuted++>', cwd: '<img src=x onerror=__xssExecuted++>' }
];
const calls = [];
let failEndpoint = "";
const sessions = ids.map((id, i) => ({ id, name: `title-${i}:${hostile}`, backend: "host", device: `device:${hostile}`, turns: 1, piSession: `pi:${hostile}`, historyChars: 2,
  runs: [{ prompt: `prompt-${i}:${hostile}`, result: `result-${i}:${hostile}`, ok: false, durationMs: 1, toolCalls: 1 }] }));
const payload = { total: sessions.length, chatCount: 1, chats: { [chatKey]: { name: `chat:${hostile}`, sessions } } };
window.fetch = async (url, options = {}) => {
  if (!["/api/agent/sessions", "/api/agent/pi-sessions"].includes(url)) throw new Error("Non-fixture fetch blocked");
  const body = options.body ? JSON.parse(options.body) : null;
  calls.push({ url, method: options.method || "GET", body });
  if (url === failEndpoint) throw new Error(`error:${hostile}`);
  const data = url === "/api/agent/pi-sessions" ? { connected: true, sessions: piSessions } : body ? { message: "synthetic accepted" } : payload;
  return { ok: true, status: 200, text: async () => JSON.stringify(data) };
};
async function runAcceptance() {
  const passed = [], failed = [];
  const check = (name, ok) => (ok ? passed : failed).push(name);
  const settle = async () => { await new Promise((r) => setTimeout(r, 30)); };
  const safe = (stage) => {
    check(`${stage}: no executed script/event marker`, window.__xssExecuted === 0);
    check(`${stage}: no attacker nodes or handlers`, document.scripts.length === 3 && !document.body.querySelector("img,svg,[data-attacker],[onerror],[onload],[onclick],[onfocus]"));
  };
  await refreshAgentSessions();
  check("overview: literal chat title", $("agentSessionTable").textContent.includes(`chat:${hostile}`));
  check("overview: literal session titles", sessions.every((s) => $("agentSessionTable").textContent.includes(s.name)));
  check("overview: exact option key/text", $("agentSessionChat").options[1].value === chatKey && $("agentSessionChat").options[1].textContent.includes(`chat:${hostile}`));
  safe("overview");
  $("agentSessionChat").value = chatKey; $("agentSessionChat").dispatchEvent(new Event("change")); await settle();
  check("detail: literal title/device/pi/prompt/result", sessions.every((s) => [s.name, s.device, s.piSession, s.runs[0].prompt, s.runs[0].result].every((v) => $("agentSessionTable").textContent.includes(v))));
  for (const attr of ["use", "rename", "reset", "del", "runs", "runs-box"]) {
    const els = Array.from($("agentSessionTable").querySelectorAll(`[data-sess-${attr}]`));
    check(`detail: exact data-sess-${attr} roundtrip`, els.length === ids.length && els.every((el, i) => el.getAttribute(`data-sess-${attr}`) === ids[i]));
  }
  safe("detail");
  const buttons = Array.from($("agentSessionTable").querySelectorAll("[data-sess-runs]")), boxes = Array.from($("agentSessionTable").querySelectorAll("[data-sess-runs-box]"));
  for (let i = 0; i < buttons.length; i++) {
    buttons[i].click(); check(`expansion ${i}: opens only exact matching ID`, boxes.every((box, j) => box.style.display === (i === j ? "" : "none")));
    buttons[i].click(); check(`expansion ${i}: collapses exact matching ID`, boxes.every((box) => box.style.display === "none"));
  }
  for (const [attr, action] of [["use", "use"], ["del", "delete"]]) {
    for (let i = 0; i < ids.length; i++) {
      $("agentSessionTable").querySelectorAll(`[data-sess-${attr}]`)[i].click(); await settle();
      check(`${action} ${i}: fake POST retains exact chat/ID`, calls.some((c) => c.body?.action === action && c.body.key === chatKey && c.body.id === ids[i]));
    }
    safe(action);
  }
  $("agentSessionImport").click(); await settle();
  check("pi: literal truncated and compact title/cwd", piSessions.every((s, i) => $("agentSessionTable").querySelectorAll("b")[i]?.textContent === s.title.slice(0, 40) && $("agentSessionTable").textContent.includes(s.cwd)));
  check("pi: exact import attribute roundtrip", piSessions.every((s, i) => $("agentSessionTable").querySelectorAll("[data-pi-import]")[i]?.dataset.piImport === s.id)); safe("pi");
  for (let i = 0; i < piSessions.length; i++) {
    if (i) { $("agentSessionImport").click(); await settle(); }
    $("agentSessionTable").querySelectorAll("[data-pi-import]")[i].click(); await settle();
    check(`import ${i}: fake POST retains exact chat/pi ID`, calls.some((c) => c.body?.action === "import" && c.body.key === chatKey && c.body.piSession === piSessions[i].id));
  }
  safe("import");
  failEndpoint = "/api/agent/pi-sessions"; $("agentSessionImport").click(); await settle();
  check("pi error: literal error message", $("agentSessionTable").textContent.includes(`error:${hostile}`)); safe("pi error");
  failEndpoint = "/api/agent/sessions"; await refreshAgentSessions();
  check("sessions error: literal error message", $("agentSessionTable").textContent.includes(`error:${hostile}`)); safe("sessions error");
  check("no browser errors or unhandled rejections", errors.length === 0);
  check("only fake-fetch endpoints/mutations used", calls.every((c) => ["/api/agent/sessions", "/api/agent/pi-sessions"].includes(c.url)));
  return { passed: passed.length, failed: failed.length, failures: failed, errors, marker: window.__xssExecuted, fakeCalls: calls.length, userAgent: navigator.userAgent };
}
