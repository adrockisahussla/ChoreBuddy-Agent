// Bridge to C#
const agent = window.chrome?.webview?.hostObjects?.agent;
async function call(name, ...args) {
  if (!agent) throw new Error('Agent bridge not available');
  return await agent[name](...args);
}

// State
const state = {
  step: 0,
  isAdmin: false,
  signedIn: false,
  managerName: null,
  signInError: null,
  signingIn: false,
  buddies: [],
  buddiesLoaded: false,
  selectedBuddy: null,
  apps: [],
  installLog: [],
  installSuccess: false,
  installDone: false,
};

const ACCENT_COLORS = ['#8b5cf6', '#f97316', '#22c55e', '#f43f5e', '#6366f1', '#06b6d4'];

const stepLabel = document.getElementById('step-label');
const adminBanner = document.getElementById('admin-banner');
const main = document.getElementById('main');
const backBtn = document.getElementById('back-btn');
const nextBtn = document.getElementById('next-btn');

// Step map (was 0..4, now 0..5 with a sign-in step inserted at 1):
//   0=welcome, 1=signin, 2=buddies, 3=configure, 4=installing, 5=done
backBtn.addEventListener('click', () => {
  if (state.step > 0 && state.step < 4) {
    state.step--;
    render();
  }
});

nextBtn.addEventListener('click', async () => {
  if (state.step === 0) {
    if (!state.isAdmin) return;
    state.step = 1;
    render();
  } else if (state.step === 1) {
    if (!state.signedIn) {
      // Trigger Google sign-in inline.
      await doSignIn();
      return;
    }
    state.step = 2;
    render();
    if (!state.buddiesLoaded) loadBuddies();
  } else if (state.step === 2) {
    if (!state.selectedBuddy) {
      alert('Pick a buddy first.');
      return;
    }
    state.step = 3;
    if (state.apps.length === 0) await loadApps();
    render();
  } else if (state.step === 3) {
    state.step = 4;
    render();
    runInstall();
  } else if (state.step === 5) {
    try { await call('CloseWizard'); } catch { window.close(); }
  }
});

async function doSignIn() {
  state.signingIn = true;
  state.signInError = null;
  render();
  try {
    const raw = await call('SignInWithGoogle');
    const r = JSON.parse(raw);
    if (!r.ok) {
      state.signInError = r.error || 'Sign-in failed';
    } else {
      state.signedIn = true;
      state.managerName = r.managerName;
    }
  } catch (e) {
    state.signInError = e.message || String(e);
  } finally {
    state.signingIn = false;
    render();
  }
}

// Listen for install progress from C#
window.chrome?.webview?.addEventListener?.('message', e => {
  try {
    const msg = JSON.parse(e.data);
    if (msg.type === 'log') {
      state.installLog.push(msg);
      renderInstallLog();
    } else if (msg.type === 'status') {
      const el = document.getElementById('install-status');
      if (el) {
        el.textContent = msg.text;
        el.className = 'install-status ' + (msg.level || '');
      }
    } else if (msg.type === 'done') {
      state.installSuccess = msg.success;
      state.installDone = true;
      state.step = 5;
      render();
    }
  } catch {}
});

async function init() {
  try {
    state.isAdmin = (await call('IsAdmin')) === 'true' || (await call('IsAdmin')) === true;
  } catch {
    state.isAdmin = false;
  }
  try {
    const raw = await call('GetAuthStatus');
    const s = JSON.parse(raw);
    state.signedIn = !!s.signedIn;
    state.managerName = s.managerName || null;
  } catch {}
  render();
}

async function loadBuddies() {
  try {
    const json = await call('GetBuddies');
    const list = JSON.parse(json);
    state.buddies = list.map((b, i) => ({ ...b, accent: ACCENT_COLORS[i % ACCENT_COLORS.length] }));
  } catch (e) {
    state.buddies = [
      { id: 'Kid1', name: 'Buddy 1 (default)', avatar: 'B', accent: ACCENT_COLORS[0] },
      { id: 'Kid2', name: 'Buddy 2 (default)', avatar: 'B', accent: ACCENT_COLORS[1] },
    ];
  }
  state.buddiesLoaded = true;
  if (state.step === 2) render();
}

async function loadApps() {
  try {
    const json = await call('GetInstalledApps');
    const list = JSON.parse(json);
    state.apps = list.map(a => ({
      key: a.key,
      label: a.label,
      path: a.path,
      isLauncher: a.isLauncher,
      installed: a.installed,
      isCustom: false,
      remoteEnabled: a.installed,
      killRelated: a.installed && a.isLauncher,
    }));
  } catch (e) {
    state.apps = [];
  }
}

