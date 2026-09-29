/* ============================================================
   澄时 · Web UI
   与 C# 宿主的协议：
     C# → JS  {type, payload}：state / dashboard / form.* / help / hint / nav / overlay / pinCleared
     JS → C#  {cmd, args}：ready / min / close / deskSelect / startGuard / …
   无 WebView2 宿主时（直接用浏览器打开本页）自动进入 demo 模式，
   用假数据渲染，方便设计和布局走查。
   ============================================================ */
"use strict";

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
}[c]));

const hasHost = !!(window.chrome && window.chrome.webview);
function send(cmd, args = {}) {
  if (hasHost) window.chrome.webview.postMessage({ cmd, args });
  else demoSend(cmd, args);
}

/* 只在内容变化时重写 innerHTML：避免每秒推送打断 hover/过渡/输入。 */
function setHtml(el, html) {
  if (el._lastHtml === html) return;
  el._lastHtml = html;
  el.innerHTML = html;
}
function setText(el, text) {
  if (el._lastText !== text) {
    el._lastText = text;
    el.textContent = text;
  }
}

/* ============================================================
   消息接收
   ============================================================ */
const LAST = {}; // 各 type 最近一次 payload（demo 模式与输入恢复用）

const HANDLERS = {
  state: renderState,
  dashboard: renderDashboard,
  "form.head": renderFormHead,
  "form.desk": renderFormDesk,
  "form.duration": renderFormDuration,
  "form.schedule": renderFormSchedule,
  "form.limits": renderFormLimits,
  "form.pin": renderFormPin,
  "form.mail": renderFormMail,
  "form.service": renderFormService,
  "form.flags": renderFormFlags,
  help: renderHelp,
  stats: renderStats,
  "form.preview": renderPreview,
  hint: renderHint,
  nav: (p) => showPage(p.page),
  overlay: (p) => {
    if (p.name === "break") $("#breakOverlay").classList.toggle("hidden", !p.visible);
  },
  pinCleared: () => {
    $("#pinOld").value = "";
    $("#pinNew").value = "";
    $("#pinNew2").value = "";
  },
};

function dispatch(type, payload) {
  LAST[type] = payload;
  HANDLERS[type]?.(payload);
}

if (hasHost) {
  window.chrome.webview.addEventListener("message", (e) => {
    const { type, payload } = e.data || {};
    if (type) dispatch(type, payload);
  });
}

/* ============================================================
   全局状态（每次 tick 推送）
   ============================================================ */
function renderState(s) {
  document.body.className = (s.view === "desk" || s.view === "timeup") ? "mode-child" : "mode-parent";

  const pill = $("#tbPill");
  pill.classList.toggle("pill-accent", s.pill.tone === "accent");
  pill.classList.toggle("pill-muted", s.pill.tone !== "accent");
  setText($("#tbPillText"), s.pill.text);

  $("#btnClose").classList.toggle("hidden", !!s.closeHidden);

  const parent = s.view === "parent";
  $("#parentView").classList.toggle("hidden", !parent);
  $("#deskView").classList.toggle("hidden", s.view !== "desk");
  $("#timeupView").classList.toggle("hidden", s.view !== "timeup");

  if (s.view === "desk") {
    setText($("#childCaption"), s.caption);
    setText($("#remaining"), s.remainingText);
    setText($("#childHint"), s.childHint);
    // 桌面顶部横幅：宽限（琥珀，倒计时归零后的保存窗口）优先于分级提醒（10/5/1 分钟）。
    const banner = $("#deskBanner");
    const grace = s.graceText, warn = s.warningText;
    banner.classList.toggle("hidden", !grace && !warn);
    banner.classList.toggle("banner-grace", !!grace);
    banner.classList.toggle("banner-warn", !grace && !!warn);
    setText(banner, grace || warn || "");
    renderTiles(s.tiles);
    setHtml($("#childBlocked"), (s.blocked || [])
      .map((b) => `<span class="chip chip-warn">${esc(b)}</span>`).join(""));
    $("#askParentBtn").classList.toggle("hidden", !s.askParent);
  }

  if (s.view === "timeup") {
    setText($("#timeupHint"), s.childHint);
    $("#askMoreBtn").classList.toggle("hidden", !s.askMore);
    $("#askParentNight").classList.toggle("hidden", !s.askParent);
  }

  // 侧边栏引擎状态
  $("#engineDot").className = "se-dot " + (s.engine.ok ? "ok" : "warn");
  setText($("#engineTitle"), s.engine.title);
  setText($("#engineDetail"), s.engine.detail);
}

