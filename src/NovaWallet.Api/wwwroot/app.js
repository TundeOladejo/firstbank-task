// NovaWallet console — thin client over the same public /api endpoints.
// No secrets, no logic duplication: the browser just calls the API and renders results.
// State (token, wallet IDs, last transfer) is persisted to localStorage so a page
// refresh restores the session automatically.

let token = null;
let historyTab = "stmt";

const SECTIONS = {
  dashboard: { title: "Dashboard", sub: "Overview of your NovaWallet activity" },
  wallets:   { title: "Wallets",   sub: "Create wallets, credit funds, and check balances" },
  transfer:  { title: "Transfer",  sub: "Move funds atomically between wallets" },
  history:   { title: "History",   sub: "Statement and append-only audit trail" },
};

// Fields whose values are saved to localStorage and restored on reload.
const PERSISTED_FIELDS = ["balWallet", "crWallet", "tfFrom", "tfTo", "hsWallet", "tfKey",
                          "cwCustomer", "crAmount", "tfAmount", "authSubject", "authCustomer"];

const STORAGE_KEY = "nw_session";

const $ = (id) => document.getElementById(id);

// ---- persistence helpers ----

function saveSession() {
  const fields = {};
  PERSISTED_FIELDS.forEach((id) => { const el = $(id); if (el) fields[id] = el.value; });
  const session = {
    token,
    subject: $("accountName")?.textContent,
    fields,
    lastTransfer: window._lastTransfer || null,
  };
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(session)); } catch { /* storage full */ }
}

function loadSession() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    return JSON.parse(raw);
  } catch { return null; }
}

function clearSession() {
  localStorage.removeItem(STORAGE_KEY);
}

// ---- money helpers (string/BigInt math, no floating point) ----

function nairaToKobo(naira) {
  const s = String(naira).trim();
  if (!/^\d+(\.\d{1,2})?$/.test(s)) return null;
  const [whole, frac = ""] = s.split(".");
  return BigInt(whole) * 100n + BigInt((frac + "00").slice(0, 2));
}

function koboToNaira(kobo) {
  const n = BigInt(kobo);
  const sign = n < 0n ? "-" : "";
  const abs  = n < 0n ? -n : n;
  return `${sign}₦${(abs / 100n).toLocaleString("en-NG")}.${(abs % 100n).toString().padStart(2, "0")}`;
}

function toast(kind, title, detail) {
  const t = $("toast");
  t.className = `toast show ${kind}`;
  t.innerHTML = `<span class="t-title">${title}</span>${detail ? `<span class="t-detail">${detail}</span>` : ""}`;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => (t.className = "toast"), 4200);
}

async function api(method, path, body, extraHeaders = {}) {
  const headers = { "Content-Type": "application/json", ...extraHeaders };
  if (token) headers["Authorization"] = `Bearer ${token}`;
  const res = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    const title  = (data && (data.title || data.error)) || `HTTP ${res.status}`;
    const detail = (data && data.detail) || (typeof data === "string" ? data : "");
    const err = new Error(detail || title);
    err.problem = { status: res.status, title, detail };
    throw err;
  }
  return { data, headers: res.headers, status: res.status };
}

function requireToken() {
  if (!token) { toast("err", "Sign in first", "Get a token to start."); return false; }
  return true;
}

// ---- navigation ----

function go(section) {
  Object.keys(SECTIONS).forEach((s) => { $(`s-${s}`).hidden = s !== section; });
  document.querySelectorAll(".nav-item[data-section]").forEach((b) =>
    b.classList.toggle("active", b.dataset.section === section));
  $("pageTitle").textContent = SECTIONS[section].title;
  $("pageSub").textContent   = SECTIONS[section].sub;
  if (section === "dashboard") refreshDashboard();
  saveSession();
}

function setAuthed(subject) {
  $("authBanner").hidden = true;
  Object.keys(SECTIONS).forEach((s) => { if (s === "dashboard") $(`s-${s}`).hidden = false; });
  $("accountName").textContent = subject;
  $("accountMeta").textContent = "Signed in";
  $("signOutBtn").hidden = false;
  const pill = $("statusPill");
  pill.className = "pill pill-live";
  pill.innerHTML = `<span class="dot"></span> ${subject}`;
}

