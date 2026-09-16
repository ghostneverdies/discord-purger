'use strict';

const $ = (id) => document.getElementById(id);

function el(tag, cls, html) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (html !== undefined) n.innerHTML = html;
    return n;
}

function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}

const AV_COLS = ['#5865f2', '#ed4245', '#faa61a', '#57f287', '#eb459e', '#3ba55c', '#9b59b6', '#e67e22'];
function nameColor(name) {
    let s = 0;
    for (const c of String(name)) s += c.codePointAt(0);
    return AV_COLS[s % AV_COLS.length];
}
function initialsOf(name) {
    const t = String(name || '?').trim();
    return (t[0] || '?').toUpperCase();
}

const avatarCache = new Map();
function avatarNode(name, url) {
    const a = el('div', 'avatar');
    a.textContent = initialsOf(name);
    if (url && avatarCache.has(url)) {
        a.style.background = 'transparent';
        a.textContent = '';
        a.appendChild(avatarCache.get(url).cloneNode(true));
        return a;
    }
    if (url) {
        a.style.background = 'transparent';
        const img = el('img');
        img.src = url;
        img.onload = () => {
            avatarCache.set(url, img);
            a.textContent = '';
            a.appendChild(img);
        };
        img.onerror = () => {
            a.style.background = nameColor(name);
        };
    } else {
        a.style.background = nameColor(name);
    }
    return a;
}

const state = {
    account: null,
    knownAccounts: [],
    nav: [],
    tab: 'servers',
    railOpen: true,
    dms: [],
    groups: [],
    guilds: [],
    channels: new Map(),
    view: { type: 'servers' },
    selectedServers: new Set(),
    selected: new Map(),
    purging: false,
    delay: 0.8,
    deletedCount: 0,
    toastTimer: null,
    manualMode: false,
    preManual: 'extract',
    manualSubmit: null,
    foundCount: 0,
    discordRunning: true,
    loginStage: 'initial',
    switcherOpen: false,
    addOpen: false,
};

const UI = {
    splash:      $('splash'),
    login:       $('login'),
    main:        $('main'),
    loginCard:   $('login-card'),
    loginErr:    $('login-err'),
    loginStatus: $('login-status'),
    loginSub:    $('login-sub'),
    token:       $('token'),
    scanBtn:     $('scan-btn'),
    manualBtn:   $('manual-btn'),
    manualForm:  $('manual-form'),
    cornerLink:  $('corner-link'),
    dontSeeBtn:  $('dont-see-btn'),
    rescanResultBtn: $('rescan-result-btn'),
    noAcctActions:$('no-acct-actions'),
    rescanBtn:   $('rescan-btn'),
    noacctManualBtn: $('noacct-manual-btn'),
    globBack:    $('glob-back'),
    scanOverlay: $('scanning-overlay'),
    scanSub:     $('scanning-sub'),
    scanFill:    $('scanning-fill'),
    acctList:    $('acct-list'),
    rail:        $('rail'),
    burger:      $('burger'),
    cats:        $('cats'),
    pill:        $('cat-pill'),
    acctMini:    $('acct-mini'),
    acctSwitcher:$('acct-switcher'),
    switcherList:$('switcher-list'),
    switcherAdd: $('switcher-add'),
    acctAv:      $('acct-av'),
    acctName:    $('acct-name'),
    acctTag:     $('acct-tag'),
    listTitle:   $('list-title'),
    listBody:    $('list-body'),
    selectAll:   $('select-all'),
    loadingOv:   $('loading-overlay'),
    loadingLbl:  $('loading-label'),
    netDot:      $('net-dot'),
    netText:     $('net-text'),
    hdrAcct:     $('hdr-acct'),
    dMinus:      $('d-minus'),
    dPlus:       $('d-plus'),
    dVal:        $('d-val'),
    startBtn:    $('start-btn'),
    stopBtn:     $('stop-btn'),
    ctrl:        document.querySelector('.ctrl'),
    sel:         $('sel'),
    selCount:    $('sel-count'),
    selChips:    $('sel-chips'),
    clearSel:    $('clear-sel'),
    log:         $('log'),
    logDelta:    $('log-delta'),
    acctVeil:    $('acct-veil'),
    toast:       $('toast'),
    addOverlay:  $('acct-add-overlay'),
    addCard:     $('acct-add-card'),
    addClose:    $('acct-add-close'),
    addToken:    $('acct-add-token'),
    addErr:      $('acct-add-err'),
    addStatus:   $('acct-add-status'),
    addSubmit:   $('acct-add-submit'),
};

function logLine(level, text) {
    if (state.purging) UI.logDelta.innerHTML = `🗑 ${state.deletedCount}`;
    const line = el('div', `log-line ${level}`, esc(text));
    UI.log.appendChild(line);
    while (UI.log.childElementCount > 400) UI.log.removeChild(UI.log.firstChild);
    UI.log.scrollTop = UI.log.scrollHeight;
}

function toast(text, kind) {
    UI.toast.textContent = text;
    UI.toast.className = `toast show ${kind || ''}`;
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => { UI.toast.className = 'toast'; }, 2600);
}