function renderTiles(tiles) {
  const palette = ["#5B7C6E", "#6B7FA3", "#9A6B5B", "#7C6B9A", "#4F7D8C", "#8C7A4F"];
  const hue = (name) => {
    let h = 0;
    for (const ch of String(name)) h = (h * 31 + ch.codePointAt(0)) >>> 0;
    return palette[h % palette.length];
  };
  setHtml($("#childTiles"), (tiles || []).map((t) => `
    <div class="app-tile">
      <div class="app-tile-avatar" style="background:${hue(t.name)}">${esc(t.name.charAt(0).toUpperCase())}</div>
      <div class="app-tile-name" title="${esc(t.name)}">${esc(t.name)}</div>
    </div>`).join(""));
}

/* ============================================================
   仪表盘
   ============================================================ */
const DESK_ICONS = { homework: "i-pencil", class: "i-monitor", code: "i-code", fullpc: "i-monitor" };
const DESK_BADGES = { homework: "已断网" };

function renderDashboard(d) {
  setText($("#greeting"), d.greeting);
  setText($("#heroLine"), d.heroLine);

  setHtml($("#deskCards"), (d.deskCards || []).map((c) => `
    <button class="desk-card nav-go" data-page="settings">
      <div class="desk-card-row">
        <div class="desk-card-icon"><svg class="ic"><use href="#${DESK_ICONS[c.id] || "i-grid"}"/></svg></div>
        <span class="desk-card-badge" style="${DESK_BADGES[c.id] ? "" : "display:none"}">${DESK_BADGES[c.id] || ""}</span>
      </div>
      <div class="desk-card-name" style="margin-top:12px">${esc(c.name)}</div>
      <div class="desk-card-summary">${esc(c.summary)}</div>
    </button>`).join(""));

  const b = d.budget;
  const C = 2 * Math.PI * 57.5;
  $("#donutArc").style.strokeDashoffset = C * (1 - Math.max(0, Math.min(1, b.fraction)));
  setText($("#donutRemaining"), b.remainingText);
  setText($("#donutUsed"), b.usedText);
  setText($("#donutHint"), b.hint);
  setText($("#weekdayLimit"), b.weekdayText);
  setText($("#weekendLimit"), b.weekendText);

  const week = d.week;
  setHtml($("#weekBars"), week.empty ? "" : week.rows.map((r) => `
    <div class="week-bar-wrap" title="${esc(r.summary)}">
      <div class="week-bar" style="height:${Math.round(r.fraction * 110)}px"></div>
      <div class="week-bar-label">${esc(r.label)}</div>
    </div>`).join(""));
  $("#weekEmpty").classList.toggle("hidden", !week.empty);

  setHtml($("#dashBlocked"), (d.blocked || [])
    .map((x) => `<span class="chip chip-warn">${esc(x)}</span>`).join(""));
  $("#dashBlockedEmpty").classList.toggle("hidden", (d.blocked || []).length > 0);

  const cd = d.currentDesk;
  setText($("#curDeskName"), cd.empty ? "—" : cd.name);
  setText($("#curDeskSummary"), cd.empty ? "还没有选书桌。" : cd.summary);
  setHtml($("#curDeskApps"), cd.empty ? "" : cd.apps
    .map((n) => `<span class="chip">${esc(n)}</span>`).join(""));
  $("#curDeskEmpty").classList.toggle("hidden", !cd.empty);

  setText($("#dashEngine"), d.engineText);
  setText($("#dashHint"), d.dashHint);

  const g = $("#guardBtn");
  setText(g, d.guardBtn.text);
  g.disabled = !d.guardBtn.enabled;
  $("#rewardBtn").classList.toggle("hidden", !d.rewardVisible);

  const au = d.appUsage;
  if (au.changed !== false) {
    setHtml($("#appUsageRows"), au.empty ? "" : au.rows.map((r) => `
      <div class="app-usage-row" title="${esc(r.summary)}">
        <div class="app-usage-name">${esc(r.name)}</div>
        <div class="app-usage-track"><div class="app-usage-bar ${r.over ? "over" : ""}" style="width:${Math.round(r.fraction * 100)}%"></div></div>
        <div class="app-usage-summary ${r.over ? "over" : ""}">${esc(r.summary)}</div>
      </div>`).join(""));
    setText($("#appUsageHint"), au.hint);
  }
  $("#appUsageEmpty").classList.toggle("hidden", !au.empty);
}

/* ============================================================
   设置页 · 分节渲染
   ============================================================ */
function renderFormHead(f) {
  setText($("#setTitle"), f.title);
  setText($("#setLead"), f.lead);
}