async function runInstall() {
  state.installLog = [];
  state.installSuccess = false;
  state.installDone = false;
  const payload = {
    buddyId: state.selectedBuddy,
    apps: state.apps.filter(a => a.installed || a.isCustom).map(a => ({
      key: a.key, label: a.label, path: a.path, isLauncher: a.isLauncher,
      remoteEnabled: a.remoteEnabled, killRelated: a.killRelated,
    })),
  };
  try {
    await call('RunInstall', JSON.stringify(payload));
  } catch (e) {
    state.installLog.push({ text: 'Bridge error: ' + e.message, level: 'err' });
    state.installSuccess = false;
    state.installDone = true;
    state.step = 4;
    render();
  }
}

async function addCustomApp() {
  try {
    const path = await call('PickCustomAppPath');
    if (!path) return;
    const name = path.split('\\').pop().replace(/\.exe$/i, '');
    if (state.apps.some(a => a.key.toLowerCase() === name.toLowerCase())) {
      alert('Already in the list.');
      return;
    }
    state.apps.push({
      key: name, label: name, path: path,
      isLauncher: false, installed: true, isCustom: true,
      remoteEnabled: true, killRelated: false,
    });
    render();
  } catch (e) {
    alert('Failed to add: ' + e.message);
  }
}

function renderInstallLog() {
  const el = document.getElementById('install-log');
  if (!el) return;
  el.innerHTML = state.installLog.map(l => {
    const cls = l.level === 'err' ? 'err' : l.level === 'ok' ? 'ok' : '';
    return `<div class="${cls}">${escapeHtml(l.text)}</div>`;
  }).join('');
  el.scrollTop = el.scrollHeight;
}

