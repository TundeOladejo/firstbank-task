// NovaWallet console — thin client over the same public /api endpoints.
// State is persisted to localStorage so a page refresh restores the session automatically.

let token = null;
let historyTab = "stmt";
let _subject = null;   // current signed-in subject — source of truth for saving

const SECTIONS = {
  dashboard: { title: "Dashboard", sub: "Overview of your NovaWallet activity" },
  wallets:   { title: "Wallets",   sub: "All wallets · create · credit · check balance" },
  transfer:  { title: "Transfer",  sub: "Move funds atomically between wallets" },
  history:   { title: "History",   sub: "Statement and append-only audit trail" },
};

// Input field ids whose values survive a page refresh via localStorage.
const PERSISTED_FIELDS = [
  "balWallet", "crWallet", "tfFrom", "tfTo", "hsWallet", "tfKey",
  "cwCustomer", "crAmount", "tfAmount", "authSubject", "authCustomer",
];

const STORAGE_KEY = "nw_session";
const $ = (id) => document.getElementById(id);

// ── persistence ──────────────────────────────────────────────────────────────

function saveSession() {
  const fields = {};
  PERSISTED_FIELDS.forEach((id) => { const el = $(id); if (el) fields[id] = el.value; });
  if (!_subject) return;   // never save a session when no one is signed in
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      subject:      _subject,
      customerId:   $("authCustomer")?.value || "",
      fields,
      lastTransfer: window._lastTransfer || null,
    }));
  } catch { /* storage full */ }
}

function loadSession() {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || "null"); }
  catch { return null; }
}

function clearSession() { localStorage.removeItem(STORAGE_KEY); }

// Restore all input field values from a saved session object.
function restoreFields(session) {
  if (!session?.fields) return;
  Object.entries(session.fields).forEach(([id, value]) => {
    const el = $(id);
    if (el && value) el.value = value;
  });
}

// ── money helpers (BigInt — no floats ever) ──────────────────────────────────

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

// ── toast ─────────────────────────────────────────────────────────────────────

function toast(kind, title, detail) {
  const t = $("toast");
  t.className = `toast show ${kind}`;
  t.innerHTML = `<span class="t-title">${title}</span>` +
                (detail ? `<span class="t-detail">${detail}</span>` : "");
  clearTimeout(toast._t);
  toast._t = setTimeout(() => (t.className = "toast"), 4200);
}

// ── api helper ───────────────────────────────────────────────────────────────

async function api(method, path, body, extra = {}) {
  const headers = { "Content-Type": "application/json", ...extra };
  if (token) headers["Authorization"] = `Bearer ${token}`;
  const res  = await fetch(path, { method, headers, body: body ? JSON.stringify(body) : undefined });
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

// ── navigation ───────────────────────────────────────────────────────────────

function go(section) {
  Object.keys(SECTIONS).forEach((s) => { $(`s-${s}`).hidden = s !== section; });
  document.querySelectorAll(".nav-item[data-section]").forEach((b) =>
    b.classList.toggle("active", b.dataset.section === section));
  $("pageTitle").textContent = SECTIONS[section].title;
  $("pageSub").textContent   = SECTIONS[section].sub;
  // Wait for auth to settle before triggering data loads.
  _authReady.then(() => {
    if (section === "dashboard") refreshDashboard();
    if (section === "wallets")   loadWallets();
  });
  saveSession();
}

function setAuthed(subject) {
  _subject = subject;
  $("authBanner").hidden = true;
  Object.keys(SECTIONS).forEach((s) => { $(`s-${s}`).hidden = s !== "dashboard"; });
  $("accountName").textContent = subject;
  $("accountMeta").textContent = "Signed in";
  $("signOutBtn").hidden = false;
  const pill = $("statusPill");
  pill.className = "status-badge status-on";
  pill.innerHTML = `<span class="status-dot"></span> ${subject}`;
}

function signOut() {
  token = null;
  _subject = null;
  window._lastTransfer = null;
  clearSession();
  Object.keys(SECTIONS).forEach((s) => { $(`s-${s}`).hidden = true; });
  $("authBanner").hidden = false;
  $("accountName").textContent = "Not signed in";
  $("accountMeta").textContent = "Authenticate to continue";
  $("signOutBtn").hidden = true;
  $("statusPill").className = "status-badge status-off";
  $("statusPill").innerHTML = `<span class="status-dot"></span> Signed out`;
  $("pageTitle").textContent = "Dashboard";
  $("pageSub").textContent   = SECTIONS.dashboard.sub;
  PERSISTED_FIELDS.forEach((id) => { const el = $(id); if (el) el.value = ""; });
  $("balOut").innerHTML = "";
  $("historyOut").innerHTML = "";
  $("walletListOut").innerHTML = `<p class="empty-state">Loading…</p>`;
  $("dashWalletList").innerHTML = `<p class="empty-state">Loading wallets…</p>`;
  newKey();
}

// ── dashboard ────────────────────────────────────────────────────────────────

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
  // Also populate the dashboard wallet list
  loadDashboardWallets();
}