function renderFormDesk(f) {
  setHtml($("#deskGrid"), f.desks.map((desk) => `
    <button class="desk-card ${desk.id === f.selected ? "on" : ""}" data-desk="${esc(desk.id)}">
      <div class="desk-card-icon"><svg class="ic"><use href="#${DESK_ICONS[desk.id] || "i-grid"}"/></svg></div>
      <span class="desk-card-check"><svg class="ic"><use href="#i-check"/></svg></span>
      <div class="desk-card-name">${esc(desk.name)}</div>
      <div class="desk-card-summary">${esc(desk.summary)}</div>
    </button>`).join(""));

  setHtml($("#appChips"), (f.apps || []).map((a) => `
    <span class="chip">${esc(a.name)}
      <button class="chip-remove" data-remove-app="${esc(a.key)}" title="从允许名单拿掉">✕</button>
    </span>`).join(""));
  $("#appChips").classList.toggle("hidden", !f.apps || f.apps.length === 0);
  $("#appChipsEmpty").classList.toggle("hidden", !(!f.apps || f.apps.length === 0));

  const unres = !!f.unrestricted;
  $("#presetSection").classList.toggle("hidden", unres);
  $("#appsSection").classList.toggle("hidden", unres);
  $("#sitesSection").classList.toggle("hidden", unres);
  $("#unrestrictedNote").classList.toggle("hidden", !unres);

  const st = f.sites;
  setText($("#sitesHint"), st.hint);
  setHtml($("#allowSiteChips"), (st.allowed || []).map((s) => `
    <span class="chip chip-accent">${esc(s)}
      <button class="chip-remove" data-remove-allow="${esc(s)}" title="从允许名单拿掉">✕</button>
    </span>`).join(""));
  $("#allowSiteEmpty").classList.toggle("hidden", (st.allowed || []).length > 0);
  setHtml($("#blockSiteChips"), (st.blocked || []).map((s) => `
    <span class="chip">${esc(s)}
      <button class="chip-remove" data-remove-block="${esc(s)}" title="从禁止名单拿掉">✕</button>
    </span>`).join(""));
  $("#catVideo").checked = !!st.categories.video;
  $("#catGames").checked = !!st.categories.games;
  $("#catAdult").checked = !!st.categories.adult;

  const gb = $("#guardBtn2");
  gb.disabled = !f.guardEnabled;
}

function renderFormDuration(d) {
  $$(".seg-tab").forEach((el) => el.classList.toggle("on", el.dataset.tab === d.tab));
  setText($("#minutesBig"), d.minutesText);
  setText($("#otherDays"), d.otherDays);
  $$(".pill-btn[data-min]").forEach((el) => {
    el.classList.toggle("on", el.dataset.min === String(d.preset));
  });
  $("#customWrap").classList.toggle("hidden", d.preset !== "custom");
  if (d.preset === "custom" && document.activeElement !== $("#customMinutes")) {
    $("#customMinutes").value = d.customValue;
  }
  setText($("#whatHappens"), d.whatHappens);
}

function renderFormSchedule(s) {
  if ($("#dayRows").contains(document.activeElement)) return; // 输入中不重排
  setHtml($("#dayRows"), s.rows.map((r) => `
    <div class="day-row">
      <span class="day-row-label">${esc(r.label)}</span>
      <input class="input" data-day="${r.day}" inputmode="numeric" value="${r.minutes}"
             aria-label="${esc(r.label)}的屏幕分钟数" />
      <span class="muted-note" style="margin:0">分<span class="day-row-custom" style="${r.custom ? "" : "display:none"}">&nbsp;已单独设置</span></span>
    </div>`).join(""));
}

function renderFormLimits(l) {
  const unres = !!l.unrestricted;
  // 「整个电脑」场景：软件不在白名单里，得先点「从电脑挑选软件…」点名要限时的那一款。
  $("#limitPickApp").classList.toggle("hidden", !unres);
  setText($("#limitLead"), unres
    ? "不限软件，但可以点名几款单独限时：比如游戏每天只给 30 分钟，用完只关掉它，其它软件照常用。"
    : "在「能用多久」之内，给某些软件单独设上限：比如游戏每天只给 30 分钟，用完就只关掉它，别的软件照常用。");

  const sel = $("#limitAppSelect");
  const focusIn = sel.contains(document.activeElement);
  const options = (l.choices || []).map((c) =>
    `<option value="${esc(c.key)}">${esc(c.name)}</option>`).join("");
  const placeholder = unres
    ? "（先点「从电脑挑选软件…」）"
    : "（先在「能用什么」里加软件）";
  setHtml(sel, options || `<option value="">${esc(placeholder)}</option>`);
  if (!focusIn && l.selectedKey && sel.querySelector(`option[value="${CSS.escape(l.selectedKey)}"]`)) {
    sel.value = l.selectedKey;
  }
  setHtml($("#limitRows"), (l.rows || []).map((r) => `
    <div class="limit-row">
      <span class="limit-row-name">${esc(r.name)}</span>
      <span class="limit-row-summary">${esc(r.summary)}</span>
      <button class="chip-remove" data-remove-limit="${esc(r.key)}" title="取消这条限额">✕</button>
    </div>`).join(""));
  $("#limitRows").classList.toggle("hidden", !(l.rows || []).length);
  $("#limitEmpty").classList.toggle("hidden", (l.rows || []).length > 0);
}