window.addEventListener('DOMContentLoaded', async () => {
    bindEvents();
    try {
        const s = await window.bridge.getState();
        if (s.account) { state.account = s.account; enterMain(); return; }
    } catch { }
    UI.splash.classList.add('hide');
    UI.login.classList.remove('hidden');
    runScan();
});

function bindEvents() {
    UI.token.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') doLogin();
        if (e.ctrlKey && (e.key === 'c' || e.key === 'x')) e.preventDefault();
    });
    UI.token.addEventListener('contextmenu', (e) => e.preventDefault());
    UI.scanBtn.addEventListener('click', runScan);
    UI.manualBtn.addEventListener('click', switchToManual);
    UI.cornerLink.addEventListener('click', switchToManual);
    UI.dontSeeBtn.addEventListener('click', () => openAddOverlay());
    UI.rescanBtn.addEventListener('click', runScan);
    UI.rescanResultBtn.addEventListener('click', runScan);
    UI.noacctManualBtn.addEventListener('click', switchToManual);
    UI.globBack.addEventListener('click', goBack);
    UI.acctMini.addEventListener('click', toggleSwitcher);
    UI.switcherAdd.addEventListener('click', () => {
        if (state.purging) return;
        closeSwitcher();
        openAddOverlay();
    });
    UI.addClose.addEventListener('click', closeAddOverlay);
    UI.addOverlay.addEventListener('mousedown', (e) => {
        if (e.target === UI.addOverlay) closeAddOverlay();
    });
    UI.addToken.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') submitAddAccount();
        if (e.ctrlKey && (e.key === 'c' || e.key === 'x')) e.preventDefault();
    });
    UI.addToken.addEventListener('contextmenu', (e) => e.preventDefault());
    UI.addSubmit.addEventListener('click', submitAddAccount);
    document.addEventListener('mousedown', (e) => {
        if (!state.switcherOpen) return;
        if (UI.acctMini.contains(e.target) || UI.acctSwitcher.contains(e.target)) return;
        closeSwitcher();
    });
    UI.acctVeil.addEventListener('mousedown', (e) => {
        if (state.switcherOpen) { e.preventDefault(); closeSwitcher(); }
    });
    UI.acctVeil.addEventListener('wheel', (e) => {
        if (state.switcherOpen) e.preventDefault();
    }, { passive: false });
    UI.acctVeil.addEventListener('touchmove', (e) => {
        if (state.switcherOpen) e.preventDefault();
    }, { passive: false });
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape') return;
        if (state.addOpen) closeAddOverlay();
        else if (state.switcherOpen) closeSwitcher();
    });
}

function showLoading(label) {
    UI.loadingLbl.textContent = label;
    UI.loadingOv.classList.remove('hidden');
}
function hideLoading() {
    UI.loadingOv.classList.add('hidden');
}

function setScanning(on) {
    UI.scanOverlay.classList.toggle('hidden', !on);
    UI.scanBtn.disabled = on;
    if (on) UI.loginErr.textContent = '';
    if (on) startFakeProgress();
    else resetFakeProgress();
}

let progressRAF = null;
let progressStart = 0;
let progressDone = false;

function startFakeProgress() {
    cancelAnimationFrame(progressRAF);
    progressStart = performance.now();
    progressDone = false;
    UI.scanFill.style.width = '0%';
    tickFakeProgress();
}

function resetFakeProgress() {
    cancelAnimationFrame(progressRAF);
    progressRAF = null;
    progressDone = false;
    UI.scanFill.style.width = '0%';
}

function tickFakeProgress() {
    if (!progressStart) return;
    const t = (performance.now() - progressStart) / 1000;
    let pct;
    if (t < 8) {
        pct = 80 * (1 - Math.pow(1 - t / 8, 2.2));
    } else if (t < 60) {
        pct = 80 + 10 * (1 - Math.exp(-(t - 8) / 30));
    } else {
        pct = 87 + 5 * (1 - Math.exp(-(t - 60) / 90));
    }
    if (!progressDone) UI.scanFill.style.width = `${Math.min(92, pct)}%`;
    if (!progressDone) progressRAF = requestAnimationFrame(tickFakeProgress);
}

function completeFakeProgress() {
    progressDone = true;
    cancelAnimationFrame(progressRAF);
    UI.scanFill.style.width = '100%';
}

async function runScan() {
    setScanning(true);
    UI.scanSub.textContent = 'Reading Discord & browser storage…';
    try {
        const res = await window.bridge.scanAccounts();
        if (state.manualMode) return;
        state.knownAccounts = res.accounts;
        state.discordRunning = res.discordRunning;
        renderDetectedAccounts(res.accounts);
        setScanResultMessage(res.accounts.length, res.discordRunning);
        UI.loginSub.className = 'login-sub dets';
    } catch (e) {
        if (state.manualMode) return;
        UI.loginErr.textContent = `✗  ${e.code}: ${e.message}`;
        UI.loginSub.textContent = 'Detect accounts logged in on this PC automatically.';
        UI.loginSub.className = 'login-sub';
    } finally {
        if (!state.manualMode) setScanning(false);
    }
}