async function loadDashboardWallets() {
  if (!token) return;
  const out = $("dashWalletList");
  try {
    const { data } = await api("GET", "/api/wallets?pageSize=20");
    if (!data.items.length) { out.innerHTML = `<p class="empty-state">No wallets yet.</p>`; return; }
    out.innerHTML = renderWalletTable(data.items, data.totalCount, true);
  } catch { out.innerHTML = `<p class="empty-state">Could not load wallets.</p>`; }
}

// ── wallet list ───────────────────────────────────────────────────────────────

let _selectedWalletId = null;

function renderWalletTable(items, totalCount, compact = false) {
  return `<table>
    <thead><tr>
      <th>Customer</th>
      <th>Wallet ID</th>
      <th>Balance</th>
      ${compact ? "" : "<th>Created</th>"}
    </tr></thead>
    <tbody>
      ${items.map((w) => `
        <tr class="wallet-row${w.walletId === _selectedWalletId ? " selected" : ""}"
            onclick="selectWallet('${w.walletId}', '${w.customerId}')">
          <td><span class="cust-name">${w.customerId}</span></td>
          <td><span class="wid-mono" title="${w.walletId}">${w.walletId.slice(0,8)}…</span></td>
          <td class="${w.balanceKobo > 0 ? "amt-pos" : ""}">${koboToNaira(w.balanceKobo)}</td>
          ${compact ? "" : `<td style="color:var(--ink-3);font-size:12px">${new Date(w.createdAt).toLocaleString()}</td>`}
        </tr>`).join("")}
    </tbody>
  </table>
  ${!compact ? `<p style="font-size:12px;color:var(--ink-3);margin-top:8px;padding:0 2px">${totalCount} wallet${totalCount!==1?"s":""} · click a row to select it for all forms</p>` : ""}`;
}

async function loadWallets() {
  if (!requireToken()) return;
  const out = $("walletListOut");
  out.innerHTML = `<p class="empty-state">Loading…</p>`;
  try {
    const { data } = await api("GET", "/api/wallets?pageSize=100");
    if (!data.items.length) { out.innerHTML = `<p class="empty-state">No wallets yet — create one.</p>`; return; }
    out.innerHTML = renderWalletTable(data.items, data.totalCount);
  } catch (e) { out.innerHTML = ""; toast("err", "Failed to load wallets", e.message); }
}

// Clicking a wallet row fills it into all relevant fields everywhere in the UI.
function selectWallet(walletId, customerId) {
  _selectedWalletId = walletId;
  // Update selection highlight
  document.querySelectorAll(".wallet-row").forEach((r) =>
    r.classList.toggle("selected", r.querySelector("code")?.title === walletId));
  // Fill into all wallet ID fields
  const targets = ["balWallet", "crWallet", "hsWallet"];
  targets.forEach((id) => { $(id).value = walletId; });
  // Fill tfFrom if empty, tfTo otherwise
  if (!$("tfFrom").value || $("tfFrom").value === walletId) {
    $("tfFrom").value = walletId;
  } else if (!$("tfTo").value) {
    $("tfTo").value = walletId;
  }
  saveSession();
  // Immediately show the balance in the balance panel
  getBalance();
  toast("ok", `${customerId} selected`, walletId.slice(0, 8) + "…");
}