function renderFormPin(p) {
  $("#pinFirstRun").classList.toggle("hidden", !p.firstRun);
  $("#pinConfigured").classList.toggle("hidden", p.firstRun);
  if (p.firstRun) return;
  setText($("#recoveryCode"), p.recoveryCode);
  setText($("#recoveryHint"), p.recoveryHint);
  if (document.activeElement !== $("#recoveryEmail")) {
    $("#recoveryEmail").value = p.recoveryEmail;
  }
}

function renderFormMail(m) {
  if (document.activeElement !== $("#smtpHost")) $("#smtpHost").value = m.host;
  if (document.activeElement !== $("#smtpPort")) $("#smtpPort").value = m.port;
  if (document.activeElement !== $("#smtpUser")) $("#smtpUser").value = m.user;
  $("#smtpSsl").checked = !!m.ssl;
}

function renderFormService(s) {
  setText($("#serviceStatus"), s.statusText);
  setText($("#installService"), s.installLabel);
  $("#uninstallService").classList.toggle("hidden", !s.installed);
}

function renderFormFlags(f) {
  const map = {
    startWithWindows: "#startWithWindows",
    guardOnLaunch: "#guardOnLaunch",
    bedtime: "#bedtime",
  };
  for (const [k, sel] of Object.entries(map)) {
    const el = $(sel);
    if (document.activeElement !== el) el.checked = !!f[k];
  }
  $$(".break-pill").forEach((el) =>
    el.classList.toggle("on", Number(el.dataset.break) === Number(f.breakReminder)));
}

function renderHelp(h) {
  setText($("#helpEngine"), h.engineText);
  setText($("#helpAbout"), h.aboutText);
  setText($("#feedbackEmail"), h.feedbackEmail);
}

function renderHint(h) {
  const sel = {
    parent: "#parentHint",
    dashHint: "#dashHint",
    spike: "#spikeHint",
    mail: "#mailHint",
    pin: "#pinHint",
    recovery: "#recoveryHint",
    feedback: "#feedbackHint",
  }[h.slot];
  if (sel) setText($(sel), h.text);
}

/* 守护生效预览：把所有规则叠加后的结果讲成大白话 */
function renderPreview(p) {
  setText($("#previewHead"), p.head);
  setHtml($("#previewList"), (p.lines || []).map((line) =>
    `<li><svg class="ic ic-sm"><use href="#i-check"/></svg><span>${esc(line)}</span></li>`).join(""));
}

/* ============================================================
   使用统计页
   ============================================================ */
function renderStats(s) {
  $$(".range-pill").forEach((el) =>
    el.classList.toggle("on", Number(el.dataset.statrange) === Number(s.range)));

  setText($("#statTotal"), s.summary.totalText);
  setText($("#statAvg"), s.summary.avgText);
  setText($("#statApps"), s.summary.appCount > 0 ? `${s.summary.appCount} 款` : "—");
  setText($("#statBlocked"), s.summary.blockedText);

  const hasDaily = (s.daily || []).some((d) => d.label !== "今天");
  setHtml($("#statDailyBars"), s.daily.map((d) => `
    <div class="stat-bar-col" title="${esc(d.totalText)}${d.blocked > 0 ? ` · 拦了 ${d.blocked} 次` : ""}">
      <div class="stat-bar" style="height:${Math.max(2, Math.round(d.fraction * 120))}px"></div>
      <div class="week-bar-label">${esc(d.label)}</div>
    </div>`).join(""));
  $("#statDailyEmpty").classList.toggle("hidden", hasDaily);

  const ranking = s.ranking || [];
  setHtml($("#statRanking"), ranking.map((r) => `
    <div class="rank-row" title="${esc(r.name)} · ${esc(r.minutesText)}（${esc(r.share)}）">
      <span class="rank-name">${esc(r.name)}</span>
      <span class="app-usage-track"><span class="rank-bar" style="width:${Math.round(r.fraction * 100)}%"></span></span>
      <span class="rank-minutes">${esc(r.minutesText)}</span>
      <span class="rank-share">${esc(r.share)}</span>
    </div>`).join(""));
  $("#statRankEmpty").classList.toggle("hidden", ranking.length > 0);

  setHtml($("#statDetail"), (s.detail || []).map((d) => `
    <div class="detail-row">
      <span>${esc(d.dateText)}</span>
      <span>${esc(d.totalText)}</span>
      <span>${esc(d.blockedText)}</span>
      <span class="detail-apps" title="${esc(d.topApps)}">${esc(d.topApps)}</span>
    </div>`).join(""));
}

/* ============================================================
   页面切换
   ============================================================ */