function setScanResultMessage(count, discordRunning) {
    if (count > 1) UI.loginSub.textContent = `Found ${count} accounts on this PC.`;
    else if (count === 1) UI.loginSub.textContent = 'Found 1 account on this PC.';
    else if (discordRunning) UI.loginSub.textContent = 'No accounts found on this PC.';
    else UI.loginSub.textContent = 'No accounts found. Open Discord, sign in, then scan again.';
}

function renderDetectedAccounts(accounts) {
    state.foundCount = accounts.length;
    UI.acctList.replaceChildren();

    if (accounts.length === 0) {
        state.loginStage = 'nofound';
        UI.acctList.classList.add('hidden');
        UI.scanBtn.classList.add('hidden');
        UI.manualBtn.classList.add('hidden');
        UI.dontSeeBtn.classList.add('hidden');
        UI.rescanResultBtn.classList.add('hidden');
        UI.noAcctActions.classList.remove('hidden');
        UI.cornerLink.classList.add('hidden');
        updateGlobBack();
        return;
    }

    state.loginStage = 'results';
    UI.acctList.classList.remove('hidden');
    UI.scanBtn.classList.add('hidden');
    UI.manualBtn.classList.add('hidden');
    UI.dontSeeBtn.classList.remove('hidden');
    UI.rescanResultBtn.classList.remove('hidden');
    UI.noAcctActions.classList.add('hidden');
    UI.cornerLink.classList.add('hidden');

    accounts.forEach((a, i) => {
        const card = el('div', 'acct-pick');
        card.style.animationDelay = `${i * 60}ms`;
        card.appendChild(avatarNode(a.username, a.avatarUrl));
        const meta = el('div', 'acct-pick-meta');
        meta.appendChild(el('div', 'acct-pick-name', esc(a.globalName || a.username)));
        const disc = a.discriminator === '0' ? `@${a.username}` : `${a.username}#${a.discriminator}`;
        meta.appendChild(el('div', 'acct-pick-tag', esc(disc)));
        card.appendChild(meta);
        card.appendChild(el('span', 'acct-pick-arrow', '›'));
        card.addEventListener('click', () => pickAccount(a.id));
        UI.acctList.appendChild(card);
    });
    updateGlobBack();
}

function showLoginInit() {
    state.manualMode = false;
    state.loginStage = 'initial';
    UI.manualForm.classList.add('hidden');
    if (state.manualSubmit) { state.manualSubmit.remove(); state.manualSubmit = null; }
    UI.acctList.classList.add('hidden');
    UI.acctList.replaceChildren();
    UI.dontSeeBtn.classList.add('hidden');
    UI.rescanResultBtn.classList.add('hidden');
    UI.noAcctActions.classList.add('hidden');
    UI.scanBtn.classList.remove('hidden');
    UI.manualBtn.classList.remove('hidden');
    UI.cornerLink.classList.remove('hidden');
    UI.loginSub.textContent = 'Detect accounts logged in on this PC automatically.';
    UI.loginSub.className = 'login-sub';
    UI.loginErr.textContent = '';
    UI.loginStatus.textContent = '';
    updateGlobBack();
}

async function pickAccount(id) {
    UI.loginErr.textContent = '';
    UI.loginStatus.textContent = 'Activating…';
    try {
        state.account = await window.bridge.selectAccount({ id });
        upsertKnownAccount(state.account);
        enterMain();
    } catch (e) {
        UI.loginErr.textContent = `✗  ${e.code}: ${e.message}`;
        UI.loginStatus.textContent = '';
    }
}

function switchToManual() {
    state.manualMode = true;
    state.preManual = state.loginStage === 'initial' ? 'extract' : 'accounts';
    setScanning(false);
    UI.manualForm.classList.remove('hidden');
    UI.scanBtn.classList.add('hidden');
    UI.manualBtn.classList.add('hidden');
    UI.acctList.classList.add('hidden');
    UI.dontSeeBtn.classList.add('hidden');
    UI.rescanResultBtn.classList.add('hidden');
    UI.noAcctActions.classList.add('hidden');
    UI.cornerLink.classList.add('hidden');
    UI.globBack.classList.remove('hidden');
    UI.loginSub.textContent = 'Paste your Discord user token to continue.';
    UI.loginSub.className = 'login-sub';
    if (!state.manualSubmit) {
        const submit = el('button', 'btn btn-accent btn-block', 'Validate &amp; Continue');
        submit.id = 'manual-submit';
        submit.addEventListener('click', doLogin);
        UI.manualForm.after(submit);
        state.manualSubmit = submit;
    }
    UI.token.focus();
}

function backFromManual() {
    state.manualMode = false;
    UI.manualForm.classList.add('hidden');
    if (state.manualSubmit) { state.manualSubmit.remove(); state.manualSubmit = null; }
    UI.loginErr.textContent = '';
    UI.loginStatus.textContent = '';
    if (state.preManual === 'accounts') {
        if (state.foundCount === 0) {
            state.loginStage = 'nofound';
            UI.acctList.classList.add('hidden');
            UI.scanBtn.classList.add('hidden');
            UI.manualBtn.classList.add('hidden');
            UI.dontSeeBtn.classList.add('hidden');
            UI.rescanResultBtn.classList.add('hidden');
            UI.noAcctActions.classList.remove('hidden');
            UI.cornerLink.classList.add('hidden');
            setScanResultMessage(state.foundCount, state.discordRunning);
            UI.loginSub.className = 'login-sub dets';
        } else {
            renderDetectedAccounts(state.knownAccounts);
            setScanResultMessage(state.foundCount, state.discordRunning);
            UI.loginSub.className = 'login-sub dets';
        }
    } else {
        showLoginInit();
    }
}