function signOut() {
  token = null;
  window._lastTransfer = null;
  clearSession();
  Object.keys(SECTIONS).forEach((s) => { $(`s-${s}`).hidden = true; });
  $("authBanner").hidden = false;
  $("accountName").textContent = "Not signed in";
  $("accountMeta").textContent = "Get a token to start";
  $("signOutBtn").hidden = true;
  const pill = $("statusPill");
  pill.className = "pill pill-muted";
  pill.innerHTML = `<span class="dot"></span> Signed out`;
  $("pageTitle").textContent = "Dashboard";
  $("pageSub").textContent   = SECTIONS.dashboard.sub;
  // Clear all wallet ID fields
  PERSISTED_FIELDS.forEach((id) => { const el = $(id); if (el) el.value = ""; });
  newKey();
  $("balOut").innerHTML = "";
  $("historyOut").innerHTML = "";
}

// ---- dashboard ----

async function refreshDashboard() {
  const id = ($("balWallet").value || $("tfFrom").value || "").trim();
  if (token && id) {
    try {
      const { data } = await api("GET", `/api/wallets/${id}/balance`);
      $("statBalance").textContent = koboToNaira(data.balanceKobo);
      $("statWallet").textContent  = id.slice(0, 8) + "…";
    } catch {
      $("statBalance").textContent = "—";
      $("statWallet").textContent  = "No wallet selected";
    }
  }
  const lt = window._lastTransfer;
  if (lt) {
    $("statTransfer").textContent     = koboToNaira(lt.amountKobo);
    $("statTransferFoot").textContent = lt.replayed ? "replayed (idempotent)" : "completed";
  }
}

// ---- actions ----

async function getToken() {
  try {
    const subject    = $("authSubject").value || "demo";
    const customerId = $("authCustomer").value || null;
    const { data }   = await api("POST", "/api/auth/token", { subject, customerId });
    token = data.accessToken;
    setAuthed(subject);
    go("dashboard");
    saveSession();
    toast("ok", "Signed in", `Token valid until ${new Date(data.expiresAt).toLocaleTimeString()}`);
  } catch (e) { toast("err", "Sign in failed", e.message); }
}

async function createWallet() {
  if (!requireToken()) return;
  try {
    const { data } = await api("POST", "/api/wallets", { customerId: $("cwCustomer").value });
    ["balWallet", "crWallet", "tfFrom", "hsWallet"].forEach((id) => { if (!$(id).value) $(id).value = data.walletId; });
    if (!$("tfTo").value && $("tfFrom").value !== data.walletId) $("tfTo").value = data.walletId;
    saveSession();
    toast("ok", "Wallet created", data.walletId);
  } catch (e) { toast("err", "Create failed", e.message); }
}

async function getBalance() {
  if (!requireToken()) return;
  try {
    const id       = $("balWallet").value.trim();
    const { data } = await api("GET", `/api/wallets/${id}/balance`);
    $("balOut").innerHTML = `<span class="amt">${koboToNaira(data.balanceKobo)}</span> <span class="cur">${data.currency} · ${data.balanceKobo} kobo</span>`;
    saveSession();
  } catch (e) { $("balOut").innerHTML = ""; toast("err", "Balance failed", e.message); }
}

async function credit() {
  if (!requireToken()) return;
  const kobo = nairaToKobo($("crAmount").value);
  if (kobo === null || kobo <= 0n) { toast("err", "Invalid amount", "Enter a positive naira amount."); return; }
  try {
    const id = $("crWallet").value.trim();
    await api("POST", `/api/wallets/${id}/credits`,
      { amountKobo: Number(kobo), reference: $("crRef").value || null });
    toast("ok", "Credited", `${koboToNaira(kobo)} to wallet`);
    saveSession();
    if ($("balWallet").value.trim() === id) getBalance();
  } catch (e) { toast("err", "Credit failed", e.message); }
}

function newKey() {
  $("tfKey").value = crypto.randomUUID();
  saveSession();
}

async function transfer() {
  if (!requireToken()) return;
  const kobo = nairaToKobo($("tfAmount").value);
  if (kobo === null || kobo <= 0n) { toast("err", "Invalid amount", "Enter a positive naira amount."); return; }
  if (!$("tfKey").value) newKey();
  try {
    const { data, headers } = await api("POST", "/api/transfers", {
      fromWalletId: $("tfFrom").value.trim(),
      toWalletId:   $("tfTo").value.trim(),
      amountKobo:   Number(kobo),
      reference:    $("tfRef").value || null,
    }, { "Idempotency-Key": $("tfKey").value });

    const replayed = headers.get("Idempotent-Replayed") === "true";
    window._lastTransfer = { amountKobo: kobo.toString(), replayed };
    saveSession();
    toast("ok",
      replayed ? "Replayed (idempotent)" : "Transfer complete",
      `${koboToNaira(kobo)} · new source balance ${koboToNaira(data.fromBalanceKobo)}`);
    if ($("balWallet").value.trim() === $("tfFrom").value.trim()) getBalance();
  } catch (e) { toast("err", "Transfer rejected", e.message); }
}