function showPage(page) {
  $$(".nav-item").forEach((el) => el.classList.toggle("active", el.dataset.page === page));
  $$(".page").forEach((el) => el.classList.toggle("active", el.id === `page-${page}`));
  if (page === "stats") send("statsShow");
}
document.addEventListener("click", (e) => {
  const nav = e.target.closest("[data-page]");
  if (nav) showPage(nav.dataset.page);
});

/* ============================================================
   事件绑定
   ============================================================ */
$("#btnMin").addEventListener("click", () => send("min"));
$("#btnClose").addEventListener("click", () => send("close"));

$("#guardBtn").addEventListener("click", () => send("dashboardGuard"));
$("#guardBtn2").addEventListener("click", () => send("startGuard", readFirstRunPin()));
$("#rewardBtn").addEventListener("click", () => send("reward"));

$("#askParentBtn").addEventListener("click", () => send("askParent"));
$("#askParentNight").addEventListener("click", () => send("askParent"));
$("#askMoreBtn").addEventListener("click", () => send("askMore"));
$$("#breakOverlay [data-break-dismiss]").forEach((el) =>
  el.addEventListener("click", () => send("breakDismiss", { mode: el.dataset.breakDismiss })));

// 设置页 · 场景/软件/网站/限时/邮件预设（事件委托）
document.addEventListener("click", (e) => {
  const desk = e.target.closest("[data-desk]");
  if (desk) { send("deskSelect", { id: desk.dataset.desk }); return; }
  const preset = e.target.closest("[data-preset]");
  if (preset) {
    const [d, m] = preset.dataset.preset.split("|");
    send("preset", { desk: d, minutes: Number(m) });
    return;
  }
  const rmApp = e.target.closest("[data-remove-app]");
  if (rmApp) { send("removeApp", { key: rmApp.dataset.removeApp }); return; }
  const rmAllow = e.target.closest("[data-remove-allow]");
  if (rmAllow) { send("removeAllowedSite", { site: rmAllow.dataset.removeAllow }); return; }
  const rmBlock = e.target.closest("[data-remove-block]");
  if (rmBlock) { send("removeBlockedSite", { site: rmBlock.dataset.removeBlock }); return; }
  const rmLimit = e.target.closest("[data-remove-limit]");
  if (rmLimit) { send("removeAppLimit", { key: rmLimit.dataset.removeLimit }); return; }
  const mail = e.target.closest("[data-mail]");
  if (mail) send("mailPreset", { tag: mail.dataset.mail });
});
$("#addAppsBtn").addEventListener("click", () => send("addApps"));
$("#addAllowSite").addEventListener("click", () => commitInput("#allowSiteInput", (v) => send("addAllowedSite", { text: v })));
$("#allowSiteInput").addEventListener("keydown", (e) => { if (e.key === "Enter") commitInput("#allowSiteInput", (v) => send("addAllowedSite", { text: v })); });
$("#addBlockSite").addEventListener("click", () => commitInput("#blockSiteInput", (v) => send("addBlockedSite", { text: v })));
$("#blockSiteInput").addEventListener("keydown", (e) => { if (e.key === "Enter") commitInput("#blockSiteInput", (v) => send("addBlockedSite", { text: v })); });
for (const id of ["#catVideo", "#catGames", "#catAdult"]) {
  $(id).addEventListener("change", () => send("setCategories", {
    video: $("#catVideo").checked, games: $("#catGames").checked, adult: $("#catAdult").checked,
  }));
}

// 设置页 · 时长
$$(".seg-tab").forEach((el) => el.addEventListener("click", () => send("dayTab", { tab: el.dataset.tab })));
$$(".pill-btn[data-min]").forEach((el) => el.addEventListener("click", () => {
  const v = el.dataset.min;
  send("setPresetDuration", { minutes: v === "custom" ? "custom" : Number(v) });
}));
$("#customMinutes").addEventListener("keydown", (e) => { if (e.key === "Enter") e.target.blur(); });
$("#customMinutes").addEventListener("blur", () => {
  const v = $("#customMinutes").value.trim();
  if (v === "") { HANDLERS["form.duration"](LAST["form.duration"]); return; }
  const n = Math.round(Number(v));
  if (Number.isFinite(n)) send("setCustomMinutes", { value: Math.min(600, Math.max(5, n)) });
  else HANDLERS["form.duration"](LAST["form.duration"]);
});

// 设置页 · 按星期
function commitDay(input) {
  const n = Math.round(Number(input.value));
  if (Number.isFinite(n) && input.value.trim() !== "") {
    const clamped = Math.min(600, Math.max(5, n));
    input.value = clamped;
    send("setScheduleDay", { day: Number(input.dataset.day), minutes: clamped });
  }
}
$("#dayRows").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && e.target.matches("input[data-day]")) e.target.blur();
});
$("#dayRows").addEventListener("focusout", (e) => {
  if (e.target.matches("input[data-day]")) commitDay(e.target);
});