async function doLogin() {
    const token = UI.token.value.trim();
    if (!token) { showLoginError('Please paste your token first.'); return; }
    showLoginLoading(true);
    try {
        state.account = await window.bridge.validateToken({ token });
        upsertKnownAccount(state.account);
        showLoginLoading(false);
        enterMain();
    } catch (e) {
        showLoginLoading(false);
        showLoginError(`${e.code}: ${e.message}`);
    }
}

function showLoginError(msg) {
    UI.loginErr.textContent = `✗  ${msg}`;
    UI.loginCard.classList.remove('shake');
    void UI.loginCard.offsetWidth;
    UI.loginCard.classList.add('shake');
}

function showLoginLoading(on) {
    UI.loginStatus.textContent = on ? 'Validating token…' : '';
}

function upsertKnownAccount(acc) {
    const existing = state.knownAccounts.find((a) => a.id === acc.id);
    if (existing) Object.assign(existing, acc);
    else state.knownAccounts.push(acc);
}

function resetMainState() {
    state.dms = [];
    state.groups = [];
    state.guilds = [];
    state.channels = new Map();
    state.selectedServers = new Set();
    state.selected = new Map();
    state.tab = 'servers';
    state.view = { type: 'servers' };
    state.nav = [];
    state.deletedCount = 0;
    UI.log.replaceChildren();
}

async function enterMain() {
    const firstEntry = UI.main.classList.contains('hidden');
    if (firstEntry) {
        UI.main.classList.remove('hidden');
        UI.login.classList.add('hidden');
    } else {
        UI.main.classList.remove('anim-in');
        void UI.main.offsetWidth;
        UI.main.classList.add('anim-in');
    }
    UI.splash.classList.add('hide');

    const a = state.account;
    UI.acctName.textContent = a.globalName || a.username;
    UI.acctTag.textContent  = (a.discriminator === '0' ? '@' : '') + a.username +
                              (a.discriminator !== '0' ? `#${a.discriminator}` : '');
    UI.hdrAcct.textContent  = `@${a.username}`;
    UI.acctAv.replaceChildren(avatarNode(a.username, a.avatarUrl));
    updateGlobBack();

    if (!firstEntry) logLine('inf', `Switching to ${a.username}…`);
    else logLine('ok', `✓  Logged in as ${a.username}.`);

    showLoading('Loading your conversations…');

    const minSpin = new Promise((r) => setTimeout(r, 5000));
    const loaded = (async () => {
        const [privRes, guildRes] = await Promise.allSettled([
            window.bridge.getDms(),
            window.bridge.getGuilds()
        ]);

        if (privRes.status === 'fulfilled') {
            state.dms = privRes.value.filter((c) => c.kind === 'dm');
            state.groups = privRes.value.filter((c) => c.kind === 'group');
        } else {
            const e = privRes.reason;
            logLine('err', `✗  ${e.message}`);
            toast(`${e.code}: ${e.message}`, 'err');
        }

        if (guildRes.status === 'fulfilled') state.guilds = guildRes.value;
        else logLine('err', `✗  ${guildRes.reason.message}`);
    })();

    await Promise.all([minSpin, loaded]);

    hideLoading();

    render(true);
    logLine('ok', `✓  ${state.dms.length} DMs, ${state.groups.length} groups, ${state.guilds.length} servers.`);

    prefetchChannels();
    restartPreload();
}

function restartPreload() {
    if (!state.account) return;
    const ids = [state.account.id, ...state.knownAccounts.map((x) => x.id).filter((i) => i !== state.account.id)];
    window.bridge.preloadAccounts({ ids }).catch(() => {});
}

function updateGlobBack() {
    const inLogin = !UI.login.classList.contains('hidden');
    const onSplash = !UI.splash.classList.contains('hide');

    let hidden = onSplash;
    if (!hidden && inLogin) {
        hidden = state.loginStage === 'initial';
    }
    if (!hidden && !inLogin) {
        hidden = state.view.type === 'servers' && state.nav.length === 0;
    }
    UI.globBack.classList.toggle('hidden', hidden);
}

function goBack() {
    if (state.addOpen) { closeAddOverlay(); return; }
    if (state.switcherOpen) { closeSwitcher(); return; }
    if (state.manualMode) { backFromManual(); return; }
    if (!UI.login.classList.contains('hidden')) {
        if (state.loginStage === 'nofound' || state.loginStage === 'results') {
            showLoginInit();
        }
        return;
    }
    if (state.view.type === 'server') {
        state.view = { type: 'servers' };
        render(false);
        updateGlobBack();
        return;
    }
    const prev = state.nav.pop();
    if (prev) {
        state.tab = prev.tab;
        state.view = prev.view;
        render(false);
        updateGlobBack();
    }
}

function toggleSwitcher() {
    if (state.purging) return;
    if (state.switcherOpen) closeSwitcher();
    else openSwitcher();
}