function switchTab(tab) {
  historyTab = tab;
  $("tabStmt").classList.toggle("active",  tab === "stmt");
  $("tabAudit").classList.toggle("active", tab === "audit");
  if ($("hsWallet").value.trim()) loadHistory();
}

function typeChip(type) {
  const map = {
    Credit:      ["chip-credit", "#i-down"],
    TransferIn:  ["chip-in",    "#i-down"],
    TransferOut: ["chip-out",   "#i-send"],
  };
  const [cls, icon] = map[type] || ["", "#i-history"];
  return `<span class="type-chip ${cls}"><svg class="ic"><use href="${icon}"/></svg>${type}</span>`;
}

async function loadHistory() {
  if (!requireToken()) return;
  const id  = $("hsWallet").value.trim();
  const out = $("historyOut");
  try {
    if (historyTab === "stmt") {
      const { data } = await api("GET", `/api/wallets/${id}/statement?page=1&pageSize=25`);
      if (!data.items.length) { out.innerHTML = `<p class="empty">No transactions yet.</p>`; return; }
      out.innerHTML = `<table><thead><tr>
        <th>When</th><th>Type</th><th>Amount</th><th>Balance after</th><th>Reference</th>
      </tr></thead><tbody>${
        data.items.map((t) => `<tr>
          <td>${new Date(t.createdAt).toLocaleString()}</td>
          <td>${typeChip(t.type)}</td>
          <td class="${t.amountKobo < 0 ? "amt-neg" : "amt-pos"}">${koboToNaira(t.amountKobo)}</td>
          <td>${koboToNaira(t.balanceAfterKobo)}</td>
          <td>${t.reference || "—"}</td>
        </tr>`).join("")
      }</tbody></table>`;
    } else {
      const { data } = await api("GET", `/api/wallets/${id}/audit?page=1&pageSize=25`);
      if (!data.items.length) { out.innerHTML = `<p class="empty">No audit entries yet.</p>`; return; }
      out.innerHTML = `<table><thead><tr>
        <th>#</th><th>When</th><th>Action</th><th>Before</th><th>After</th><th>Entry hash</th>
      </tr></thead><tbody>${
        data.items.map((a) => `<tr>
          <td>${a.id}</td>
          <td>${new Date(a.createdAt).toLocaleString()}</td>
          <td>${typeChip(a.action.replace("TRANSFER_IN","TransferIn").replace("TRANSFER_OUT","TransferOut").replace("CREDIT","Credit"))}</td>
          <td>${koboToNaira(a.balanceBeforeKobo)}</td>
          <td>${koboToNaira(a.balanceAfterKobo)}</td>
          <td><code title="${a.entryHash}">${a.entryHash.slice(0, 12)}…</code></td>
        </tr>`).join("")
      }</tbody></table>`;
    }
  } catch (e) { out.innerHTML = ""; toast("err", "Load failed", e.message); }
}

// ---- restore session on page load ----
async function restoreSession() {
  const session = loadSession();
  if (!session?.token) { newKey(); return; }

  // Restore field values immediately.
  if (session.fields) {
    Object.entries(session.fields).forEach(([id, value]) => {
      const el = $(id);
      if (el && value) el.value = value;
    });
  }
  if (session.lastTransfer) window._lastTransfer = session.lastTransfer;

  // Re-issue a fresh token with the same subject so we're never stuck with an expired one.
  // This is safe because the token endpoint is unauthenticated — just POST the same subject again.
  try {
    const subject    = session.subject || session.fields?.authSubject || "demo";
    const customerId = session.fields?.authCustomer || null;
    const { data }   = await api("POST", "/api/auth/token",
      { subject, customerId: customerId || undefined });
    token = data.accessToken;
    // Update stored token immediately.
    session.token = token;
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(session)); } catch {}

    setAuthed(subject);
    go("dashboard");
    toast("ok", "Session restored", "Picked up where you left off.");
  } catch (e) {
    // Server not up yet — show sign-in screen.
    token = null;
    newKey();
  }
}

restoreSession();