// 设置页 · 单软件限时
$("#limitPickApp").addEventListener("click", () => send("pickLimitApp"));
$("#addLimit").addEventListener("click", () => {
  const key = $("#limitAppSelect").value;
  const n = Math.round(Number($("#limitMinutes").value));
  if (!key) return;
  if (Number.isFinite(n) && n >= 5 && n <= 600) {
    $("#limitMinutes").value = "";
    send("addAppLimit", { key, minutes: n });
  }
});

// 设置页 · 密码 / 找回 / 邮件 / 服务 / 开关
$("#changePin").addEventListener("click", () => send("changePin", {
  old: $("#pinOld").value, new: $("#pinNew").value, confirm: $("#pinNew2").value,
}));
$("#copyRecovery").addEventListener("click", () => send("copyRecovery"));
$("#saveRecoveryEmail").addEventListener("click", () => send("saveRecoveryEmail", { email: $("#recoveryEmail").value.trim() }));
$("#saveMail").addEventListener("click", () => send("saveMail", {
  host: $("#smtpHost").value.trim(), port: $("#smtpPort").value.trim(),
  ssl: $("#smtpSsl").checked, user: $("#smtpUser").value.trim(), pass: $("#smtpPass").value,
}));
$("#installService").addEventListener("click", () => send("installService"));
$("#uninstallService").addEventListener("click", () => send("uninstallService"));
for (const [id, name] of [["#startWithWindows", "startWithWindows"], ["#guardOnLaunch", "guardOnLaunch"], ["#bedtime", "bedtime"]]) {
  $(id).addEventListener("change", () => send("setFlag", { name, value: $(id).checked }));
}
$$(".break-pill").forEach((el) => el.addEventListener("click", () =>
  send("setFlag", { name: "breakReminder", value: Number(el.dataset.break) })));

// 统计页 · 时间范围
$$(".range-pill").forEach((el) => el.addEventListener("click", () =>
  send("statsRange", { days: Number(el.dataset.statrange) })));

// 帮助页
$("#spikeBtn").addEventListener("click", () => send("spike"));
$("#copyFeedback").addEventListener("click", () => send("copyFeedback"));
$("#openMailApp").addEventListener("click", () => send("openMailApp"));
$("#openLogs").addEventListener("click", () => send("openLogs"));
$("#sponsorBtn").addEventListener("click", () => send("sponsor"));

function readFirstRunPin() {
  return { pin: $("#pinCreate").value, confirm: $("#pinConfirm").value };
}
function commitInput(sel, fn) {
  const el = $(sel);
  const v = el.value.trim();
  if (v) { el.value = ""; fn(v); }
}

// 数字输入只留数字
document.addEventListener("input", (e) => {
  if (e.target.matches("input[inputmode=numeric]")) {
    e.target.value = e.target.value.replace(/[^\d]/g, "");
  }
});

// 启动握手：页面就绪后 C# 开始推全量状态
if (hasHost) send("ready");

/* ============================================================
   Demo 模式（浏览器直接打开时，便于设计走查）
   ============================================================ */
function demoSend(cmd, args) {
  if (cmd === "statsShow") {
    dispatch("stats", demoStats(7));
  } else if (cmd === "statsRange") {
    dispatch("stats", demoStats(args.days || 7));
  } else if (cmd === "deskSelect") {
    const desk = { ...LAST["form.desk"], selected: args.id, unrestricted: args.id === "fullpc" };
    dispatch("form.desk", desk);
    dispatch("form.limits", { ...LAST["form.limits"], unrestricted: args.id === "fullpc" });
  } else if (cmd === "dayTab") {
    dispatch("form.duration", { ...LAST["form.duration"], tab: args.tab });
  } else if (cmd === "setPresetDuration" && args.minutes !== "custom") {
    const d = LAST["form.duration"];
    dispatch("form.duration", { ...d, preset: args.minutes, minutesText: `${args.minutes} 分钟` });
  } else if (cmd === "nav") {
    showPage(args.page);
  }
}

/* demo 专用：右下角小工具条，切换三种视图便于走查 */
if (!hasHost) {
  const bar = document.createElement("div");
  bar.id = "demoBar";
  bar.innerHTML =
    '<span>预览</span>' +
    '<button data-demo-view="parent">家长视图</button>' +
    '<button data-demo-view="desk">书桌守护中</button>' +
    '<button data-demo-view="timeup">时间用完</button>';
  document.body.appendChild(bar);
  bar.addEventListener("click", (e) => {
    const b = e.target.closest("[data-demo-view]");
    if (b) showDemoView(b.dataset.demoView);
  });
}