function openSwitcher() {
    state.switcherOpen = true;
    UI.acctSwitcher.classList.remove('hidden', 'closing');
    UI.acctMini.classList.add('open');
    UI.acctVeil.classList.add('open');
    renderSwitcher();
    updateGlobBack();
}

function closeSwitcher() {
    if (!state.switcherOpen) return;
    state.switcherOpen = false;
    UI.acctMini.classList.remove('open');
    UI.acctVeil.classList.remove('open');
    UI.acctSwitcher.classList.add('closing');
    setTimeout(() => {
        UI.acctSwitcher.classList.add('hidden');
        UI.acctSwitcher.classList.remove('closing');
    }, 220);
    updateGlobBack();
}

function renderSwitcher() {
    UI.switcherList.replaceChildren();
    const activeId = state.account?.id;
    const list = [...state.knownAccounts].sort((a, b) =>
        (a.id === activeId ? -1 : b.id === activeId ? 1 : 0));

    list.forEach((a, i) => {
        const row = el('div', `switcher-row${a.id === activeId ? ' on' : ''}`);
        row.style.animationDelay = `${Math.min(i * 30, 150)}ms`;
        row.appendChild(avatarNode(a.username, a.avatarUrl));
        const meta = el('div', 'switcher-meta');
        meta.appendChild(el('div', 'switcher-name', esc(a.globalName || a.username)));
        const disc = a.discriminator === '0' ? `@${a.username}` : `${a.username}#${a.discriminator}`;
        meta.appendChild(el('div', 'switcher-tag', esc(disc)));
        row.appendChild(meta);
        if (a.id === activeId) row.appendChild(el('span', 'switcher-check', '✓'));
        row.addEventListener('click', () => switchAccount(a.id));
        UI.switcherList.appendChild(row);
    });
}

async function switchAccount(id) {
    if (state.purging || !state.account || id === state.account.id) { closeSwitcher(); return; }
    closeSwitcher();
    try {
        state.account = await window.bridge.selectAccount({ id });
        upsertKnownAccount(state.account);
        UI.main.classList.remove('anim-in');
        void UI.main.offsetWidth;
        UI.main.classList.add('anim-in');
        enterMain();
    } catch (e) {
        toast(`${e.code}: ${e.message}`, 'err');
    }
}

function openAddOverlay() {
    if (state.purging) return;
    state.addOpen = true;
    UI.addOverlay.classList.remove('hidden', 'closing');
    UI.addToken.value = '';
    UI.addErr.textContent = '';
    UI.addStatus.textContent = '';
    UI.addSubmit.querySelector('.btn-label').style.display = '';
    UI.addSubmit.querySelector('.btn-spinner').classList.add('hidden');
    UI.addSubmit.disabled = false;
    updateGlobBack();
    setTimeout(() => UI.addToken.focus(), 60);
}

function closeAddOverlay() {
    if (!state.addOpen) return;
    state.addOpen = false;
    UI.addOverlay.classList.add('closing');
    setTimeout(() => {
        UI.addOverlay.classList.add('hidden');
        UI.addOverlay.classList.remove('closing');
    }, 200);
    updateGlobBack();
}

async function submitAddAccount() {
    const token = UI.addToken.value.trim();
    if (!token) { UI.addErr.textContent = '✗ Please paste a token first.'; return; }
    UI.addSubmit.disabled = true;
    UI.addSubmit.querySelector('.btn-label').style.display = 'none';
    UI.addSubmit.querySelector('.btn-spinner').classList.remove('hidden');
    UI.addErr.textContent = '';
    try {
        const acc = await window.bridge.validateToken({ token });
        const wasActive = !!state.account;
        upsertKnownAccount(acc);
        UI.addToken.value = '';
        closeAddOverlay();
        if (wasActive) {
            const cur = state.account;
            if (cur.id !== acc.id) await window.bridge.selectAccount({ id: cur.id });
            toast(`✓ Added @${acc.username}`, 'ok');
            restartPreload();
        } else {
            state.account = acc;
            enterMain();
        }
    } catch (e) {
        UI.addErr.textContent = `✗ ${e.code}: ${e.message}`;
    } finally {
        UI.addSubmit.disabled = false;
        UI.addSubmit.querySelector('.btn-label').style.display = '';
        UI.addSubmit.querySelector('.btn-spinner').classList.add('hidden');
    }
}

function render(first) {
    renderCats();
    renderList(first);
    refreshSelectionUI();
    renderAccount();
}

function renderCats() {
    const items = UI.cats.querySelectorAll('.cat');
    items.forEach((c) => c.classList.toggle('active', c.dataset.tab === state.tab));
    const active = UI.cats.querySelector('.cat.active');
    const idx = [...items].indexOf(active);
    UI.pill.style.top = `${10 + idx * 46 + 6}px`;

    if (state.tab === 'servers' && state.view.type === 'server') {
        UI.listTitle.textContent = state.view.name;
    } else {
        UI.listTitle.textContent =
            state.tab === 'servers' ? 'Servers' : state.tab === 'dms' ? 'Direct Messages' : 'Groups';
    }
}