// ── actions ───────────────────────────────────────────────────────────────────

async function getToken() {
  try {
    const subject    = $("authSubject").value.trim() || "demo";
    const customerId = $("authCustomer").value.trim() || null;
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
    // Auto-fill empty wallet fields with the new wallet
    ["balWallet", "crWallet", "hsWallet"].forEach((id) => { if (!$(id).value) $(id).value = data.walletId; });
    if (!$("tfFrom").value) $("tfFrom").value = data.walletId;
    else if (!$("tfTo").value && $("tfTo").value !== data.walletId) $("tfTo").value = data.walletId;
    _selectedWalletId = data.walletId;
    saveSession();
    // Refresh the wallet list to show the new entry
    loadWallets();
    toast("ok", "Wallet created", data.walletId);
  } catch (e) { toast("err", "Create failed", e.message); }
}

async function getBalance() {
  if (!requireToken()) return;
  try {
    const id       = $("balWallet").value.trim();
    const { data } = await api("GET", `/api/wallets/${id}/balance`);
    $("balOut").innerHTML =
      `<span class="bal-amount">${koboToNaira(data.balanceKobo)}</span>` +
      `<span class="bal-kobo">${data.currency} · ${data.balanceKobo.toLocaleString()} kobo</span>`;
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
    // Refresh balance and wallet list to show updated figures
    if ($("balWallet").value.trim() === id) getBalance();
    loadWallets();
    loadDashboardWallets();
  } catch (e) { toast("err", "Credit failed", e.message); }
}

function newKey() { $("tfKey").value = crypto.randomUUID(); }

function copyKey() {
  const k = $("tfKey").value;
  if (k) navigator.clipboard.writeText(k).then(() => toast("ok", "Copied", k.slice(0,8) + "…"));
}

function updateTfDisplay() {
  const from = $("tfFrom").value.trim();
  const to   = $("tfTo").value.trim();
  const amt  = nairaToKobo($("tfAmount").value);
  $("tfFromDisplay").textContent = from ? from.slice(0,8) + "…" : "—";
  $("tfToDisplay").textContent   = to   ? to.slice(0,8)   + "…" : "—";
  $("tfAmountDisplay").textContent = amt && amt > 0n ? koboToNaira(amt) : "₦0.00";
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
    loadWallets();
    loadDashboardWallets();
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
    Credit:      ["chip-credit", "#i-credit"],
    TransferIn:  ["chip-in",    "#i-credit"],
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

// ── restore session on page load ─────────────────────────────────────────────
// _authReady resolves once we know whether the user is signed in or not.
// Functions that need auth (loadWallets, refreshDashboard, etc.) await it
// before doing anything, so they never race against restoreSession().

let _resolveAuth;
const _authReady = new Promise((resolve) => { _resolveAuth = resolve; });

async function restoreSession() {
  const session = loadSession();

  // Step 1 — restore field values synchronously so inputs are populated immediately
  // on first render, before any async work starts.
  restoreFields(session);
  if (session?.lastTransfer) window._lastTransfer = session.lastTransfer;

  if (!session?.subject) {
    // No saved session — show sign-in screen and signal auth is settled (not signed in).
    newKey();
    _resolveAuth(false);
    return;
  }

  try {
    // Step 2 — re-issue a fresh token with the saved subject.
    // The token endpoint is unauthenticated, so this always works while the server is up.
    const subject    = session.subject || "demo";
    const customerId = session.customerId || session.fields?.authCustomer || null;
    const { data }   = await api("POST", "/api/auth/token",
      { subject, customerId: customerId || undefined });
    token = data.accessToken;

    // Step 3 — now that token is set, update the UI.
    // Fields are already in the DOM from Step 1, so refreshDashboard() can read them.
    setAuthed(subject);
    go("dashboard");
    toast("ok", "Session restored", "Picked up where you left off.");
    _resolveAuth(true);
  } catch {
    // Server not reachable — fall back to sign-in screen.
    token = null;
    newKey();
    _resolveAuth(false);
  }
}

newKey();
restoreSession();
