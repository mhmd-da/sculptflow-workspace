"use strict";

const $ = (id) => document.getElementById(id);
const SMOKE_SUITES = ["health", "auth", "security", "pages"];
const NEED_LABEL = { ingestkey: "ingest key", adminkey: "admin key", metasecret: "meta secret", database: "database", browser: "browser" };
const STATUS_LABEL = { passed: "Passed", failed: "Failed", skipped: "Skipped", running: "Running", pending: "Pending" };

let catalog = [];
let environments = [];
let pollTimer = null;
let currentRunId = null;
const openLogs = new Set();

function store(key, value) {
  try { if (value === undefined) return JSON.parse(localStorage.getItem(key)); localStorage.setItem(key, JSON.stringify(value)); }
  catch { return null; }
}

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "class") node.className = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else if (v !== null && v !== undefined && v !== false) node.setAttribute(k, v === true ? "" : v);
  }
  for (const c of children.flat()) if (c !== null && c !== undefined) node.append(c);
  return node;
}

async function api(path, options) {
  const res = await fetch(path, { headers: { "Content-Type": "application/json" }, ...options });
  const body = res.headers.get("content-type")?.includes("json") ? await res.json() : null;
  if (!res.ok) throw new Error(body?.error || `Request failed (${res.status})`);
  return body;
}

// ---- picker ---------------------------------------------------------------------------------------------------

function renderSuites() {
  const saved = new Set(store("sf-selected") || catalog.flatMap(s => s.tests.map(t => t.id)));
  const root = $("suites");
  root.replaceChildren();
  for (const suite of catalog) {
    const suiteBox = el("input", { type: "checkbox", "aria-label": `Select all ${suite.name} tests` });
    const tests = suite.tests.map(t => {
      const box = el("input", { type: "checkbox", value: t.id, "data-suite": suite.id, "data-finding": t.finding || "" });
      box.checked = saved.has(t.id);
      box.addEventListener("change", () => { syncSuite(suite.id); onSelectionChanged(); });
      const badges = [
        ...t.risks.map(r => el("span", { class: "risk", title: "Skipped unless allowed" }, r)),
        ...t.needs.filter(n => n !== "session").map(n => el("span", { class: "need" }, NEED_LABEL[n] || n)),
        t.finding ? el("span", { class: "risk", title: "Reproduces this QA finding: fails until it is fixed" }, `QA ${t.finding}`) : null,
      ];
      return el("li", {}, el("label", { title: t.id }, box, el("span", { class: "tname" }, t.name), badges));
    });
    suiteBox.addEventListener("click", (e) => e.stopPropagation());
    suiteBox.addEventListener("change", () => {
      root.querySelectorAll(`input[data-suite="${suite.id}"]`).forEach(b => (b.checked = suiteBox.checked));
      onSelectionChanged();
    });
    const details = el("details", { class: "suite", "data-id": suite.id },
      el("summary", {}, suiteBox, el("span", { class: "name" }, suite.name), el("span", { class: "count", "data-count": suite.id })),
      el("p", { class: "desc" }, suite.description),
      el("ul", {}, tests));
    root.append(details);
    syncSuite(suite.id);
  }
  onSelectionChanged();
}

function syncSuite(suiteId) {
  const boxes = [...document.querySelectorAll(`input[data-suite="${suiteId}"]`)];
  const on = boxes.filter(b => b.checked).length;
  const suiteBox = document.querySelector(`.suite[data-id="${suiteId}"] summary input`);
  suiteBox.checked = on === boxes.length;
  suiteBox.indeterminate = on > 0 && on < boxes.length;
  document.querySelector(`[data-count="${suiteId}"]`).textContent = `${on}/${boxes.length}`;
}

function selectedIds() {
  return [...document.querySelectorAll("#suites li input:checked")].map(b => b.value);
}

function onSelectionChanged() {
  const ids = selectedIds();
  store("sf-selected", ids);
  $("selCount").textContent = `${ids.length} selected`;
  $("runBtn").disabled = ids.length === 0 || pollTimer !== null;
}

function applyPreset(preset) {
  document.querySelectorAll("#suites li input").forEach(b => {
    b.checked = preset === "all" || (preset === "smoke" && SMOKE_SUITES.includes(b.dataset.suite)) || (preset === "findings" && b.dataset.finding !== "");
  });
  catalog.forEach(s => syncSuite(s.id));
  onSelectionChanged();
}

// ---- environment ----------------------------------------------------------------------------------------------

function renderEnvInfo() {
  const env = environments.find(e => e.name === $("env").value);
  const info = $("envInfo");
  info.replaceChildren();
  if (!env) return;
  store("sf-env", env.name);
  info.append(
    el("span", { class: "tag" }, env.baseUrl),
    el("span", { class: env.hasIngestKey ? "tag ok" : "tag" }, env.hasIngestKey ? "ingest key ✓" : "no ingest key"),
    el("span", { class: env.hasAdminKey ? "tag ok" : "tag warn", title: env.hasAdminKey ? "" : "Without it the test clients can't be deleted after the run" },
      env.hasAdminKey ? "admin key ✓" : "no admin key"),
    el("span", { class: env.hasMetaSecret ? "tag ok" : "tag" }, env.hasMetaSecret ? "meta secret ✓" : "no meta secret"),
    el("span", { class: env.hasDatabase ? "tag ok" : "tag" }, env.hasDatabase ? "database ✓" : "no database"));
  if (!env.isLocal) info.append(el("span", { class: "tag warn" }, "live system"));
}

// ---- runs -----------------------------------------------------------------------------------------------------