function renderList(first) {
    UI.listBody.replaceChildren();
    if (state.tab === 'servers') {
        if (state.view.type === 'server') return renderServerChannels();
        return renderServers();
    }
    return renderPrivate(state.tab === 'dms' ? 'dms' : 'groups');
}

function rowOn(el_, on) { el_.classList.toggle('on', !!on); }

function renderServers() {
    if (state.guilds.length === 0) {
        UI.listBody.appendChild(el('div', 'empty', 'No servers on this account.'));
        return;
    }
    state.guilds.forEach((g, i) => {
        const r = el('div', 'row server');
        r.style.animationDelay = `${Math.min(i * 25, 240)}ms`;
        r.dataset.srv = g.id;
        rowOn(r, state.selectedServers.has(g.id));

        r.appendChild(avatarNode(g.name, g.iconUrl));
        const meta = el('div', 'row-meta');
        meta.appendChild(el('div', 'row-name', esc(g.name)));
        const chans = state.channels.get(g.id);
        const sub  = el('div', 'row-sub', chans ? `${chans.filter((c) => c.purgeable).length} channels` : '…');
        meta.appendChild(sub);
        r.appendChild(meta);
        r.appendChild(el('div', 'chk'));
        r.appendChild(el('span', 'go', '›'));

        r.addEventListener('click', (e) => {
            if (state.purging) return;
            if (e.target.closest('.chk')) { toggleServer(g.id); return; }
            openServer(g);
        });
        UI.listBody.appendChild(r);
    });
}

function renderServerChannels() {
    const v = state.view;
    if (!state.channels.has(v.id)) {
        UI.listBody.appendChild(el('div', 'loading-row',
            'loading <span class="typing-dots"><span></span><span></span><span></span>'));
        loadServerChannels(v.id);
        return;
    }

    const chans = (state.channels.get(v.id) || []).filter((c) => c.purgeable);
    if (chans.length === 0) {
        UI.listBody.appendChild(el('div', 'empty', 'No text channels in this server.'));
        return;
    }
    chans.forEach((c, j) => {
        const r = el('div', 'ch-row');
        r.style.animationDelay = `${Math.min(j * 22, 200)}ms`;
        r.dataset.id = c.id;
        r.dataset.srv = v.id;
        rowOn(r, state.selected.has(c.id) || state.selectedServers.has(v.id));

        r.appendChild(el('span', 'ch-name', `# ${esc(c.name)}`));
        r.appendChild(el('div', 'chk'));
        r.addEventListener('click', () => { if (!state.purging) toggleChannel(c); });
        UI.listBody.appendChild(r);
    });
}

function renderPrivate(kind) {
    const list = kind === 'dms' ? state.dms : state.groups;
    const label = kind === 'dms' ? 'DMs' : 'Group DMs';
    if (list.length === 0) { UI.listBody.appendChild(el('div', 'empty', `No ${label.toLowerCase()} found.`)); return; }

    list.forEach((dm, i) => {
        const r = el('div', 'row');
        r.style.animationDelay = `${Math.min(i * 25, 240)}ms`;
        r.dataset.id = dm.id;
        rowOn(r, state.selected.has(dm.id));

        r.appendChild(avatarNode(dm.name, dm.avatarUrl));
        const meta = el('div', 'row-meta');
        meta.appendChild(el('div', 'row-name', esc(dm.name)));
        meta.appendChild(el('div', 'row-sub', kind === 'dms' ? 'Direct Message' : `${dm.memberCount} members`));
        r.appendChild(meta);
        r.appendChild(el('div', 'chk'));
        r.addEventListener('click', () => { if (!state.purging) toggleDm(dm); });
        UI.listBody.appendChild(r);
    });
}

function renderAccount() {
    const a = state.account;
    if (!a) return;
    UI.acctName.textContent = a.globalName || a.username;
    UI.acctTag.textContent  = (a.discriminator === '0' ? '@' : '') + a.username +
                              (a.discriminator !== '0' ? `#${a.discriminator}` : '');
    UI.acctAv.replaceChildren(avatarNode(a.username, a.avatarUrl));
}

function openServer(g) {
    state.nav.push({ tab: 'servers', view: { type: 'servers' } });
    state.view = { type: 'server', id: g.id, name: g.name };
    render(false);
    updateGlobBack();
    if (!state.channels.has(g.id)) loadServerChannels(g.id);
}

function closeServerView() {
    state.view = { type: 'servers' };
    render(false);
    updateGlobBack();
}

async function loadServerChannels(id) {
    try {
        state.channels.set(id, await window.bridge.getGuildChannels({ guildId: id }));
    } catch { state.channels.set(id, []); }
    if (state.view.type === 'server' && state.view.id === id) render(false);
    else refreshSelectionUI();
}

async function ensureChannels(id) {
    if (!state.channels.has(id)) {
        try { state.channels.set(id, await window.bridge.getGuildChannels({ guildId: id })); }
        catch { state.channels.set(id, []); }
    }
    return state.channels.get(id) || [];
}

function toggleDm(dm) {
    if (state.selected.has(dm.id)) state.selected.delete(dm.id);
    else state.selected.set(dm.id, { name: dm.name, kind: 'dm', avatar: dm.avatarUrl });
    refreshSelectionUI();
}

