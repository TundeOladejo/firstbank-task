// NovaWallet console — thin client over the same public /api endpoints.
// No secrets, no logic duplication: the browser just calls the API and renders results.

let token = null;
let historyTab = "stmt";

// ---- helpers ----
const $ = (id) => document.getElementById(id);

function nairaToKobo(naira) {
  // Parse to kobo without floating-point drift: work on the string, not on a float.
  const s = String(naira).trim();
  if (!/^\d+(\.\d{1,2})?$/.test(s)) return null;
  const [whole, frac = ""] = s.split(".");
  const koboFrac = (frac + "00").slice(0, 2);
  return BigInt(whole) * 100n + BigInt(koboFrac);
}

function koboToNaira(kobo) {
  const n = BigInt(kobo);
  const sign = n < 0n ? "-" : "";
  const abs = n < 0n ? -n : n;
  const whole = abs / 100n;
  const frac = (abs % 100n).toString().padStart(2, "0");
  return `${sign}₦${whole.toLocaleString("en-NG")}.${frac}`;
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
    // RFC 7807 Problem Details -> friendly message.
    const title = (data && (data.title || data.error)) || `HTTP ${res.status}`;
    const detail = (data && data.detail) || (typeof data === "string" ? data : "");
    const err = new Error(detail || title);
    err.problem = { status: res.status, title, detail };
    throw err;
  }
  return { data, headers: res.headers, status: res.status };
}

function requireToken() {
  if (!token) { toast("err", "Sign in first", "Get a token in step 1."); return false; }
  return true;
}

// ---- actions ----
async function getToken() {
  try {
    const subject = $("authSubject").value || "demo";
    const customerId = $("authCustomer").value || null;
    const { data } = await api("POST", "/api/auth/token", { subject, customerId });
    token = data.accessToken;
    const pill = $("authPill");
    pill.textContent = `Signed in · ${subject}`;
    pill.className = "pill pill-live";
    toast("ok", "Signed in", `Token valid until ${new Date(data.expiresAt).toLocaleTimeString()}`);
  } catch (e) { toast("err", "Sign in failed", e.message); }
}

async function createWallet() {
  if (!requireToken()) return;
  try {
    const customerId = $("cwCustomer").value;
    const { data } = await api("POST", "/api/wallets", { customerId });
    // Remember the wallet id across the relevant fields for convenience.
    ["balWallet", "crWallet", "tfFrom", "hsWallet"].forEach((id) => { if (!$(id).value) $(id).value = data.walletId; });
    if (!$("tfTo").value && $("tfFrom").value !== data.walletId) $("tfTo").value = data.walletId;
    toast("ok", "Wallet created", data.walletId);
  } catch (e) { toast("err", "Create failed", e.message); }
}

async function getBalance() {
  if (!requireToken()) return;
  try {
    const id = $("balWallet").value.trim();
    const { data } = await api("GET", `/api/wallets/${id}/balance`);
    $("balOut").innerHTML = `<span class="amt">${koboToNaira(data.balanceKobo)}</span> <span class="cur">${data.currency} · ${data.balanceKobo} kobo</span>`;
  } catch (e) { $("balOut").innerHTML = ""; toast("err", "Balance failed", e.message); }
}

async function credit() {
  if (!requireToken()) return;
  const kobo = nairaToKobo($("crAmount").value);
  if (kobo === null || kobo <= 0n) { toast("err", "Invalid amount", "Enter a positive naira amount."); return; }
  try {
    const id = $("crWallet").value.trim();
    await api("POST", `/api/wallets/${id}/credits`, { amountKobo: Number(kobo), reference: $("crRef").value || null });
    toast("ok", "Credited", `${koboToNaira(kobo)} to wallet`);
    if ($("balWallet").value.trim() === id) getBalance();
  } catch (e) { toast("err", "Credit failed", e.message); }
}

function newKey() { $("tfKey").value = crypto.randomUUID(); }

async function transfer() {
  if (!requireToken()) return;
  const kobo = nairaToKobo($("tfAmount").value);
  if (kobo === null || kobo <= 0n) { toast("err", "Invalid amount", "Enter a positive naira amount."); return; }
  if (!$("tfKey").value) newKey();
  try {
    const { data, headers } = await api("POST", "/api/transfers", {
      fromWalletId: $("tfFrom").value.trim(),
      toWalletId: $("tfTo").value.trim(),
      amountKobo: Number(kobo),
      reference: $("tfRef").value || null,
    }, { "Idempotency-Key": $("tfKey").value });
    const replayed = headers.get("Idempotent-Replayed") === "true";
    toast("ok", replayed ? "Replayed (idempotent)" : "Transfer complete",
      `${koboToNaira(kobo)} · new source balance ${koboToNaira(data.fromBalanceKobo)}`);
    if ($("balWallet").value.trim() === $("tfFrom").value.trim()) getBalance();
  } catch (e) { toast("err", "Transfer rejected", e.message); }
}

function switchTab(tab) {
  historyTab = tab;
  $("tabStmt").classList.toggle("active", tab === "stmt");
  $("tabAudit").classList.toggle("active", tab === "audit");
  if ($("hsWallet").value.trim()) loadHistory();
}

function typeChip(type) {
  const map = { Credit: "chip-credit", TransferIn: "chip-in", TransferOut: "chip-out" };
  return `<span class="type-chip ${map[type] || ""}">${type}</span>`;
}

async function loadHistory() {
  if (!requireToken()) return;
  const id = $("hsWallet").value.trim();
  const out = $("historyOut");
  try {
    if (historyTab === "stmt") {
      const { data } = await api("GET", `/api/wallets/${id}/statement?page=1&pageSize=25`);
      if (!data.items.length) { out.innerHTML = `<p class="empty">No transactions yet.</p>`; return; }
      out.innerHTML = `<table><thead><tr><th>When</th><th>Type</th><th>Amount</th><th>Balance after</th><th>Reference</th></tr></thead><tbody>${
        data.items.map((t) => `<tr>
          <td>${new Date(t.createdAt).toLocaleString()}</td>
          <td>${typeChip(t.type)}</td>
          <td class="${t.amountKobo < 0 ? "amt-neg" : "amt-pos"}">${koboToNaira(t.amountKobo)}</td>
          <td>${koboToNaira(t.balanceAfterKobo)}</td>
          <td>${t.reference || "—"}</td></tr>`).join("")
      }</tbody></table>`;
    } else {
      const { data } = await api("GET", `/api/wallets/${id}/audit?page=1&pageSize=25`);
      if (!data.items.length) { out.innerHTML = `<p class="empty">No audit entries yet.</p>`; return; }
      out.innerHTML = `<table><thead><tr><th>#</th><th>When</th><th>Action</th><th>Before</th><th>After</th><th>Entry hash</th></tr></thead><tbody>${
        data.items.map((a) => `<tr>
          <td>${a.id}</td>
          <td>${new Date(a.createdAt).toLocaleString()}</td>
          <td>${typeChip(a.action.replace("TRANSFER_", "Transfer").replace("CREDIT", "Credit"))}</td>
          <td>${koboToNaira(a.balanceBeforeKobo)}</td>
          <td>${koboToNaira(a.balanceAfterKobo)}</td>
          <td><code title="${a.entryHash}">${a.entryHash.slice(0, 12)}…</code></td></tr>`).join("")
      }</tbody></table>`;
    }
  } catch (e) { out.innerHTML = ""; toast("err", "Load failed", e.message); }
}

newKey();