function showDemoView(v) {
  const base = LAST["state"] || {};
  if (v === "parent") {
    dispatch("state", { ...base, view: "parent", closeHidden: false });
  } else if (v === "desk") {
    dispatch("state", {
      ...base, view: "desk", closeHidden: false,
      caption: "今天还剩", remainingText: "0:42:18",
      childHint: "只能用 记事本、计算器、Word。其它软件会被关掉。",
      tiles: [{ name: "记事本" }, { name: "计算器" }, { name: "Word" }, { name: "词典" }],
      blocked: ["Google Chrome"], askParent: true, askMore: false,
    });
  } else {
    dispatch("state", {
      ...base, view: "timeup", closeHidden: true, remainingText: "00:00",
      childHint: "明天早上自动恢复；家长可以加时，或输入密码结束守护。",
      tiles: [], blocked: [], askParent: true, askMore: true,
    });
  }
}

if (!hasHost) {
  const names = ["记事本", "Google Chrome"];
  dispatch("state", {
    view: "parent",
    pill: { text: "守护中", tone: "accent" },
    closeHidden: false,
    caption: "今天还剩", remainingText: "0:42:18",
    childHint: "只能用 记事本、计算器。其它软件会被关掉。",
    tiles: [{ name: "记事本" }, { name: "计算器" }, { name: "Word" }],
    blocked: names, askParent: true, askMore: true,
    engine: { ok: true, title: "守护服务已连接", detail: "以系统权限执行，孩子关不掉。" },
  });
  dispatch("dashboard", {
    greeting: "下午好", heroLine: "守护进行中 · 孩子今天已用 1 小时 18 分，还剩 42 分钟。",
    budget: {
      fraction: .66, remainingText: "42分", usedText: "已用 1 小时 18 分 / 共 2 小时",
      hint: "正在守护。时间用完会自动锁到系统桌面。",
      weekdayText: "周内每天 1 小时", weekendText: "周末每天 2 小时",
    },
    deskCards: [
      { id: "homework", name: "写作业", summary: "文档 + 词典 + 计算器" },
      { id: "class", name: "网课", summary: "浏览器 + 笔记" },
      { id: "code", name: "编程", summary: "IDE + 终端" },
    ],
    week: {
      empty: false,
      rows: ["今天", "9/1 周一", "8/31 周日", "8/30 周六", "8/29 周五", "8/28 周四", "8/27 周三"]
        .map((label, i) => ({ label, fraction: [.7, .5, .9, .4, .8, .3, .6][i], summary: "已用 1 小时" })),
    },
    appUsage: {
      empty: false, hint: "3 款在用 · 1 款额度用完", changed: true,
      rows: [
        { name: "Word", summary: "42 分钟", fraction: .8, over: false },
        { name: "记事本", summary: "18 分钟", fraction: .4, over: false },
        { name: "Chrome", summary: "已用完", fraction: 1, over: true },
      ],
    },
    blocked: names,
    guardBtn: { text: "暂停守护", enabled: false }, rewardVisible: true,
    currentDesk: { name: "写作业", summary: "写作业时只留这几款", apps: ["Word", "记事本", "计算器"], empty: false },
    engineText: "守护服务已连接：以系统权限执行，孩子关不掉。", dashHint: "",
  });
  dispatch("form.head", { title: "家长设置", lead: "改时长或软件后点开始守护。孩子在守护画面里改不了。" });
  dispatch("form.desk", {
    selected: "homework", guardEnabled: true, unrestricted: false,
    desks: [
      { id: "fullpc", name: "整个电脑", summary: "不限制软件，只按时长锁屏" },
      { id: "homework", name: "写作业", summary: "文档 + 词典 + 计算器" },
      { id: "class", name: "网课", summary: "浏览器 + 笔记" },
      { id: "code", name: "编程", summary: "IDE + 终端" },
    ],
    apps: [{ key: "word", name: "Word" }, { key: "notepad", name: "记事本" }, { key: "calc", name: "计算器" }],
    sites: {
      hint: "浏览器（Chrome / Edge）里，勾选的类别和禁止的网站会被拦掉，写作业时还可整机断网。",
      allowed: ["ke.qq.com"], blocked: ["youku.com"],
      categories: { video: true, games: false, adult: false },
    },
  });
  dispatch("form.duration", {
    tab: "weekday", minutesText: "1 小时", otherDays: "周末每天 2 小时", preset: 60, customValue: 45,
    whatHappens: "用完后，名单外的软件会被关掉，直到明天或输入家长密码。",
  });
  dispatch("form.schedule", {
    rows: ["周一", "周二", "周三", "周四", "周五", "周六", "周日"]
      .map((label, day) => ({ day, label, minutes: day === 5 ? 180 : 60, custom: day === 5 })),
  });
  dispatch("form.limits", {
    choices: [{ key: "word", name: "Word" }, { key: "notepad", name: "记事本" }],
    rows: [{ key: "game", name: "Steam", summary: "每天 30 分钟" }],
  });
  dispatch("form.pin", {
    firstRun: false, recoveryCode: "5312-8890",
    recoveryHint: "忘记密码时用，请抄下来或点「复制」存好。", recoveryEmail: "parent@example.com",
  });
  dispatch("form.mail", { host: "smtp.qq.com", port: "587", ssl: true, user: "" });
  dispatch("form.service", {
    installed: true,
    statusText: "守护服务已安装并在运行：开机自动守护已生效，进程孩子杀不掉。",
    installLabel: "重新安装/启动",
  });
  dispatch("form.flags", { startWithWindows: true, guardOnLaunch: true, bedtime: true, breakReminder: 45 });
  dispatch("form.preview", {
    head: "点「开始守护」后，以下规则会同时生效：",
    lines: [
      "只能用「写作业」书桌里的 9 款软件；名单外的会被关掉",
      "浏览器只能打开 2 个白名单网站",
      "每天最多 1 小时（周末 2 小时）；时间用完自动锁屏，次日恢复",
      "其中 1 款软件单独限了 30 分钟，用完只关它",
      "睡觉时段（22:00 – 07:00）到点断网",
    ],
  });
  dispatch("help", {
    engineText: "守护服务已连接：以系统权限执行，孩子关不掉。",
    aboutText: "澄时 v0.1.3 · 所有设置和记录都只存在这台电脑上，不会上传。",
    feedbackEmail: "sakz886@sina.com",
  });
}