function toggleServer(gid) {
    if (state.selectedServers.has(gid)) state.selectedServers.delete(gid);
    else state.selectedServers.add(gid);
    refreshSelectionUI();
}

function toggleChannel(c) {
    const gid = state.view.id;
    if (state.selectedServers.has(gid)) {
        const chans = (state.channels.get(gid) || []).filter((x) => x.purgeable);
        state.selectedServers.delete(gid);
        for (const cc of chans) {
            if (cc.id === c.id) continue;
            state.selected.set(cc.id, { name: `#${cc.name}`, kind: 'channel', server: gid });
        }
        state.selected.delete(c.id);
    } else if (state.selected.has(c.id)) {
        state.selected.delete(c.id);
    } else {
        state.selected.set(c.id, { name: `#${c.name}`, kind: 'channel', server: gid });
    }
    refreshSelectionUI();
}

function refreshSelectionUI() {
    const n = state.selectedServers.size + state.selected.size;
    UI.selCount.textContent = n === 0 ? 'No conversations selected' : `${n} selected`;
    UI.startBtn.disabled = n === 0 || state.purging;
    UI.clearSel.disabled = n === 0 || state.purging;
    applyRowStates();
    buildChips();
    updateSelectAllLabel();
}

function applyRowStates() {
    UI.listBody.querySelectorAll('.row').forEach((r) => {
        if (r.dataset.srv !== undefined && !r.dataset.id) {
            rowOn(r, state.selectedServers.has(r.dataset.srv));
        } else if (r.dataset.id) {
            rowOn(r, isChannelActive(r.dataset.id));
        }
    });
    UI.listBody.querySelectorAll('.ch-row').forEach((r) => rowOn(r, isChannelActive(r.dataset.id)));
}

function isChannelActive(id) {
    if (state.selected.has(id)) return true;
    for (const gid of state.selectedServers) {
        const chans = state.channels.get(gid);
        if (chans && chans.some((c) => c.id === id)) return true;
    }
    return false;
}

function buildChips() {
    UI.selChips.replaceChildren();
    for (const gid of state.selectedServers) {
        const g = state.guilds.find((x) => x.id === gid);
        if (!g) continue;
        UI.selChips.appendChild(chipNode(g.name, g.iconUrl, 'server', gid));
    }
    for (const [id, item] of state.selected) {
        if (item.server && state.selectedServers.has(item.server)) continue;
        UI.selChips.appendChild(chipNode(item.name, item.avatar, item.kind, id));
    }
}

function chipNode(name, avatar, kind, id) {
    const chip = el('div', 'chip');
    chip.appendChild(avatarNode(name, avatar));
    chip.appendChild(el('span', 'chip-name', esc(name)));
    const x = el('span', 'chip-x', '✕');
    x.addEventListener('click', (e) => {
        e.stopPropagation();
        if (state.purging) return;
        if (kind === 'server') {
            state.selectedServers.delete(id);
            for (const [cid, item] of state.selected) {
                if (item.server === id) state.selected.delete(cid);
            }
        } else {
            state.selected.delete(id);
        }
        refreshSelectionUI();
    });
    chip.appendChild(x);
    return chip;
}

UI.selectAll.addEventListener('click', async () => {
    if (state.purging) return;

    if (state.tab === 'servers') {
        if (state.view.type === 'server') {
            const gid = state.view.id;
            const chans = (await ensureChannels(gid)).filter((c) => c.purgeable);
            if (chans.length === 0) return;
            const allActive = chans.every((c) => isChannelActive(c.id));
            if (allActive) {
                state.selectedServers.delete(gid);
                chans.forEach((c) => state.selected.delete(c.id));
            } else {
                state.selectedServers.add(gid);
            }
            refreshSelectionUI();
            return;
        }
        if (state.guilds.length === 0) return;
        const allSel = state.guilds.every((g) => state.selectedServers.has(g.id));
        state.guilds.forEach((g) => {
            if (allSel) state.selectedServers.delete(g.id);
            else state.selectedServers.add(g.id);
        });
        refreshSelectionUI();
        return;
    }

    const list = state.tab === 'dms' ? state.dms : state.groups;
    if (list.length === 0) return;
    const allSel = list.every((c) => state.selected.has(c.id));
    for (const c of list) {
        if (allSel) state.selected.delete(c.id);
        else state.selected.set(c.id, { name: c.name, kind: 'dm', avatar: c.avatarUrl });
    }
    refreshSelectionUI();
});

UI.clearSel.addEventListener('click', () => {
    if (state.purging) return;
    state.selectedServers.clear();
    state.selected.clear();
    refreshSelectionUI();
});

function updateSelectAllLabel() {
    UI.selectAll.disabled = state.purging;
    let label;
    if (state.tab === 'servers') {
        if (state.view.type === 'server') {
            const chans = state.channels.get(state.view.id);
            if (!chans || !chans.length) { label = 'Select all'; }
            else {
                const p = chans.filter((c) => c.purgeable);
                const all = p.length && p.every((c) => isChannelActive(c.id));
                label = all ? 'Deselect all' : `Select all (${p.length})`;
            }
        } else {
            const all = state.guilds.length && state.guilds.every((g) => state.selectedServers.has(g.id));
            label = all ? 'Deselect all' : state.guilds.length ? `Select all (${state.guilds.length})` : 'Select all';
        }
    } else {
        const list = state.tab === 'dms' ? state.dms : state.groups;
        const all = list.length && list.every((c) => state.selected.has(c.id));
        label = all ? 'Deselect all' : list.length ? `Select all (${list.length})` : 'Select all';
    }
    UI.selectAll.textContent = label;
}