async function startRun() {
  const env = environments.find(e => e.name === $("env").value);
  if (!env.isLocal && !confirm(`Run against ${env.name} (${env.baseUrl})?\n\nIt registers temporary [Automation] clients there and deletes them at the end.`)) return;
  showNotice(null);
  try {
    const { id } = await api("/api/runs", {
      method: "POST",
      body: JSON.stringify({
        environment: env.name,
        testIds: selectedIds(),
        allowExternal: $("allowExternal").checked,
        allowMessaging: $("allowMessaging").checked,
        allowBilling: $("allowBilling").checked,
        allowShared: $("allowShared").checked,
        allowAi: $("allowAi").checked,
        keepTestData: $("keepData").checked,
      }),
    });
    openLogs.clear();
    watchRun(id);
  } catch (e) { showNotice(e.message); }
}

function watchRun(id) {
  currentRunId = id;
  $("current").hidden = false;
  $("reportLink").hidden = true;
  $("runBtn").disabled = true;
  $("stopBtn").hidden = false;
  clearInterval(pollTimer);
  pollTimer = setInterval(() => refreshRun(id), 1000);
  refreshRun(id);
}

async function refreshRun(id) {
  let run;
  try { run = await api(`/api/runs/${id}`); } catch { return; }
  renderRun(run);
  if (run.finishedAt) {
    clearInterval(pollTimer);
    pollTimer = null;
    $("stopBtn").hidden = true;
    onSelectionChanged();
    setTimeout(loadHistory, 800);
    const link = $("reportLink");
    link.href = `/runs/${run.id}/report`;
    link.hidden = false;
  }
}

function renderRun(run) {
  const done = run.passed + run.failed + run.skipped;
  $("phase").textContent = run.phase;
  $("cPassed").textContent = `${run.passed} passed`;
  $("cFailed").textContent = `${run.failed} failed`;
  $("cSkipped").textContent = `${run.skipped} skipped`;
  $("bar").style.width = run.total ? `${(done / run.total) * 100}%` : "0";
  $("runLog").textContent = run.runLog.join("\n");

  const list = $("results");
  list.replaceChildren(...run.results.map(r => {
    const details = el("details", { open: openLogs.has(r.id) },
      el("summary", {},
        el("span", { class: `pill ${r.status}` }, STATUS_LABEL[r.status] || r.status),
        el("span", { class: "rname" }, r.name, " ", el("span", { class: "rid" }, r.id), r.finding ? el("span", { class: "risk" }, ` QA ${r.finding}`) : null),
        el("span", { class: "ms" }, r.status === "pending" || r.status === "running" ? "" : `${r.durationMs} ms`),
        r.message ? el("span", { class: `msg ${r.status}` }, r.message) : null),
      r.log.length ? el("pre", {}, r.log.join("\n")) : el("pre", {}, "No requests logged."));
    details.addEventListener("toggle", () => details.open ? openLogs.add(r.id) : openLogs.delete(r.id));
    return el("li", {}, details);
  }));
}

async function loadHistory() {
  const runs = await api("/api/runs");
  const list = $("history");
  list.replaceChildren(...(runs.length ? runs.map(r => el("li", {},
    el("span", {}, new Date(r.startedAt).toLocaleString()),
    el("span", { class: "tag" }, r.environment),
    el("span", { class: "pill passed" }, `${r.passed} passed`),
    r.failed ? el("span", { class: "pill failed" }, `${r.failed} failed`) : null,
    r.skipped ? el("span", { class: "pill skipped" }, `${r.skipped} skipped`) : null,
    el("a", { href: `/runs/${r.id}/report`, target: "_blank" }, "Report"),
    el("a", { href: "#", onclick: (e) => { e.preventDefault(); openLogs.clear(); showOld(r.id); } }, "Show here"),
  )) : [el("li", { class: "muted" }, "No runs yet.")]));
}

async function showOld(id) {
  clearInterval(pollTimer);
  pollTimer = null;
  const run = await api(`/api/runs/${id}`);
  $("current").hidden = false;
  renderRun(run);
  $("reportLink").href = `/runs/${id}/report`;
  $("reportLink").hidden = false;
  onSelectionChanged();
}

async function cleanup() {
  const env = $("env").value;
  if (!confirm(`Delete every leftover [Automation] test client on ${env}?`)) return;
  try {
    const { messages } = await api(`/api/cleanup/${encodeURIComponent(env)}`, { method: "POST" });
    showNotice(messages.join("\n"));
  } catch (e) { showNotice(e.message); }
}

function showNotice(text) {
  $("notice").hidden = !text;
  $("notice").textContent = text || "";
}

// ---- boot -----------------------------------------------------------------------------------------------------

(async function init() {
  let defaultEnv;
  [catalog, { default: defaultEnv, environments }] = await Promise.all([api("/api/catalog"), api("/api/environments")]);
  const select = $("env");
  for (const e of environments) select.append(el("option", { value: e.name }, e.name));
  const savedEnv = store("sf-env");
  select.value = environments.some(e => e.name === savedEnv) ? savedEnv : defaultEnv;
  select.addEventListener("change", renderEnvInfo);
  renderEnvInfo();
  renderSuites();
  document.querySelectorAll("[data-preset]").forEach(b => b.addEventListener("click", () => applyPreset(b.dataset.preset)));
  $("runBtn").addEventListener("click", startRun);
  $("stopBtn").addEventListener("click", () => currentRunId && api(`/api/runs/${currentRunId}/stop`, { method: "POST" }));
  $("cleanupBtn").addEventListener("click", cleanup);
  loadHistory();
})();