function demoStats(range) {
  const days = [];
  const now = new Date();
  for (let i = range; i >= 1; i--) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - i);
    const minutes = [42, 61, 55, 0, 88, 120, 96, 70, 63, 110, 58, 90, 47, 75, 84, 66, 99, 52, 80, 118, 45, 73, 91, 60, 102, 68, 56, 87, 74, 95][d.getDate() % 30];
    days.push({
      date: d,
      label: `${d.getMonth() + 1}/${d.getDate()}`,
      weekday: "周" + "日一二三四五六"[d.getDay()],
      minutes,
      blocked: d.getDate() % 4 === 0 ? 2 : 0,
      apps: minutes === 0 ? {} : { "WINWORD.EXE": 38, "chrome.exe": 24, "notepad.exe": 12, "mspaint.exe": 6 },
    });
  }
  days.push({
    date: now, label: "今天", weekday: "周" + "日一二三四五六"[now.getDay()],
    minutes: 78, blocked: 1,
    apps: { "WINWORD.EXE": 42, "chrome.exe": 21, "notepad.exe": 15 },
  });

  const names = { "WINWORD.EXE": "Word", "chrome.exe": "Google Chrome", "notepad.exe": "记事本", "mspaint.exe": "画图" };
  const nameOf = (k) => names[k] || k.replace(/\.exe$/i, "");
  const fmt = (m) => (m >= 60 ? `${Math.floor(m / 60)} 小时${m % 60 ? ` ${m % 60} 分` : ""}` : `${m} 分钟`);

  const maxMinutes = Math.max(...days.map((d) => d.minutes), 1);
  const daily = days.map((d) => ({
    label: d.label, totalText: fmt(d.minutes),
    fraction: Math.max(.02, d.minutes / maxMinutes), blocked: d.blocked,
  }));

  const totals = {};
  for (const d of days) for (const [k, m] of Object.entries(d.apps)) totals[k] = (totals[k] || 0) + m;
  const ranked = Object.entries(totals).sort((a, b) => b[1] - a[1]);
  const totalApp = ranked.reduce((sum, [, m]) => sum + m, 0);
  const ranking = ranked.map(([k, m]) => ({
    name: nameOf(k), minutesText: fmt(m),
    share: `${Math.round((100 * m) / totalApp)}%`, fraction: Math.max(.02, m / ranked[0][1]),
  }));

  const totalMinutes = days.reduce((sum, d) => sum + d.minutes, 0);
  const detail = days.slice().reverse().map((d) => ({
    dateText: d.label === "今天" ? "今天" : `${d.label} ${d.weekday}`,
    totalText: fmt(d.minutes),
    blockedText: d.blocked > 0 ? `${d.blocked} 次` : "—",
    topApps: Object.entries(d.apps).sort((a, b) => b[1] - a[1]).slice(0, 3)
      .map(([k]) => nameOf(k)).join("、"),
  }));

  return {
    range,
    summary: {
      totalText: fmt(totalMinutes),
      avgText: fmt(Math.round(totalMinutes / days.length)),
      appCount: ranked.length,
      blockedText: `${days.reduce((sum, d) => sum + d.blocked, 0)} 次`,
    },
    daily, ranking, detail,
  };
}