function prefetchChannels() {
    let i = 0;
    async function next() {
        if (state.purging || i >= state.guilds.length) return;
        const g = state.guilds[i++];
        if (!state.channels.has(g.id)) {
            try { state.channels.set(g.id, await window.bridge.getGuildChannels({ guildId: g.id })); }
            catch { state.channels.set(g.id, []); }
            refreshServerSubs(g.id);
        }
        setTimeout(next, 50);
    }
    setTimeout(next, 150);
}

function refreshServerSubs(gid) {
    if (state.tab !== 'servers' || state.view.type !== 'servers') return;
    const row = UI.listBody.querySelector(`.row[data-srv="${gid}"] .row-sub`);
    if (!row) return;
    const chans = state.channels.get(gid);
    if (chans) row.textContent = `${chans.filter((c) => c.purgeable).length} channels`;
    row.classList.remove('loading');
}

UI.cats.addEventListener('click', (e) => {
    const cat = e.target.closest('.cat');
    if (!cat || cat.dataset.tab === state.tab) return;
    state.nav.push({ tab: state.tab, view: state.view });
    state.tab = cat.dataset.tab;
    state.view = { type: 'servers' };
    render(false);
    updateGlobBack();
});
UI.burger.addEventListener('click', () => {
    state.railOpen = !state.railOpen;
    UI.main.classList.toggle('collapsed', !state.railOpen);
});

function setDelay(v) { state.delay = Math.min(10, Math.max(0.3, Math.round(v * 10) / 10)); UI.dVal.textContent = `${state.delay.toFixed(1)} s`; }
UI.dMinus.addEventListener('click', () => { if (!state.purging) setDelay(state.delay - 0.1); });
UI.dPlus.addEventListener('click', () => { if (!state.purging) setDelay(state.delay + 0.1); });

UI.startBtn.addEventListener('click', startPurge);
UI.stopBtn.addEventListener('click', () => { window.bridge.stopPurge(); });

async function buildTargets() {
    const targets = [];
    const seen = new Set();

    for (const gid of state.selectedServers) {
        const g = state.guilds.find((x) => x.id === gid);
        const chans = await ensureChannels(gid);
        for (const c of chans.filter((x) => x.purgeable)) {
            if (seen.has(c.id)) continue;
            seen.add(c.id);
            targets.push({ id: c.id, name: `${g?.name || ''} #${c.name}`, kind: 'channel' });
        }
    }
    for (const [id, item] of state.selected) {
        if (seen.has(id)) continue;
        seen.add(id);
        targets.push({ id, name: item.name, kind: item.kind === 'channel' ? 'channel' : 'dm' });
    }
    return targets;
}

async function startPurge() {
    if (state.purging) return;
    const targets = await buildTargets();
    if (targets.length === 0) return;
    setPurging(true);
    UI.logDelta.textContent = '';
    try { await window.bridge.startPurge({ targets, delayMs: state.delay * 1000 }); }
    catch (e) { setPurging(false); toast(`${e.code}: ${e.message}`, 'err'); }
}

function setPurging(on) {
    state.purging = on;
    state.deletedCount = 0;
    UI.main.classList.toggle('purging', on);
    UI.ctrl.classList.toggle('purging', on);
    UI.stopBtn.disabled = !on;
    UI.dMinus.disabled = on; UI.dPlus.disabled = on;
    UI.startBtn.disabled = on || (state.selectedServers.size + state.selected.size) === 0;
    UI.startBtn.querySelector('.btn-label').style.display = on ? 'none' : '';
    UI.startBtn.querySelector('.btn-spinner').classList.toggle('hidden', !on);
    updateSelectAllLabel();
}

window.bridge.on('scanPhase', ({ text }) => {
    UI.scanSub.textContent = text;
    if (text === 'Validating tokens…') completeFakeProgress();
});
window.bridge.on('netStatus', ({ connected }) => {
    UI.netDot.className = `net-dot ${connected ? 'ok' : 'bad'}`;
    UI.netText.textContent = connected ? 'Connected' : 'No internet';
});
window.bridge.on('log', ({ level, text }) => logLine(level, text));
window.bridge.on('purgeDeleted', ({ total }) => { state.deletedCount = total; });
window.bridge.on('rateLimited', ({ seconds }) => { toast(`Rate limited — waiting ${seconds.toFixed(1)}s`, ''); });
window.bridge.on('purgeDone', ({ total }) => {
    const was = state.purging; setPurging(false); if (!was) return;
    state.deletedCount = total;
    logLine('ok', `✅  Done — ${total} message(s) deleted.`);
    UI.logDelta.textContent = `🗑 ${total}`; UI.logDelta.className = 'log-delta ok';
    toast(`Purge finished — ${total} deleted`, total > 0 ? 'ok' : '');
});