function escapeHtml(s) {
  return String(s).replace(/[<>&"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', '"': '&quot;' }[c]));
}

function render() {
  // Step label (5 visible steps: welcome, sign-in, buddies, configure, install/done)
  stepLabel.textContent = `Step ${Math.min(state.step + 1, 5)} of 5`;

  // Admin banner
  adminBanner.style.display = 'block';
  if (state.isAdmin) {
    adminBanner.className = 'admin-banner ok';
    adminBanner.textContent = '✓ Running as administrator';
  } else {
    adminBanner.className = 'admin-banner bad';
    adminBanner.textContent = '✗ Not admin — please re-run as administrator';
  }

  // Footer buttons
  backBtn.style.display = (state.step > 0 && state.step < 4) ? 'inline-block' : 'none';
  if (state.step === 4) {
    nextBtn.style.display = 'none';
  } else if (state.step === 5) {
    nextBtn.style.display = 'inline-block';
    nextBtn.textContent = 'Finish';
    nextBtn.disabled = false;
  } else {
    nextBtn.style.display = 'inline-block';
    if (state.step === 0) {
      nextBtn.disabled = !state.isAdmin;
      nextBtn.textContent = 'Get started →';
    } else if (state.step === 1) {
      nextBtn.disabled = state.signingIn;
      nextBtn.textContent = state.signedIn ? 'Next →' : (state.signingIn ? 'Signing in…' : 'Sign in with Google');
    } else if (state.step === 2) {
      nextBtn.textContent = 'Next →';
      nextBtn.disabled = false;
    } else if (state.step === 3) {
      nextBtn.textContent = 'Install →';
      nextBtn.disabled = false;
    }
  }

  switch (state.step) {
    case 0: renderWelcome(); break;
    case 1: renderSignIn(); break;
    case 2: renderBuddies(); break;
    case 3: renderConfigure(); break;
    case 4: renderInstalling(); break;
    case 5: renderDone(); break;
  }
}

function renderSignIn() {
  const status = state.signedIn
    ? `<div class="welcome-body" style="color:#22c55e;font-weight:600">✓ Signed in as ${escapeHtml(state.managerName || 'manager')}</div>
       <div class="welcome-body" style="opacity:0.7">Tap <strong>Next →</strong> to pick which buddy this PC is for.</div>`
    : `<div class="welcome-body">
         Sign in with your parent Google account so the agent can talk to ChoreBuddy on your behalf.
         A browser window will open — sign in, then come back to this wizard.
       </div>
       ${state.signingIn ? '<div class="welcome-body" style="opacity:0.7">⏳ Waiting for browser sign-in to complete…</div>' : ''}
       ${state.signInError ? `<div class="welcome-body" style="color:#ff5e5e">✗ ${escapeHtml(state.signInError)}</div>` : ''}
       <div class="welcome-body" style="opacity:0.6;font-size:13px">
         Use the same Google account you use to manage ChoreBuddy on your phone.
         The agent stores a refresh token in <code>C:\\ProgramData\\ChoreBuddy\\agent-config.json</code>.
       </div>`;
  main.innerHTML = `
    <div class="title">Sign in to ChoreBuddy</div>
    ${status}
  `;
}

function renderWelcome() {
  main.innerHTML = `
    <div class="title">Set up firewall control on this PC</div>
    <div class="welcome-body">
      This installs the ChoreBuddy Agent as a Windows Service so it can:
      <ul>
        <li>Block apps and games when you send SHUTOFF from your phone</li>
        <li>Show a lockout message on this PC's screen</li>
        <li>Run automatically at startup</li>
        <li>Restart itself if killed</li>
      </ul>
      You'll pick a buddy this PC is for and which apps to control.
    </div>
  `;
}

function renderBuddies() {
  main.innerHTML = `
    <div class="title">Which buddy is this PC for?</div>
    <div class="subtitle">When you send SHUTOFF, only the apps configured for this buddy get blocked.</div>
    <div id="buddy-list">${
      !state.buddiesLoaded
        ? '<div style="color:#7b84a8; text-align:center; padding: 40px;">Loading buddies from ChoreBuddy...</div>'
        : state.buddies.map(b => buddyCardHtml(b)).join('')
    }</div>
  `;

  document.querySelectorAll('.buddy-card-wrap').forEach(el => {
    el.addEventListener('click', () => {
      state.selectedBuddy = el.dataset.id;
      render();
    });
  });
}

function buddyCardHtml(b) {
  const selected = state.selectedBuddy === b.id;
  const letter = (b.name || '?').charAt(0).toUpperCase();
  return `
    <div class="card buddy-card-wrap ${selected ? 'selected' : ''}" data-id="${escapeHtml(b.id)}">
      <div class="buddy-card">
        <div class="buddy-avatar" style="background:${b.accent}">${escapeHtml(letter)}</div>
        <div>
          <div class="buddy-name">${escapeHtml(b.name)}</div>
          <div class="buddy-hint">${selected ? '✓ Selected' : 'Click to pick'}</div>
        </div>
      </div>
    </div>
  `;
}

function renderConfigure() {
  main.innerHTML = `
    <div class="title">Which apps should this PC control?</div>
    <div class="subtitle">Pre-checked apps were found installed. Toggle anything off you don't want controlled. Add custom apps below.</div>
    <div id="app-list"></div>
    <button class="btn-add" id="add-custom-btn">+ Add custom app...</button>
  `;
  renderAppList();
  document.getElementById('add-custom-btn').addEventListener('click', addCustomApp);
}

function renderAppList() {
  const list = document.getElementById('app-list');
  if (!list) return;
  list.innerHTML = state.apps.map((a, i) => appRowHtml(a, i)).join('');
  state.apps.forEach((a, i) => {
    const remoteEl = document.getElementById(`remote-${i}`);
    const killEl = document.getElementById(`kill-${i}`);
    if (remoteEl) remoteEl.addEventListener('change', e => {
      a.remoteEnabled = e.target.checked;
    });
    if (killEl) killEl.addEventListener('change', e => {
      a.killRelated = e.target.checked;
    });
  });
}

function appRowHtml(a, i) {
  const status = a.isCustom ? 'custom' : (a.installed ? 'installed' : 'notinstalled');
  const statusText = a.isCustom ? 'custom' : (a.installed ? '✓ installed' : 'not installed');
  const disabled = (!a.installed && !a.isCustom) ? 'disabled' : '';
  return `
    <div class="app-row">
      <div class="app-main">
        <div class="app-name ${disabled}">${escapeHtml(a.label)}</div>
        <span class="app-status ${status}">${statusText}</span>
        <label class="toggle ${disabled}">
          <input type="checkbox" id="remote-${i}" ${a.remoteEnabled ? 'checked' : ''} ${disabled ? 'disabled' : ''} />
          <span class="slider"></span>
        </label>
      </div>
      ${a.isLauncher ? `
        <div class="app-sub">
          <div class="app-sub-label">Also block all games in this library</div>
          <label class="toggle ${disabled}">
            <input type="checkbox" id="kill-${i}" ${a.killRelated ? 'checked' : ''} ${disabled ? 'disabled' : ''} />
            <span class="slider"></span>
          </label>
        </div>
      ` : ''}
    </div>
  `;
}

function renderInstalling() {
  main.innerHTML = `
    <div class="title">Installing...</div>
    <div class="install-status" id="install-status">Starting...</div>
    <div class="install-log" id="install-log"></div>
  `;
  renderInstallLog();
}

function renderDone() {
  const buddy = state.buddies.find(b => b.id === state.selectedBuddy);
  if (state.installSuccess) {
    main.innerHTML = `
      <div class="done-icon">✓</div>
      <div class="done-h">All set!</div>
      <div class="done-body">
        This PC is now paired to <strong>${escapeHtml(buddy?.name || state.selectedBuddy)}</strong>.
        <ul>
          <li>The agent runs as a Windows Service (auto-starts at boot)</li>
          <li>A watchdog restarts it if killed</li>
          <li>The lockout overlay starts when you log in</li>
        </ul>
        Open ChoreBuddy manager → ☰ menu → 🚫 Firewall Debug to control this PC remotely.
      </div>
    `;
  } else {
    main.innerHTML = `
      <div class="done-icon" style="color:#ff5e5e">✗</div>
      <div class="done-h" style="color:#ff5e5e">Install failed</div>
      <div class="done-body">Check the log above for details. You can close and re-run the wizard after fixing.</div>
    `;
  }
}

init();
