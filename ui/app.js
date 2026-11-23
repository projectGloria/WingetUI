(function () {
  const state = {
    activeTab: "presets",
    activeCategory: "all",
    filterText: "",
    presets: { categories: [] },
    installedMap: new Map(), // lowerCaseId -> { name, id, version, available, source }
    metaMap: new Map(),      // lowerCaseId -> { releaseDate, releaseNotesUrl, homepage }
    installedList: [],
    updatesList: [],
    searchResults: [],
    selectedIds: new Set(),
    taskQueue: [],
    currentTask: null,
    taskProgress: null,      // { phase, percent (-1 = indeterminate), detail } for currentTask
    checkingUpdates: false,
    isScanning: false,
    wingetOnlyFilter: true,
    trustedOnly: true,      // search: hide community (unverified-publisher) packages
    searchPending: false,   // true until the host has scored every search result
    searchSeq: 0
  };

  const KNOWN_ICONS = {
    "7zip.7zip": "https://github.com/ip7z.png?size=128",
    "yt-dlp.yt-dlp": "https://github.com/yt-dlp.png?size=128",
    "yt-dlp.ffmpeg": "https://ffmpeg.org/favicon.ico",
    "gyan.ffmpeg": "https://ffmpeg.org/favicon.ico",
    "9nksqgp7f2nh": "https://upload.wikimedia.org/wikipedia/commons/6/6b/WhatsApp.svg",
    "whatsapp.whatsapp": "https://upload.wikimedia.org/wikipedia/commons/6/6b/WhatsApp.svg"
  };

  const KNOWN_DOMAINS = {
    "brave.brave": "brave.com",
    "mozilla.firefox": "firefox.com",
    "valve.steam": "store.steampowered.com",
    "google.playgames": "play.google.com",
    "faceitltd.faceitac": "faceit.com",
    "xp8clzl93f5z4p": "nvidia.com",
    "logitech.ghub": "logitechg.com",
    "discord.discord": "discord.com",
    "tunnelbear.tunnelbear": "tunnelbear.com",
    "7zip.7zip": "7-zip.org",
    "rarlab.winrar": "win-rar.com",
    "gyan.ffmpeg": "ffmpeg.org",
    "yt-dlp.ffmpeg": "ffmpeg.org",
    "openjs.nodejs": "nodejs.org",
    "openjs.nodejs.lts": "nodejs.org",
    "python.python.3.14": "python.org",
    "microsoft.visualstudiocode": "code.visualstudio.com",
    "notepad++.notepad++": "notepad-plus-plus.org",
    "microsoft.windowsterminal": "learn.microsoft.com",
    "git.git": "git-scm.com",
    "github.githubdesktop": "desktop.github.com",
    "openai.codex": "openai.com",
    "9plm9xgg6vks": "chatgpt.com",
    "openai.chatgpt": "chatgpt.com",
    "9nksqgp7f2nh": "whatsapp.com",
    "whatsapp.whatsapp": "whatsapp.com",
    "9nztwsqntd0s": "telegram.org",
    "9ncbcszsjrsb": "spotify.com",
    "9wzdncrfj3tj": "netflix.com",
    "9p6rc76msmmj": "amazon.com",
    "xp8c7q1nd229ts": "discord.com",
    "xpdm1zw6815mqm": "videolan.org",
    "9pb2mz1zmb1s": "apple.com",
    "9pkmjd4k8npt": "apple.com",
    "9nblggh5l9xt": "instagram.com",
    "9nh2gph4jzdm": "tiktok.com",
    "google.antigravity": "deepmind.google",
    "google.antigravitycli": "deepmind.google",
    "voidtools.everything.beta": "voidtools.com",
    "voidtools.everything": "voidtools.com",
    "microsoft.powertoys": "learn.microsoft.com",
    "hibitsoftware.hibituninstaller": "hibitsoft.ir",
    "microsoft.appinstaller": "microsoft.com",
    "microsoft.edge": "microsoft.com",
    "microsoft.edgewebview2runtime": "microsoft.com",
    "denoland.deno": "deno.com",
    "obsidian.obsidian": "obsidian.md",
    "videolan.vlc": "videolan.org",
    "spotify.spotify": "spotify.com",
    "telegram.telegramdesktop": "telegram.org",
    "obsproject.obsstudio": "obsproject.com",
    "blenderfoundation.blender": "blender.org",
    "gimp.gimp": "gimp.org",
    "audacity.audacity": "audacityteam.org",
    "handbrake.handbrake": "handbrake.fr",
    "postman.postman": "postman.com",
    "docker.dockerdesktop": "docker.com",
    "sublimehq.sublimetext.4": "sublimetext.com",
    "winscp.winscp": "winscp.net",
    "putty.putty": "putty.org",
    "sharex.sharex": "getsharex.com",
    "bitwarden.bitwarden": "bitwarden.com",
    "proton.protonvpn": "protonvpn.com",
    "mullvadvpn.mullvadvpn": "mullvad.net",
    "epicgames.epicgameslauncher": "epicgames.com",
    "gog.galaxy": "gog.com",
    "electronicarts.eadesktop": "ea.com",
    "ubisoft.connect": "ubisoft.com",
    "tailscale.tailscale": "tailscale.com",
    "wiresharkfoundation.wireshark": "wireshark.org",
    "qbittorrent.qbittorrent": "qbittorrent.org",
    "anydesk.anydesk": "anydesk.com",
    "teamviewer.teamviewer": "teamviewer.com",
    "rustdesk.rustdesk": "rustdesk.com"
  };

  const KNOWN_DESCRIPTIONS = {
    "9nksqgp7f2nh": "Simple, reliable, private messaging and calling by Meta",
    "whatsapp.whatsapp": "Simple, reliable, private messaging and calling by Meta",
    "9plm9xgg6vks": "Official OpenAI ChatGPT desktop application for Windows",
    "openai.chatgpt": "Official OpenAI ChatGPT desktop application for Windows",
    "9nztwsqntd0s": "Fast and secure cloud-based mobile and desktop messaging app",
    "telegram.telegramdesktop": "Fast and secure cloud-based mobile and desktop messaging app",
    "9ncbcszsjrsb": "Digital music, podcast, and video streaming service",
    "spotify.spotify": "Digital music, podcast, and video streaming service",
    "9wzdncrfj3tj": "Stream TV shows, movies, and original entertainment",
    "xpdm1zw6815mqm": "Free and open-source portable cross-platform media player",
    "videolan.vlc": "Free and open-source portable cross-platform media player",
    "xp8c7q1nd229ts": "Voice, video, and text communication platform",
    "discord.discord": "Voice, video, and text communication platform"
  };

  function isHttpUrl(url) {
    return typeof url === "string" && /^https?:\/\//i.test(url.trim());
  }

  function resolveIconSources(item) {
    if (!item) return { primary: "", fallback: "", local: "" };
    const idLower = (item.id || "").toLowerCase();
    const installed = state.installedMap.get(idLower);
    const localIcon = item.localIcon || (installed && installed.localIcon) || "";

    if (KNOWN_ICONS[idLower]) {
      return { primary: KNOWN_ICONS[idLower], fallback: "", local: localIcon };
    }

    const meta = state.metaMap.get(idLower);
    if (meta && isHttpUrl(meta.iconUrl)) {
      return {
        primary: meta.iconUrl,
        fallback: isHttpUrl(meta.fallbackIconUrl) ? meta.fallbackIconUrl : "",
        local: localIcon
      };
    }

    let domain = "";
    if (item.domain && item.domain !== "github.com") {
      domain = item.domain;
    } else if (KNOWN_DOMAINS[idLower]) {
      domain = KNOWN_DOMAINS[idLower];
    } else {
      for (const cat of state.presets.categories || []) {
        for (const app of cat.apps || []) {
          if (app.id.toLowerCase() === idLower && app.domain && app.domain !== "github.com") {
            domain = app.domain;
            break;
          }
          if (
            Array.isArray(app.altIds) &&
            app.altIds.some((a) => a.toLowerCase() === idLower) &&
            app.domain &&
            app.domain !== "github.com"
          ) {
            domain = app.domain;
            break;
          }
        }
        if (domain) break;
      }
    }

    if (!domain) {
      if (idLower.startsWith("microsoft.")) domain = "microsoft.com";
      else if (idLower.startsWith("google.")) domain = "google.com";
      else if (idLower.startsWith("mozilla.")) domain = "mozilla.org";
      else if (idLower.startsWith("python.")) domain = "python.org";
      else if (idLower.startsWith("jetbrains.")) domain = "jetbrains.com";
      else if (idLower.startsWith("adobe.")) domain = "adobe.com";
      else if (idLower.startsWith("nvidia.")) domain = "nvidia.com";
      else if (idLower.startsWith("valve.")) domain = "store.steampowered.com";
      else if (idLower.startsWith("brave.")) domain = "brave.com";
      else if (idLower.startsWith("discord.")) domain = "discord.com";
      else if (idLower.startsWith("logitech.")) domain = "logitechg.com";
      else if (idLower.startsWith("openai.")) domain = "openai.com";
    }

    if (!domain && item.name) {
      const normName = item.name.toLowerCase();
      if (normName.includes("whatsapp")) domain = "whatsapp.com";
      else if (normName.includes("spotify")) domain = "spotify.com";
      else if (normName.includes("telegram")) domain = "telegram.org";
      else if (normName.includes("discord")) domain = "discord.com";
      else if (normName.includes("slack")) domain = "slack.com";
      else if (normName.includes("netflix")) domain = "netflix.com";
      else if (normName.includes("vlc") || normName.includes("videolan")) domain = "videolan.org";
      else if (normName.includes("steam")) domain = "store.steampowered.com";
      else if (normName.includes("chatgpt") || normName.includes("openai")) domain = "chatgpt.com";
    }

    if (domain) {
      const favUrl = `https://www.google.com/s2/favicons?sz=128&domain_url=${encodeURIComponent(domain)}`;
      return { primary: favUrl, fallback: "", local: localIcon };
    }

    if (localIcon) {
      return { primary: localIcon, fallback: "", local: "" };
    }

    return { primary: "", fallback: "", local: "" };
  }

  function getInitials(name) {
    if (!name) return "??";
    const cleaned = name.replace(/[^a-zA-Z0-9 ]/g, " ").trim();
    const words = cleaned.split(/\s+/).filter(Boolean);
    if (words.length >= 2) {
      return (words[0][0] + words[1][0]).toUpperCase();
    }
    return (cleaned || name).slice(0, 2).toUpperCase();
  }

  function getHueForKey(key) {
    const str = String(key || "app").toLowerCase();
    let hash = 0;
    for (let i = 0; i < str.length; i++) {
      hash = (hash * 31 + str.charCodeAt(i)) | 0;
    }
    const palette = [235, 262, 198, 160, 32, 342, 215, 285];
    return palette[Math.abs(hash) % palette.length];
  }

  window.onAppIconLoad = function (img) {
    if (!img) return;
    // Google S2 favicons returns HTTP 404 with a 16x16 default globe PNG when a domain has no favicon
    if (img.src.indexOf("google.com/s2/favicons") !== -1 && img.naturalWidth <= 16 && img.naturalHeight <= 16) {
      window.onAppIconError(img);
    }
  };

  window.onAppIconError = function (img) {
    if (!img) return;
    const fb = img.getAttribute("data-fallback");
    if (fb) {
      img.removeAttribute("data-fallback");
      img.src = fb;
      return;
    }
    const loc = img.getAttribute("data-local");
    if (loc) {
      img.removeAttribute("data-local");
      img.src = loc;
      return;
    }
    img.style.display = "none";
    if (img.nextElementSibling) {
      img.nextElementSibling.style.display = "flex";
    }
  };

  function renderAppIcon(item, isSmall = false) {
    const idLower = ((item && item.id) || "").toLowerCase();
    const displayName = (item && (item.name || item.id)) || "";
    const sources = resolveIconSources(item);
    const initials = escapeHtml(getInitials(displayName));
    const hue = getHueForKey(idLower || displayName);
    const sizeClass = isSmall ? "sm" : "";

    if (!sources.primary) {
      return `
        <div class="app-icon-wrap ${sizeClass}" data-pkg-icon="${escapeHtml(idLower)}" data-pkg-name="${escapeHtml(displayName)}" style="--icon-hue:${hue}">
          <span class="app-icon-fallback" style="display:flex">${initials}</span>
        </div>
      `;
    }

    return `
      <div class="app-icon-wrap ${sizeClass}" data-pkg-icon="${escapeHtml(idLower)}" data-pkg-name="${escapeHtml(displayName)}" style="--icon-hue:${hue}">
        <img class="app-icon-img" src="${escapeHtml(sources.primary)}"
             ${sources.fallback ? `data-fallback="${escapeHtml(sources.fallback)}"` : ""}
             ${sources.local ? `data-local="${escapeHtml(sources.local)}"` : ""}
             alt="" loading="lazy"
             onload="window.onAppIconLoad(this)"
             onerror="window.onAppIconError(this)" />
        <span class="app-icon-fallback" style="display:none">${initials}</span>
      </div>
    `;
  }

  // Metadata arrives one package at a time (hundreds in a burst). Coalesce DOM work so a scan costs
  // one icon pass and one re-render per ~150ms instead of one of each per message.
  const pendingIconIds = new Set();
  let iconFlushTimer = null;
  let renderTimer = null;

  function queueIconUpdate(idLower) {
    pendingIconIds.add(idLower);
    if (!iconFlushTimer) {
      iconFlushTimer = setTimeout(() => {
        iconFlushTimer = null;
        const ids = new Set(pendingIconIds);
        pendingIconIds.clear();
        updatePackageIconsInDom(ids);
      }, 150);
    }
  }

  function scheduleRender() {
    if (renderTimer) return;
    renderTimer = setTimeout(() => {
      renderTimer = null;
      renderUpdates();
      renderPresets();
      if (state.searchPending) renderSearch();
    }, 200);
  }

  function updatePackageIconsInDom(idSet) {
    if (!idSet || idSet.size === 0) return;
    document.querySelectorAll(".app-icon-wrap[data-pkg-icon]").forEach((wrap) => {
      const idLower = wrap.getAttribute("data-pkg-icon");
      if (!idSet.has(idLower)) return;
      const name = wrap.getAttribute("data-pkg-name") || idLower;
      const sources = resolveIconSources({ id: idLower, name });
      if (!sources.primary) return;

      const existingImg = wrap.querySelector("img.app-icon-img");
      if (existingImg && existingImg.getAttribute("src") === sources.primary && existingImg.style.display !== "none") {
        return;
      }

      const initials = escapeHtml(getInitials(name));
      wrap.innerHTML = `
        <img class="app-icon-img" src="${escapeHtml(sources.primary)}"
             ${sources.fallback ? `data-fallback="${escapeHtml(sources.fallback)}"` : ""}
             ${sources.local ? `data-local="${escapeHtml(sources.local)}"` : ""}
             alt="" loading="lazy"
             onload="window.onAppIconLoad(this)"
             onerror="window.onAppIconError(this)" />
        <span class="app-icon-fallback" style="display:none">${initials}</span>
      `;
    });
  }

  function formatRelativeDate(dateStr) {
    if (!dateStr) return null;
    const parsed = new Date(dateStr + "T00:00:00");
    if (isNaN(parsed.getTime())) return null;

    const now = new Date();
    const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const diffDays = Math.round((today - parsed) / (1000 * 60 * 60 * 24));

    if (diffDays <= 0) return { label: "Today", fresh: true };
    if (diffDays === 1) return { label: "Yesterday", fresh: true };
    if (diffDays <= 7) return { label: `${diffDays}d ago`, fresh: true };
    if (diffDays < 30) return { label: `${diffDays}d ago`, fresh: false };
    if (diffDays < 365) {
      const months = Math.floor(diffDays / 30);
      return { label: `${months}mo ago`, fresh: false };
    }
    const years = Math.floor(diffDays / 365);
    return { label: `${years}y ago`, fresh: false };
  }

  function renderReleaseDateCell(pkgId) {
    const meta = state.metaMap.get((pkgId || "").toLowerCase());
    if (!meta) {
      return `<span style="color:var(--text-muted);font-size:12px;">Checking date...</span>`;
    }
    if (!meta.releaseDate) {
      return `<span style="color:var(--text-muted);font-size:12px;">Unknown</span>`;
    }

    const rel = formatRelativeDate(meta.releaseDate);
    const relBadge = rel
      ? `<span class="release-ago ${rel.fresh ? "fresh" : ""}">${escapeHtml(rel.label)}</span>`
      : "";
    const notesBtn = meta.releaseNotesUrl
      ? `<span class="release-notes-link" data-open-url="${escapeHtml(meta.releaseNotesUrl)}" title="Open Release Notes">Notes ↗</span>`
      : "";

    return `
      <div class="release-cell">
        <span class="release-date-text">${escapeHtml(meta.releaseDate)}</span>
        ${relBadge}
        ${notesBtn}
      </div>
    `;
  }

  // ---- Startup splash ----
  const SPLASH_MAX_TILES = 60;
  let splashTimer = null;

  function onScanProgress(msg) {
    const splash = document.getElementById("splash");
    if (!splash || splash.classList.contains("hidden")) return;

    if (msg.phase) document.getElementById("splash-phase").textContent = msg.phase + "…";

    const bar = document.getElementById("splash-bar");
    const fill = document.getElementById("splash-bar-fill");
    const count = document.getElementById("splash-count");
    if (msg.total > 0 && msg.done > 0) {
      bar.classList.remove("indeterminate");
      fill.style.width = Math.min(100, Math.round((msg.done / msg.total) * 100)) + "%";
      count.textContent = `${msg.done} / ${msg.total}`;
    } else if (msg.total > 0) {
      count.textContent = `${msg.total} packages found`;
    }

    if (msg.id) {
      // package_metadata for this id was posted just before this message, so state already has its icon
      const item = { id: msg.id, name: msg.name || msg.id };
      document.getElementById("splash-current").classList.add("active");
      document.getElementById("splash-current-icon").innerHTML = renderAppIcon(item, false);
      document.getElementById("splash-current-name").textContent = item.name;
      document.getElementById("splash-current-id").textContent = item.id;

      const tiles = document.getElementById("splash-tiles");
      const tile = document.createElement("div");
      tile.className = "splash-tile";
      tile.title = item.name;
      tile.innerHTML = renderAppIcon(item, true);
      tiles.appendChild(tile);
      while (tiles.childElementCount > SPLASH_MAX_TILES) tiles.removeChild(tiles.firstElementChild);
    }
  }

  function hideSplash() {
    const splash = document.getElementById("splash");
    if (!splash || splash.classList.contains("hidden")) return;
    clearTimeout(splashTimer);
    splash.classList.add("leaving");
    setTimeout(() => {
      splash.classList.add("hidden");
      document.getElementById("splash-tiles").innerHTML = "";
    }, 380);
  }

  function sendHost(payload) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(payload);
    }
  }

  // kind: "info" | "success" | "warn" | "error". Problems stay up longer and can be dismissed.
  function showToast(msg, kind = "info") {
    const container = document.getElementById("toast-container");
    while (container.childElementCount >= 4) container.firstElementChild.remove();
    const el = document.createElement("div");
    el.className = `toast toast-${kind}`;
    el.setAttribute("role", kind === "error" ? "alert" : "status");
    el.textContent = msg;
    el.title = "Click to dismiss";
    el.addEventListener("click", () => el.remove());
    container.appendChild(el);
    setTimeout(() => el.remove(), kind === "error" || kind === "warn" ? 8000 : 3500);
  }

  // The log is a bounded ring so a long "Update All" session can't grow the DOM without limit
  const LOG_MAX_LINES = 1500;
  const logLines = ["WingetUI ready. Select packages above to install, update, or uninstall."];
  let logFlushPending = false;

  function appendLog(line) {
    for (const l of String(line).split("\n")) logLines.push(l);
    if (logLines.length > LOG_MAX_LINES) logLines.splice(0, logLines.length - LOG_MAX_LINES);
    if (logFlushPending) return;
    logFlushPending = true;
    requestAnimationFrame(() => {
      logFlushPending = false;
      const body = document.getElementById("console-body");
      const stick = body.scrollHeight - body.scrollTop - body.clientHeight < 40;
      document.getElementById("console-output").textContent = logLines.join("\n");
      if (stick) body.scrollTop = body.scrollHeight;
    });
  }

  function clearLog() {
    logLines.length = 0;
    logLines.push("Console cleared.");
    document.getElementById("console-output").textContent = "Console cleared.";
  }

  // ---- Confirm dialog (styled; replaces window.confirm) ----
  function showConfirm({ title, message, confirmLabel = "Confirm", danger = false }) {
    return new Promise((resolve) => {
      const modal = document.getElementById("modal-confirm");
      document.getElementById("confirm-title").textContent = title;
      document.getElementById("confirm-message").textContent = message;
      const ok = document.getElementById("btn-confirm-ok");
      const cancel = document.getElementById("btn-confirm-cancel");
      ok.textContent = confirmLabel;
      ok.className = danger ? "btn btn-danger" : "btn btn-primary";
      modal.classList.remove("hidden");

      const finish = (result) => {
        modal.classList.add("hidden");
        ok.removeEventListener("click", onOk);
        cancel.removeEventListener("click", onCancel);
        modal.removeEventListener("click", onBackdrop);
        document.removeEventListener("keydown", onKey, true);
        resolve(result);
      };
      const onOk = () => finish(true);
      const onCancel = () => finish(false);
      const onBackdrop = (e) => { if (e.target === modal) finish(false); };
      const onKey = (e) => {
        if (e.key === "Escape") { e.stopPropagation(); finish(false); }
        else if (e.key === "Enter") { e.preventDefault(); e.stopPropagation(); finish(true); }
      };
      ok.addEventListener("click", onOk);
      cancel.addEventListener("click", onCancel);
      modal.addEventListener("click", onBackdrop);
      document.addEventListener("keydown", onKey, true);
      (danger ? cancel : ok).focus();
    });
  }

  // ---- Task progress presentation ----
  const OP_VERBS = {
    install: { ing: "Installing", done: "installed", noun: "Install" },
    upgrade: { ing: "Updating", done: "updated", noun: "Update" },
    uninstall: { ing: "Uninstalling", done: "uninstalled", noun: "Uninstall" }
  };

  const PHASE_LABELS = {
    prepare: "Preparing",
    download: "Downloading",
    verify: "Verifying installer",
    install: "Installing",
    uninstall: "Uninstalling",
    finish: "Finishing",
    confirm: "Confirming"
  };

  function phaseText(task, prog) {
    if (!task) return "";
    const p = prog || { phase: "prepare", percent: -1, detail: "" };
    let label = PHASE_LABELS[p.phase] || (OP_VERBS[task.operation] || OP_VERBS.install).ing;
    if (p.phase === "prepare") label = "Starting";
    if (p.phase === "download" && p.percent >= 0) return `${label} ${p.percent}%`;
    return label + "…";
  }

  // Lowercased ids -> what the task queue is doing with any of them
  function taskStateFor(ids) {
    const set = new Set((ids || []).filter(Boolean).map((i) => i.toLowerCase()));
    if (state.currentTask && set.has(state.currentTask.packageId.toLowerCase())) {
      return { kind: "running", task: state.currentTask };
    }
    const q = state.taskQueue.find((t) => set.has(t.packageId.toLowerCase()));
    return q ? { kind: "queued", task: q } : null;
  }

  function renderTaskInline(st) {
    if (!st) return "";
    if (st.kind === "queued") {
      const verb = (OP_VERBS[st.task.operation] || OP_VERBS.install).noun;
      return `
        <div class="task-inline queued">
          <span class="status-pill status-busy">${escapeHtml(verb)} queued</span>
          <button class="btn-icon btn-dequeue" data-dequeue="${escapeHtml(st.task.taskId)}" title="Remove from queue" aria-label="Remove from queue">&times;</button>
        </div>`;
    }
    const prog = state.taskProgress;
    const pct = prog && prog.percent >= 0 ? prog.percent : -1;
    return `
      <div class="task-inline running" data-task-inline>
        <div class="mini-progress ${pct < 0 ? "indeterminate" : ""}" role="progressbar" aria-valuemin="0" aria-valuemax="100" ${pct >= 0 ? `aria-valuenow="${pct}"` : ""}>
          <div class="mini-progress-fill" style="width:${pct < 0 ? 35 : pct}%"></div>
        </div>
        <span class="task-inline-label">${escapeHtml(phaseText(st.task, prog))}</span>
      </div>`;
  }

  // Called on every task_progress message: patch the DOM in place instead of re-rendering tables
  function updateTaskProgressUi() {
    const task = state.currentTask;
    const prog = state.taskProgress;
    const pct = prog && prog.percent >= 0 ? prog.percent : -1;
    const text = phaseText(task, prog);

    document.querySelectorAll("[data-task-inline]").forEach((el) => {
      const bar = el.querySelector(".mini-progress");
      const fill = el.querySelector(".mini-progress-fill");
      bar.classList.toggle("indeterminate", pct < 0);
      if (pct >= 0) bar.setAttribute("aria-valuenow", pct);
      else bar.removeAttribute("aria-valuenow");
      fill.style.width = (pct < 0 ? 35 : pct) + "%";
      el.querySelector(".task-inline-label").textContent = text;
    });

    const wrap = document.getElementById("task-progress");
    if (!task) {
      wrap.classList.add("hidden");
      return;
    }
    wrap.classList.remove("hidden");
    const bar = document.getElementById("task-progress-bar");
    bar.classList.toggle("indeterminate", pct < 0);
    document.getElementById("task-progress-fill").style.width = (pct < 0 ? 35 : pct) + "%";
    const detail = prog && prog.detail ? ` · ${prog.detail}` : "";
    document.getElementById("task-progress-text").textContent = text + detail;
  }

  // winget / installer return codes (doc/windows/package-manager/winget/returnCodes.md)
  const WINGET_ERRORS = {
    "8A150005": "The operation was cancelled.",
    "8A150008": "Downloading the installer failed. Check your internet connection.",
    "8A150010": "No installer in this package fits this PC (architecture, scope or OS version).",
    "8A150011": "The downloaded installer's hash doesn't match the manifest. It was not run.",
    "8A150014": "Package not found.",
    "8A150016": "Multiple packages match this ID.",
    "8A150019": "This operation needs administrator rights.",
    "8A15002B": "No applicable update was found.",
    "8A15002D": "The installer failed the security check.",
    "8A15002E": "The download was incomplete.",
    "8A15002F": "This app has no uninstall command registered.",
    "8A150030": "The app's uninstaller failed.",
    "8A150041": "The package agreements were not accepted.",
    "8A150049": "The MSI installer failed.",
    "8A15004F": "The available version isn't newer than the installed one.",
    "8A150050": "The installed version is unknown, so winget won't upgrade it automatically.",
    "8A150056": "This installer can't run from an administrator context.",
    "8A150061": "The package is already installed.",
    "8A150068": "The package is pinned, which prevents upgrades.",
    "8A15006D": "A required service is busy. Try again later.",
    "8A15007D": "This user-scope package can't be changed from an administrator context.",
    "8A150086": "The download was empty. Check your internet connection.",
    "8A15008E": "The update uses a different installer technology. Uninstall and reinstall instead.",
    "8A150101": "The app is currently running. Close it and try again.",
    "8A150102": "Another installation is in progress. Try again later.",
    "8A150103": "Files are in use. Close the app and try again.",
    "8A150104": "A dependency is missing from your system.",
    "8A150105": "Not enough disk space.",
    "8A150106": "Not enough memory to install.",
    "8A150107": "This installer needs an internet connection.",
    "8A150108": "The installer hit an error. Contact the publisher's support.",
    "8A150109": "Restart your PC to finish installation.",
    "8A15010A": "Installation failed. Restart your PC and try again.",
    "8A15010B": "Your PC will restart to finish installation.",
    "8A15010C": "The installation was cancelled in the installer.",
    "8A15010D": "Another version of this app is already installed.",
    "8A15010E": "A newer version of this app is already installed.",
    "8A15010F": "Your organization's policies block this installation.",
    "8A150110": "Failed to install the package's dependencies.",
    "8A150111": "The app is in use by another application.",
    "8A150113": "This package isn't supported on this system.",
    "8A150114": "This installer can't upgrade an existing installation. Uninstall it first."
  };

  function describeExitCode(code) {
    if (typeof code !== "number" || code === 0) return "";
    const hex = (code >>> 0).toString(16).toUpperCase();
    return WINGET_ERRORS[hex] || `winget exited with code 0x${hex}.`;
  }

  function findInstalledInfo(app) {
    if (!app || !app.id) return null;
    const primary = state.installedMap.get(app.id.toLowerCase());
    if (primary) return primary;
    if (Array.isArray(app.altIds)) {
      for (const alt of app.altIds) {
        const match = state.installedMap.get(alt.toLowerCase());
        if (match) return match;
      }
    }
    return null;
  }

  // Queue Management
  function enqueueTask(operation, packageId, packageName) {
    if (!packageId) return;
    // One pending operation per package: a second click (or install+uninstall) would race the first
    if (taskStateFor([packageId])) {
      showToast(`${packageName || packageId} already has a task queued`);
      return;
    }

    const task = {
      taskId: "t_" + Date.now() + "_" + Math.random().toString(36).slice(2, 6),
      operation,
      packageId,
      packageName: packageName || packageId
    };
    state.taskQueue.push(task);
    state.batchTotal = (state.currentTask || state.taskQueue.length > 1) ? (state.batchTotal || 1) + 1 : 1;
    updateQueueUi();
    renderAll();

    if (!state.currentTask) {
      runNextTask();
    }
  }

  function removeQueuedTask(taskId) {
    const before = state.taskQueue.length;
    state.taskQueue = state.taskQueue.filter((t) => t.taskId !== taskId);
    if (state.taskQueue.length !== before) {
      state.batchTotal = Math.max(1, (state.batchTotal || 1) - 1);
      updateQueueUi();
      renderAll();
    }
  }

  function runNextTask() {
    if (state.taskQueue.length === 0) {
      state.currentTask = null;
      state.taskProgress = null;
      state.batchDone = 0;
      state.batchTotal = 0;
      updateQueueUi();
      renderAll();
      // Full refresh once the batch is over (per-task results were already applied from task_done)
      sendHost({ action: "scan_installed" });
      return;
    }

    state.currentTask = state.taskQueue.shift();
    state.taskProgress = { phase: "prepare", percent: -1, detail: "" };
    updateQueueUi();
    renderAll();

    const t = state.currentTask;
    const installed = state.installedMap.get(t.packageId.toLowerCase());
    const silent = document.getElementById("toggle-silent").checked;
    sendHost({
      action: "run_winget_task",
      taskId: t.taskId,
      operation: t.operation,
      packageId: t.packageId,
      packageName: t.packageName,
      fromVersion: installed ? installed.version || "" : "",
      silent
    });
  }

  function updateQueueUi() {
    const dot = document.getElementById("task-dot");
    const summary = document.getElementById("task-summary");
    const pill = document.getElementById("queue-pill");
    const cancelBtn = document.getElementById("btn-cancel-task");
    const clearBtn = document.getElementById("btn-clear-queue");

    if (state.currentTask) {
      const t = state.currentTask;
      dot.className = "status-dot busy";
      const verb = (OP_VERBS[t.operation] || OP_VERBS.install).ing;
      const step = state.batchTotal > 1 ? ` (${(state.batchDone || 0) + 1} of ${state.batchTotal})` : "";
      summary.textContent = `${verb} ${t.packageName}${step}`;
      summary.title = t.packageId;
      cancelBtn.classList.remove("hidden");
    } else {
      dot.className = "status-dot done";
      summary.textContent = "Idle — Ready";
      summary.title = "";
      cancelBtn.classList.add("hidden");
    }

    if (state.taskQueue.length > 0) {
      pill.textContent = `${state.taskQueue.length} queued`;
      pill.classList.remove("hidden");
      clearBtn.classList.remove("hidden");
    } else {
      pill.classList.add("hidden");
      clearBtn.classList.add("hidden");
      document.getElementById("queue-popover").classList.add("hidden");
    }
    renderQueuePopover();
    updateTaskProgressUi();
  }

  function renderQueuePopover() {
    const pop = document.getElementById("queue-popover");
    if (pop.classList.contains("hidden")) return;
    pop.innerHTML = `
      <div class="queue-popover-title">Up next</div>
      ${state.taskQueue
        .map(
          (t, i) => `
        <div class="queue-row">
          <span class="queue-index">${i + 1}</span>
          <span class="queue-op queue-op-${escapeHtml(t.operation)}">${escapeHtml((OP_VERBS[t.operation] || OP_VERBS.install).noun)}</span>
          <span class="queue-name" title="${escapeHtml(t.packageId)}">${escapeHtml(t.packageName)}</span>
          <button class="btn-icon btn-dequeue" data-dequeue="${escapeHtml(t.taskId)}" title="Remove from queue" aria-label="Remove from queue">&times;</button>
        </div>`
        )
        .join("")}
    `;
  }

  // Applies a finished task to local state right away, so every view (presets, tables, search,
  // details) reflects the result without waiting for the full rescan at the end of the queue.
  function applyTaskResult(msg) {
    const idLower = (msg.packageId || "").toLowerCase();
    if (!idLower || !msg.success) return;
    if (msg.verify === "confirmed" || msg.verify === "pending") {
      if (msg.installed && msg.installed.id) {
        const row = msg.installed;
        const prev = state.installedMap.get(idLower);
        if (prev && prev.localIcon && !row.localIcon) row.localIcon = prev.localIcon;
        state.installedMap.delete(idLower);
        state.installedMap.set(row.id.toLowerCase(), row);
        const i = state.installedList.findIndex((p) => p.id.toLowerCase() === idLower);
        if (i >= 0) state.installedList[i] = row;
        else state.installedList.push(row);
      } else if (!msg.installed && (msg.operation === "uninstall" || msg.verify === "confirmed")) {
        state.installedMap.delete(idLower);
        state.installedList = state.installedList.filter((p) => p.id.toLowerCase() !== idLower);
      }
    }
    if (msg.operation === "upgrade" && msg.verify === "confirmed") {
      state.updatesList = state.updatesList.filter((u) => (u.id || "").toLowerCase() !== idLower);
    }
    if (msg.operation === "uninstall" && msg.verify === "confirmed") {
      state.updatesList = state.updatesList.filter((u) => (u.id || "").toLowerCase() !== idLower);
    }
  }

  function reportTaskResult(msg) {
    const name = msg.packageName || msg.packageId;
    const verbs = OP_VERBS[msg.operation] || OP_VERBS.install;
    let log, toast, kind;

    if (!msg.success) {
      const reason = describeExitCode(msg.exitCode);
      const detail = msg.message && msg.message !== reason ? ` (${msg.message})` : "";
      log = `⚠ ${verbs.noun} of ${name} failed: ${reason || msg.message || "unknown error"}${reason ? detail : ""}`;
      toast = `${verbs.noun} failed: ${name} — ${reason || msg.message || "see Terminal for details"}`;
      kind = "error";
    } else if (msg.skipped) {
      log = `• ${name}: nothing to do (${describeExitCode(msg.exitCode).replace(/\.$/, "").toLowerCase()}).`;
      toast = msg.operation === "upgrade" ? `${name} is already up to date` : `${name} is already installed`;
      kind = "info";
    } else if (msg.verify === "pending") {
      log =
        msg.operation === "uninstall"
          ? `⚠ winget reported ${name} as uninstalled, but it is still detected. The uninstaller may still be running, may need you to finish it, or a restart may be required.`
          : msg.operation === "install"
          ? `⚠ winget reported ${name} as installed, but it isn't detected yet. Store apps can take a while to finish in the background.`
          : `⚠ winget reported ${name} as updated, but an update is still listed. The app may need to be closed or the PC restarted.`;
      toast = `${name}: ${msg.operation} not confirmed yet — see Terminal`;
      kind = "warn";
    } else {
      log = `✓ ${name} ${verbs.done}${msg.verify === "confirmed" ? " (confirmed)" : ""}.`;
      toast = `${name} ${verbs.done}`;
      kind = "success";
    }

    if (msg.success && msg.rebootRequired) {
      log += " A restart is required to finish.";
      toast += " — restart required";
      kind = "warn";
    }
    appendLog(log);
    showToast(toast, kind);
  }

  // Rendering: Category Pills
  function renderCategoryPills() {
    const wrap = document.getElementById("category-pills");
    const cats = state.presets.categories || [];
    let html = `<button class="cat-pill ${state.activeCategory === "all" ? "active" : ""}" data-cat="all">All Categories</button>`;
    for (const c of cats) {
      const isAct = state.activeCategory === c.id ? "active" : "";
      html += `<button class="cat-pill ${isAct}" data-cat="${escapeHtml(c.id)}">${escapeHtml(c.name)}</button>`;
    }
    wrap.innerHTML = html;

    wrap.querySelectorAll(".cat-pill").forEach((btn) => {
      btn.addEventListener("click", () => {
        state.activeCategory = btn.getAttribute("data-cat");
        renderCategoryPills();
        renderPresets();
      });
    });
  }

  // Rendering: Presets View
  function renderPresets() {
    const container = document.getElementById("presets-container");
    const cats = state.presets.categories || [];
    const q = state.filterText.trim().toLowerCase();

    let totalPresets = 0;
    let installedPresets = 0;
    let html = "";

    for (const cat of cats) {
      for (const app of cat.apps || []) {
        totalPresets++;
        if (findInstalledInfo(app)) installedPresets++;
      }

      if (state.activeCategory !== "all" && state.activeCategory !== cat.id) continue;

      const filteredApps = (cat.apps || []).filter((app) => {
        if (!q) return true;
        return (
          app.name.toLowerCase().includes(q) ||
          app.id.toLowerCase().includes(q) ||
          (app.description && app.description.toLowerCase().includes(q)) ||
          cat.name.toLowerCase().includes(q)
        );
      });

      if (filteredApps.length === 0) continue;

      const catInstalledCount = filteredApps.filter((a) => !!findInstalledInfo(a)).length;

      html += `
        <div class="category-block">
          <div class="category-header">
            <div class="category-title-wrap">
              <span class="category-title">${escapeHtml(cat.name)}</span>
              <span class="category-count">(${catInstalledCount}/${filteredApps.length} installed)</span>
            </div>
            <button class="btn-chip btn-cat-toggle" data-cat-id="${escapeHtml(cat.id)}">Select Category</button>
          </div>
          <div class="cards-grid">
            ${filteredApps.map((app) => renderPresetCard(app, cat.id)).join("")}
          </div>
        </div>
      `;
    }

    if (!html) {
      html = `<div class="empty-state">No preset apps match your current filter.</div>`;
    }

    container.innerHTML = html;
    document.getElementById("badge-presets").textContent = `${installedPresets}/${totalPresets}`;
    updateBatchBanner();

    // Wire card click-to-select
    container.querySelectorAll(".app-card").forEach((card) => {
      card.addEventListener("click", () => {
        const id = card.getAttribute("data-card-id");
        if (!id) return;
        if (state.selectedIds.has(id)) state.selectedIds.delete(id);
        else state.selectedIds.add(id);
        renderPresets();
      });
    });

    container.querySelectorAll(".btn-cat-toggle").forEach((btn) => {
      btn.addEventListener("click", () => {
        const catId = btn.getAttribute("data-cat-id");
        const cat = (state.presets.categories || []).find((c) => c.id === catId);
        if (!cat) return;
        const allSelected = cat.apps.every((a) => state.selectedIds.has(a.id));
        cat.apps.forEach((a) => {
          if (allSelected) state.selectedIds.delete(a.id);
          else state.selectedIds.add(a.id);
        });
        renderPresets();
      });
    });

    container.querySelectorAll("[data-action]").forEach((btn) => {
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        const act = btn.getAttribute("data-action");
        const id = btn.getAttribute("data-id");
        const name = btn.getAttribute("data-name");
        const catId = btn.getAttribute("data-cat");

        if (act === "remove-preset") {
          removeAppFromPresets(catId, id);
        } else {
          requestTask(act, id, name);
        }
      });
    });
    bindDequeueButtons(container);
  }

  function bindDequeueButtons(container) {
    container.querySelectorAll("[data-dequeue]").forEach((btn) => {
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        removeQueuedTask(btn.getAttribute("data-dequeue"));
      });
    });
  }

  // Entry point for user-initiated single tasks: uninstalls are destructive, so ask first
  async function requestTask(operation, id, name) {
    if (operation === "uninstall") {
      const ok = await showConfirm({
        title: `Uninstall ${name || id}?`,
        message: `This runs the app's uninstaller${document.getElementById("toggle-silent").checked ? " silently" : ""}. Its data may be removed too.`,
        confirmLabel: "Uninstall",
        danger: true
      });
      if (!ok) return;
    }
    enqueueTask(operation, id, name);
  }

  function renderPresetCard(app, catId) {
    const info = findInstalledInfo(app);
    const isSelected = state.selectedIds.has(app.id);
    const candidateIds = new Set([app.id.toLowerCase()]);
    if (info && info.id) candidateIds.add(info.id.toLowerCase());
    if (Array.isArray(app.altIds)) {
      app.altIds.forEach((a) => candidateIds.add(a.toLowerCase()));
    }
    const taskState = taskStateFor(Array.from(candidateIds));

    let statusHtml = "";
    let actionBtns = "";

    if (taskState) {
      statusHtml = renderTaskInline(taskState);
    } else if (info && info.available) {
      statusHtml = ""; // the Update button already says it; version details live in the Updates tab
      actionBtns = `
        <button class="btn btn-amber btn-sm" data-action="upgrade" data-id="${escapeHtml(info.id)}" data-name="${escapeHtml(app.name)}">Update</button>
        <button class="btn btn-ghost btn-sm" data-action="uninstall" data-id="${escapeHtml(info.id)}" data-name="${escapeHtml(app.name)}">Uninstall</button>
      `;
    } else if (info) {
      statusHtml = `<span class="status-pill status-installed">✓ Installed (${escapeHtml(info.version)})</span>`;
      actionBtns = `
        <button class="btn btn-ghost btn-sm" data-action="uninstall" data-id="${escapeHtml(info.id)}" data-name="${escapeHtml(app.name)}">Uninstall</button>
      `;
    } else {
      statusHtml = `<span class="status-pill status-missing">Not Installed</span>`;
      actionBtns = `
        <button class="btn btn-primary btn-sm" data-action="install" data-id="${escapeHtml(app.id)}" data-name="${escapeHtml(app.name)}">Install</button>
      `;
    }

    const displayDesc = app.description || KNOWN_DESCRIPTIONS[app.id.toLowerCase()] || "";

    return `
      <div class="app-card ${isSelected ? "selected" : ""}" data-card-id="${escapeHtml(app.id)}">
        <div class="card-top">
          ${renderAppIcon(app, false)}
          <div class="card-info">
            <div class="card-title-row">
              <span class="card-name">${escapeHtml(app.name)}</span>
              ${app.badge ? `<span class="card-rec-badge">${escapeHtml(app.badge)}</span>` : ""}
            </div>
            <div class="pkg-id" data-copy="${escapeHtml(app.id)}" title="Click to copy Winget ID">${escapeHtml(app.id)}</div>
            ${displayDesc ? `<div class="card-desc">${escapeHtml(displayDesc)}</div>` : ""}
          </div>
          <button class="btn-delete-preset" data-action="remove-preset" data-cat="${escapeHtml(catId)}" data-id="${escapeHtml(app.id)}" title="Remove from presets">&times;</button>
        </div>
        <div class="card-bottom">
          ${statusHtml}
          <div class="card-actions">
            ${actionBtns}
          </div>
        </div>
      </div>
    `;
  }

  function updateBatchBanner() {
    const banner = document.getElementById("batch-banner");
    const countEl = document.getElementById("batch-count");
    const count = state.selectedIds.size;
    if (count > 0) {
      countEl.textContent = count;
      banner.classList.remove("hidden");
    } else {
      banner.classList.add("hidden");
    }
  }

  // Rendering: Updates View
  function renderUpdates() {
    const wrap = document.getElementById("updates-table-wrap");
    const q = state.filterText.trim().toLowerCase();

    const map = new Map();
    for (const pkg of state.installedList) {
      if (pkg.available && pkg.available.trim() !== "") {
        map.set(pkg.id.toLowerCase(), pkg);
      }
    }
    for (const u of state.updatesList) {
      if (u.id) map.set(u.id.toLowerCase(), u);
    }

    const allUpdates = Array.from(map.values());
    document.getElementById("badge-updates").textContent = allUpdates.length;

    const filtered = allUpdates.filter((u) => {
      if (!q) return true;
      return u.name.toLowerCase().includes(q) || u.id.toLowerCase().includes(q);
    });

    if (filtered.length === 0) {
      wrap.innerHTML = `<div class="empty-state">${
        state.checkingUpdates
          ? `<div class="spinner"></div>Checking every installed package for updates…`
          : state.isScanning
          ? "Scanning installed packages for available updates..."
          : "All packages are up to date! Click 'Deep Scan Updates' to also check apps with unknown versions."
      }</div>`;
      return;
    }

    wrap.innerHTML = `
      <table class="pkg-table">
        <thead>
          <tr>
            <th>Package Name</th>
            <th>Winget ID</th>
            <th>Installed</th>
            <th>Available</th>
            <th>Released</th>
            <th style="text-align:right">Action</th>
          </tr>
        </thead>
        <tbody>
          ${filtered
            .map(
              (u) => `
            <tr class="clickable-row" data-show-details="${escapeHtml(u.id)}" data-pkg-name="${escapeHtml(u.name)}" title="Click row to inspect package details & authenticity">
              <td>
                <div class="pkg-name-cell">
                  ${renderAppIcon(u, true)}
                  <strong>${escapeHtml(u.name)}</strong>
                </div>
              </td>
              <td><span class="pkg-id" data-copy="${escapeHtml(u.id)}">${escapeHtml(u.id)}</span></td>
              <td><code>${escapeHtml(u.version)}</code></td>
              <td><span class="status-pill status-update">${escapeHtml(u.available || "Newer")}</span></td>
              <td>${renderReleaseDateCell(u.id)}</td>
              <td style="text-align:right">
                <div class="table-actions">
                  ${renderRowActions(u.id, `<button class="btn btn-amber btn-sm" data-table-op="upgrade" data-id="${escapeHtml(u.id)}" data-name="${escapeHtml(u.name)}">Update</button>`)}
                </div>
              </td>
            </tr>
          `
            )
            .join("")}
        </tbody>
      </table>
    `;

    bindTableActions(wrap);
  }

  // Rendering: Installed View
  function renderInstalled() {
    const wrap = document.getElementById("installed-table-wrap");
    const q = state.filterText.trim().toLowerCase();

    const baseList = state.installedList.filter((p) => {
      if (state.wingetOnlyFilter) {
        return p.source === "winget" || p.source === "msstore";
      }
      return true;
    });

    document.getElementById("badge-installed").textContent = baseList.length;

    const filtered = baseList.filter((p) => {
      if (!q) return true;
      return p.name.toLowerCase().includes(q) || p.id.toLowerCase().includes(q);
    });

    if (filtered.length === 0) {
      wrap.innerHTML = `<div class="empty-state">${
        state.isScanning ? "Scanning installed packages..." : "No matching installed packages found."
      }</div>`;
      return;
    }

    wrap.innerHTML = `
      <table class="pkg-table">
        <thead>
          <tr>
            <th>Package Name</th>
            <th>Package ID</th>
            <th>Version</th>
            <th>Source</th>
            <th style="text-align:right">Actions</th>
          </tr>
        </thead>
        <tbody>
          ${filtered
            .map(
              (p) => `
            <tr class="clickable-row" data-show-details="${escapeHtml(p.id)}" data-pkg-name="${escapeHtml(p.name)}" data-pkg-source="${escapeHtml(p.source || "winget")}" title="Click row to inspect package details & authenticity">
              <td>
                <div class="pkg-name-cell">
                  ${renderAppIcon(p, true)}
                  <strong>${escapeHtml(p.name)}</strong>
                </div>
              </td>
              <td><span class="pkg-id" data-copy="${escapeHtml(p.id)}">${escapeHtml(p.id)}</span></td>
              <td>
                <code>${escapeHtml(p.version)}</code>
                ${
                  p.available
                    ? `<span class="status-pill status-update" style="margin-left:6px">→ ${escapeHtml(p.available)}</span>`
                    : ""
                }
              </td>
              <td><span class="status-pill status-missing">${escapeHtml(p.source || "system")}</span></td>
              <td style="text-align:right">
                <div class="table-actions">
                  <button class="btn btn-ghost btn-sm" data-pin-id="${escapeHtml(p.id)}" data-pin-name="${escapeHtml(p.name)}" title="Save to My Presets">+ Preset</button>
                  ${renderRowActions(
                    p.id,
                    `${
                      p.available
                        ? `<button class="btn btn-amber btn-sm" data-table-op="upgrade" data-id="${escapeHtml(p.id)}" data-name="${escapeHtml(p.name)}">Update</button>`
                        : ""
                    }
                    <button class="btn btn-danger-outline btn-sm" data-table-op="uninstall" data-id="${escapeHtml(p.id)}" data-name="${escapeHtml(p.name)}">Uninstall</button>`
                  )}
                </div>
              </td>
            </tr>
          `
            )
            .join("")}
        </tbody>
      </table>
    `;

    bindTableActions(wrap);
  }

  // App Details Popup Modal
  let activeDetailId = null;
  let activeDetailName = null;

  function openAppDetailsModal(id, fallbackName, source) {
    if (!id) return;
    activeDetailId = id;
    const modal = document.getElementById("modal-app-details");
    if (!modal) return;

    modal.classList.remove("hidden");

    // Reset to loading state
    const loading = document.getElementById("detail-loading");
    const content = document.getElementById("detail-content");
    loading.classList.remove("hidden");
    content.classList.add("hidden");

    // Pre-fill header
    document.getElementById("detail-app-name").textContent = fallbackName || id;
    const pkgIdEl = document.getElementById("detail-pkg-id");
    pkgIdEl.textContent = id;
    pkgIdEl.setAttribute("data-copy", id);

    const isMsStore = (source || "").toLowerCase() === "msstore";
    document.getElementById("detail-version").textContent = isMsStore ? "Store Edition" : "...";

    const badge = document.getElementById("detail-verified-badge");
    const knownMeta = state.metaMap.get(id.toLowerCase());
    if (isMsStore) {
      badge.className = "badge-source badge-msstore";
      badge.textContent = "✓ Microsoft Store Certified";
    } else if (knownMeta && knownMeta.trusted) {
      badge.className = "badge-source badge-winget";
      badge.textContent = "✓ Verified publisher";
    } else if (knownMeta && knownMeta.trusted === false) {
      badge.className = "badge-source";
      badge.textContent = "Community";
    } else {
      badge.className = "badge-source";
      badge.textContent = "Verifying…";
    }

    // Set initial icon
    const iconWrap = document.getElementById("detail-icon-wrap");
    iconWrap.innerHTML = renderAppIcon({ id: id, name: fallbackName, domain: "" }, false);

    activeDetailName = fallbackName || id;
    refreshDetailModalState();

    const addPresetBtn = document.getElementById("btn-detail-add-preset");
    addPresetBtn.onclick = () => {
      const descEl = document.getElementById("detail-description");
      let d = descEl ? descEl.textContent.trim() : "";
      if (d === "No description provided." || d === "No description provided in package manifest.") d = "";
      if (d.length > 120) {
        const dotIdx = d.indexOf(". ");
        if (dotIdx > 20 && dotIdx < 130) d = d.substring(0, dotIdx + 1);
        else d = d.substring(0, 117) + "...";
      }
      closeAppDetailsModal();
      openAddPresetModal(fallbackName || id, id, d);
    };

    // Request full details from host
    sendHost({
      action: "get_package_details",
      id: id,
      name: fallbackName,
      source: source
    });
  }

  // Install status + primary action of the open details modal, recomputed from live state
  function refreshDetailModalState() {
    const id = activeDetailId;
    if (!id) return;
    const name = activeDetailName || id;
    const instPill = document.getElementById("detail-install-status");
    const actionBtn = document.getElementById("btn-detail-action");
    const uninstallBtn = document.getElementById("btn-detail-uninstall");
    const info = state.installedMap.get(id.toLowerCase());
    const busy = taskStateFor([id]);

    actionBtn.setAttribute("data-id", id);
    actionBtn.setAttribute("data-name", name);
    uninstallBtn.setAttribute("data-id", id);
    uninstallBtn.setAttribute("data-name", name);
    actionBtn.disabled = !!busy;
    uninstallBtn.disabled = !!busy;
    uninstallBtn.classList.toggle("hidden", !(info && info.available));

    if (busy) {
      instPill.className = "status-pill status-busy";
      instPill.textContent =
        busy.kind === "queued"
          ? `${(OP_VERBS[busy.task.operation] || OP_VERBS.install).noun} queued`
          : phaseText(busy.task, state.taskProgress);
    } else if (info) {
      instPill.className = "status-pill status-installed";
      instPill.textContent = `Installed (${info.version || "Current"})`;
    } else {
      instPill.className = "status-pill status-missing";
      instPill.textContent = "Not Installed";
    }

    if (info && info.available) {
      actionBtn.className = "btn btn-amber";
      actionBtn.textContent = `Update to ${info.available}`;
      actionBtn.setAttribute("data-table-op", "upgrade");
    } else if (info) {
      actionBtn.className = "btn btn-danger-outline";
      actionBtn.textContent = "Uninstall";
      actionBtn.setAttribute("data-table-op", "uninstall");
    } else {
      actionBtn.className = "btn btn-primary";
      actionBtn.textContent = "Install";
      actionBtn.setAttribute("data-table-op", "install");
    }
  }

  function closeAppDetailsModal() {
    activeDetailId = null;
    activeDetailName = null;
    const modal = document.getElementById("modal-app-details");
    if (modal) modal.classList.add("hidden");
  }

  // Rendering: Search View
  function renderSearch() {
    const wrap = document.getElementById("search-table-wrap");
    // While a new query runs, keep its "Searching…" placeholder instead of repainting old results
    if (state.searchInFlight || !state.searchResults || state.searchResults.length === 0) {
      return;
    }

    // true = verified publisher / Store-certified, false = community, null = not scored yet
    const trustOf = (r) => {
      if ((r.source || "").toLowerCase() === "msstore") return true;
      const m = state.metaMap.get((r.id || "").toLowerCase());
      return m && typeof m.trusted === "boolean" ? m.trusted : null;
    };

    let rows = state.searchResults;
    let hiddenCount = 0;
    if (!state.searchPending) {
      // Verified first (stable sort keeps winget's relevance order inside each group)
      rows = rows
        .map((r, i) => ({ r, i, t: trustOf(r) === true ? 0 : 1 }))
        .sort((a, b) => a.t - b.t || a.i - b.i)
        .map((x) => x.r);
      if (state.trustedOnly) {
        const kept = rows.filter((r) => trustOf(r) === true);
        hiddenCount = rows.length - kept.length;
        rows = kept;
      }
    }

    const note = document.getElementById("search-trust-note");
    if (note) {
      note.textContent = state.searchPending
        ? "Verifying publishers…"
        : hiddenCount > 0
        ? `${hiddenCount} community package${hiddenCount === 1 ? "" : "s"} hidden`
        : "";
    }

    if (rows.length === 0) {
      wrap.innerHTML = `<div class="empty-state">No verified-publisher packages found. Untick “Verified publishers only” to see ${hiddenCount} community result${hiddenCount === 1 ? "" : "s"}.</div>`;
      return;
    }

    wrap.innerHTML = `
      <table class="pkg-table">
        <thead>
          <tr>
            <th>Package Name</th>
            <th>Winget ID</th>
            <th>Version</th>
            <th>Verification / Source</th>
            <th style="text-align:right">Actions</th>
          </tr>
        </thead>
        <tbody>
          ${rows
            .map((r) => {
              const isInst = state.installedMap.has(r.id.toLowerCase());
              const srcLower = (r.source || "winget").toLowerCase();
              const isMsStore = srcLower === "msstore";
              const trust = trustOf(r);
              const isOfficialWinget = trust === true && !isMsStore;

              let sourceBadge;
              if (isMsStore) {
                sourceBadge = `<span class="badge-source badge-msstore" title="Microsoft Store Certified application">✓ Microsoft Store</span>`;
              } else if (trust === true) {
                sourceBadge = `<span class="badge-source badge-winget" title="Publisher matches the organization behind the package's website / download source">✓ Verified publisher</span>`;
              } else if (trust === false) {
                sourceBadge = `<span class="badge-source badge-community" title="Community-submitted to winget-pkgs; publisher could not be verified">Community</span>`;
              } else {
                sourceBadge = `<span class="badge-source badge-community">Verifying…</span>`;
              }

              let versionDisplay = "";
              if (!r.version || r.version === "Unknown") {
                if (isMsStore) {
                  versionDisplay = `<span class="badge-source badge-msstore" style="font-size:10.5px;padding:2px 7px;">Store App</span>`;
                } else {
                  versionDisplay = `<span style="color:var(--text-muted);font-size:12px;">Latest</span>`;
                }
              } else {
                versionDisplay = `<code>${escapeHtml(r.version)}</code>`;
              }

              return `
                <tr class="clickable-row" data-show-details="${escapeHtml(r.id)}" data-pkg-name="${escapeHtml(r.name)}" data-pkg-source="${escapeHtml(r.source || "winget")}" title="Click row to inspect package details & authenticity">
                  <td>
                    <div class="pkg-name-cell">
                      ${renderAppIcon(r, true)}
                      <div>
                        <strong style="margin-right:6px;">${escapeHtml(r.name)}</strong>
                        ${isMsStore ? `<span class="badge-source badge-msstore" style="font-size:9.5px;padding:1px 5px;">Store</span>` : (isOfficialWinget ? `<span class="badge-source badge-winget" style="font-size:9.5px;padding:1px 5px;">Verified</span>` : "")}
                      </div>
                    </div>
                  </td>
                  <td><span class="pkg-id" data-copy="${escapeHtml(r.id)}" title="Click to copy ID">${escapeHtml(r.id)}</span></td>
                  <td>${versionDisplay}</td>
                  <td>${sourceBadge}</td>
                  <td style="text-align:right">
                    <div class="table-actions">
                      <button class="btn btn-ghost btn-sm btn-inspect" data-inspect-id="${escapeHtml(r.id)}" data-inspect-name="${escapeHtml(r.name)}" data-inspect-source="${escapeHtml(r.source || "winget")}" title="Inspect full package authenticity & details">
                        Details
                      </button>
                      <button class="btn btn-ghost btn-sm" data-pin-id="${escapeHtml(r.id)}" data-pin-name="${escapeHtml(r.name)}">+ Presets</button>
                      ${renderRowActions(
                        r.id,
                        isInst
                          ? `<span class="status-pill status-installed">✓ Installed</span>`
                          : `<button class="btn btn-primary btn-sm" data-table-op="install" data-id="${escapeHtml(r.id)}" data-name="${escapeHtml(r.name)}">Install</button>`
                      )}
                    </div>
                  </td>
                </tr>
              `;
            })
            .join("")}
        </tbody>
      </table>
    `;

    bindTableActions(wrap);
  }

  function bindTableActions(container) {
    container.querySelectorAll(".clickable-row, [data-show-details]").forEach((row) => {
      row.addEventListener("click", (e) => {
        if (e.target.closest("button, a, input, select, .pkg-id, [data-open-url]")) return;
        const id = row.getAttribute("data-show-details");
        const name = row.getAttribute("data-pkg-name") || id;
        const source = row.getAttribute("data-pkg-source") || "winget";
        openAppDetailsModal(id, name, source);
      });
    });

    container.querySelectorAll(".btn-inspect").forEach((btn) => {
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        const id = btn.getAttribute("data-inspect-id");
        const name = btn.getAttribute("data-inspect-name") || id;
        const source = btn.getAttribute("data-inspect-source") || "winget";
        openAppDetailsModal(id, name, source);
      });
    });

    container.querySelectorAll("[data-table-op]").forEach((btn) => {
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        const op = btn.getAttribute("data-table-op");
        const id = btn.getAttribute("data-id");
        const name = btn.getAttribute("data-name");
        requestTask(op, id, name);
      });
    });
    bindDequeueButtons(container);

    container.querySelectorAll("[data-pin-id]").forEach((btn) => {
      btn.addEventListener("click", (e) => {
        e.stopPropagation();
        openAddPresetModal(btn.getAttribute("data-pin-name"), btn.getAttribute("data-pin-id"));
      });
    });
  }

  function setButtonBusy(btnId, busy, busyLabel) {
    const btn = document.getElementById(btnId);
    if (!btn) return;
    const label = btn.querySelector("span");
    if (busy) {
      if (label && !btn.dataset.idleLabel) btn.dataset.idleLabel = label.textContent;
      if (label && busyLabel) label.textContent = busyLabel;
      btn.disabled = true;
      btn.classList.add("is-busy");
    } else {
      if (label && btn.dataset.idleLabel) label.textContent = btn.dataset.idleLabel;
      delete btn.dataset.idleLabel;
      btn.disabled = false;
      btn.classList.remove("is-busy");
    }
  }

  async function requestClose() {
    if (state.currentTask) {
      const ok = await showConfirm({
        title: "A task is still running",
        message: `Closing now aborts ${(OP_VERBS[state.currentTask.operation] || OP_VERBS.install).ing.toLowerCase()} ${state.currentTask.packageName}, which can leave it partially installed or removed${state.taskQueue.length ? `, and drops ${state.taskQueue.length} queued task${state.taskQueue.length === 1 ? "" : "s"}` : ""}.`,
        confirmLabel: "Close anyway",
        danger: true
      });
      if (!ok) return;
    }
    sendHost({ action: "window_close", force: true });
  }

  // Table action cell: the normal buttons, or the queued/progress state while a task owns the package
  function renderRowActions(id, idleHtml) {
    const st = taskStateFor([id]);
    return st ? renderTaskInline(st) : idleHtml;
  }

  // Every view that shows install state must be refreshed here; the Search tab and the details
  // modal were previously left showing stale "Installed" state after an uninstall.
  function renderAll() {
    renderPresets();
    renderUpdates();
    renderInstalled();
    renderSearch();
    refreshDetailModalState();
  }

  // Presets editing & Markdown export
  function removeAppFromPresets(catId, appId) {
    const cat = (state.presets.categories || []).find((c) => c.id === catId);
    if (!cat) return;
    cat.apps = cat.apps.filter((a) => a.id !== appId);
    state.selectedIds.delete(appId);
    sendHost({ action: "save_presets", data: state.presets });
    renderPresets();
    showToast(`Removed ${appId} from presets`);
  }

  function openAddPresetModal(defaultName = "", defaultId = "", defaultDesc = "") {
    const select = document.getElementById("preset-app-category");
    select.innerHTML = (state.presets.categories || [])
      .map((c) => `<option value="${escapeHtml(c.id)}">${escapeHtml(c.name)}</option>`)
      .join("");

    if (!defaultDesc && defaultId) {
      defaultDesc = KNOWN_DESCRIPTIONS[defaultId.toLowerCase()] || "";
    }

    document.getElementById("preset-app-name").value = defaultName;
    document.getElementById("preset-app-id").value = defaultId;
    document.getElementById("preset-app-desc").value = defaultDesc;
    document.getElementById("modal-add-preset").classList.remove("hidden");
  }

  function buildWingetTxtMarkdown() {
    const lines = [];
    for (const cat of state.presets.categories || []) {
      if (!cat.apps || cat.apps.length === 0) continue;
      lines.push(`**${cat.name}**`);
      for (const app of cat.apps) {
        lines.push(`- ${app.name}: \`winget install -e --id '${app.id}'\``);
      }
      lines.push("");
    }
    return lines.join("\r\n").trim() + "\r\n";
  }

  function copyToClipboard(text, toastMessage) {
    if (!text) return;
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard
        .writeText(text)
        .then(() => showToast(toastMessage || `Copied: ${text}`))
        .catch(() => showToast("Failed to copy to clipboard"));
    } else {
      showToast("Clipboard unavailable");
    }
  }

  function escapeHtml(str) {
    if (str == null) return "";
    return String(str)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // Event Listeners Setup
  document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll(".nav-item").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll(".nav-item").forEach((b) => b.classList.remove("active"));
        btn.classList.add("active");
        const tab = btn.getAttribute("data-tab");
        state.activeTab = tab;
        document.querySelectorAll(".tab-view").forEach((v) => v.classList.remove("active"));
        document.getElementById("view-" + tab).classList.add("active");
      });
    });

    const filterInput = document.getElementById("global-filter");
    const clearBtn = document.getElementById("btn-clear-filter");
    filterInput.addEventListener("input", () => {
      state.filterText = filterInput.value;
      clearBtn.classList.toggle("hidden", !state.filterText);
      renderAll();
    });
    clearBtn.addEventListener("click", () => {
      filterInput.value = "";
      state.filterText = "";
      clearBtn.classList.add("hidden");
      renderAll();
    });

    document.getElementById("btn-select-missing").addEventListener("click", () => {
      state.selectedIds.clear();
      for (const cat of state.presets.categories || []) {
        if (state.activeCategory !== "all" && state.activeCategory !== cat.id) continue;
        for (const app of cat.apps || []) {
          if (!findInstalledInfo(app)) state.selectedIds.add(app.id);
        }
      }
      renderPresets();
    });

    document.getElementById("btn-select-updates").addEventListener("click", () => {
      state.selectedIds.clear();
      for (const cat of state.presets.categories || []) {
        if (state.activeCategory !== "all" && state.activeCategory !== cat.id) continue;
        for (const app of cat.apps || []) {
          const info = findInstalledInfo(app);
          if (info && info.available) state.selectedIds.add(app.id);
        }
      }
      renderPresets();
    });

    document.getElementById("btn-select-all").addEventListener("click", () => {
      for (const cat of state.presets.categories || []) {
        if (state.activeCategory !== "all" && state.activeCategory !== cat.id) continue;
        for (const app of cat.apps || []) {
          state.selectedIds.add(app.id);
        }
      }
      renderPresets();
    });

    document.getElementById("btn-select-none").addEventListener("click", () => {
      state.selectedIds.clear();
      renderPresets();
    });

    function findPresetAppById(id) {
      for (const c of state.presets.categories || []) {
        for (const a of c.apps || []) {
          if (a.id === id) return a;
        }
      }
      return { id, name: id };
    }

    document.getElementById("btn-batch-install").addEventListener("click", () => {
      for (const id of state.selectedIds) {
        const app = findPresetAppById(id);
        enqueueTask("install", app.id, app.name);
      }
      state.selectedIds.clear();
      renderPresets();
    });

    document.getElementById("btn-batch-update").addEventListener("click", () => {
      for (const id of state.selectedIds) {
        const app = findPresetAppById(id);
        const info = findInstalledInfo(app);
        enqueueTask("upgrade", info ? info.id : app.id, app.name);
      }
      state.selectedIds.clear();
      renderPresets();
    });

    document.getElementById("btn-batch-uninstall").addEventListener("click", async () => {
      // Only apps that are actually installed can be uninstalled
      const targets = [];
      for (const id of state.selectedIds) {
        const app = findPresetAppById(id);
        const info = findInstalledInfo(app);
        if (info) targets.push({ id: info.id, name: app.name });
      }
      if (targets.length === 0) {
        showToast("None of the selected apps are installed.");
        return;
      }
      const names = targets.slice(0, 6).map((t) => t.name).join(", ") + (targets.length > 6 ? `, +${targets.length - 6} more` : "");
      const ok = await showConfirm({
        title: `Uninstall ${targets.length} app${targets.length === 1 ? "" : "s"}?`,
        message: `${names}. Each app's uninstaller will run in turn.`,
        confirmLabel: "Uninstall",
        danger: true
      });
      if (!ok) return;
      targets.forEach((t) => {
        if (!taskStateFor([t.id])) enqueueTask("uninstall", t.id, t.name);
      });
      state.selectedIds.clear();
      renderPresets();
    });

    document.getElementById("btn-check-updates").addEventListener("click", () => {
      if (state.checkingUpdates) return;
      state.checkingUpdates = true;
      setButtonBusy("btn-check-updates", true, "Scanning…");
      renderUpdates();
      sendHost({ action: "check_updates" });
    });

    document.getElementById("btn-update-all").addEventListener("click", () => {
      const map = new Map();
      for (const pkg of state.installedList) {
        if (pkg.available) map.set(pkg.id.toLowerCase(), pkg);
      }
      for (const u of state.updatesList) {
        if (u.id) map.set(u.id.toLowerCase(), u);
      }
      const list = Array.from(map.values()).filter((u) => !taskStateFor([u.id]));
      if (list.length === 0) {
        showToast("No updates available to install.");
        return;
      }
      list.forEach((u) => enqueueTask("upgrade", u.id, u.name));
    });

    document.getElementById("filter-winget-only").addEventListener("change", (e) => {
      state.wingetOnlyFilter = e.target.checked;
      renderInstalled();
    });

    document.getElementById("filter-trusted-only").addEventListener("change", (e) => {
      state.trustedOnly = e.target.checked;
      renderSearch();
    });

    document.getElementById("form-repo-search").addEventListener("submit", (e) => {
      e.preventDefault();
      const q = document.getElementById("input-repo-search").value.trim();
      if (!q) return;
      state.searchSeq++;
      state.searchPending = true;
      state.searchInFlight = true;
      const note = document.getElementById("search-trust-note");
      if (note) note.textContent = "";
      document.getElementById("search-table-wrap").innerHTML = `<div class="empty-state">Searching Winget catalog for "${escapeHtml(q)}"...</div>`;
      sendHost({ action: "search_winget", query: q, seq: state.searchSeq });
    });

    document.getElementById("btn-refresh").addEventListener("click", () => {
      sendHost({ action: "scan_installed" });
    });

    document.getElementById("btn-export-txt").addEventListener("click", () => {
      const md = buildWingetTxtMarkdown();
      sendHost({ action: "export_winget_txt", markdown: md });
    });

    // Window Dragging & Caption Controls
    const topbar = document.querySelector(".topbar");
    const brand = document.querySelector(".brand");
    const splashEl = document.getElementById("splash");

    [topbar, brand, splashEl].forEach((el) => {
      if (!el) return;
      el.addEventListener("mousedown", (e) => {
        if (e.target.closest("button, input, a, select, textarea, .clear-input, .win-btn")) return;
        if (e.button === 0) {
          sendHost({ action: "window_drag" });
        }
      });
      el.addEventListener("dblclick", (e) => {
        if (e.target.closest("button, input, a, select, textarea, .clear-input, .win-btn")) return;
        sendHost({ action: "window_maximize" });
      });
    });

    document.getElementById("win-btn-minimize")?.addEventListener("click", () => {
      sendHost({ action: "window_minimize" });
    });

    document.getElementById("win-btn-maximize")?.addEventListener("click", () => {
      sendHost({ action: "window_maximize" });
    });

    document.getElementById("win-btn-close")?.addEventListener("click", requestClose);

    document.getElementById("btn-add-preset").addEventListener("click", () => openAddPresetModal());
    document.getElementById("btn-close-modal").addEventListener("click", () => {
      document.getElementById("modal-add-preset").classList.add("hidden");
    });
    document.getElementById("btn-cancel-modal").addEventListener("click", () => {
      document.getElementById("modal-add-preset").classList.add("hidden");
    });

    document.getElementById("form-add-preset").addEventListener("submit", (e) => {
      e.preventDefault();
      const name = document.getElementById("preset-app-name").value.trim();
      const id = document.getElementById("preset-app-id").value.trim();
      const catId = document.getElementById("preset-app-category").value;
      let description = document.getElementById("preset-app-desc").value.trim();
      if (!name || !id) return;

      if (!description && id) {
        description = KNOWN_DESCRIPTIONS[id.toLowerCase()] || "";
      }

      const existingCat = (state.presets.categories || []).find((c) =>
        (c.apps || []).some((a) => a.id.toLowerCase() === id.toLowerCase())
      );
      if (existingCat) {
        showToast(`${name} is already in ${existingCat.name}`);
      } else {
        const cat = (state.presets.categories || []).find((c) => c.id === catId);
        if (cat) {
          cat.apps.push({ name, id, description });
          sendHost({ action: "save_presets", data: state.presets });
          renderPresets();
          showToast(`Added ${name} to ${cat.name}`);
        }
      }
      document.getElementById("modal-add-preset").classList.add("hidden");
    });

    function toggleConsoleDrawer() {
      const body = document.getElementById("console-body");
      const isHidden = body.classList.toggle("hidden");
      document.getElementById("console-toggle-label").textContent = isHidden ? "Terminal ▲" : "Terminal ▼";
    }

    document.getElementById("console-bar").addEventListener("click", (e) => {
      if (e.target.closest("button")) return;
      toggleConsoleDrawer();
    });

    document.getElementById("btn-toggle-console").addEventListener("click", (e) => {
      e.stopPropagation();
      toggleConsoleDrawer();
    });

    document.getElementById("btn-clear-log").addEventListener("click", (e) => {
      e.stopPropagation();
      clearLog();
    });

    // Cancel = stop the running task only; the queue carries on (use "Clear queue" to drop the rest)
    document.getElementById("btn-cancel-task").addEventListener("click", async (e) => {
      e.stopPropagation();
      const t = state.currentTask;
      if (!t) return;
      const ok = await showConfirm({
        title: `Cancel ${(OP_VERBS[t.operation] || OP_VERBS.install).ing.toLowerCase()} ${t.packageName}?`,
        message: "Stopping an installer midway can leave the app partially installed or removed.",
        confirmLabel: "Stop task",
        danger: true
      });
      if (!ok || state.currentTask !== t) return;
      document.getElementById("btn-cancel-task").disabled = true;
      document.getElementById("task-progress-text").textContent = "Cancelling…";
      sendHost({ action: "cancel_task", taskId: t.taskId });
    });

    document.getElementById("btn-clear-queue").addEventListener("click", (e) => {
      e.stopPropagation();
      const n = state.taskQueue.length;
      state.taskQueue = [];
      state.batchTotal = state.currentTask ? (state.batchDone || 0) + 1 : 0;
      updateQueueUi();
      renderAll();
      if (n) showToast(`Removed ${n} queued task${n === 1 ? "" : "s"}`);
    });

    document.getElementById("queue-pill").addEventListener("click", (e) => {
      e.stopPropagation();
      const pop = document.getElementById("queue-popover");
      pop.classList.toggle("hidden");
      renderQueuePopover();
    });

    document.getElementById("queue-popover").addEventListener("click", (e) => {
      e.stopPropagation();
      const btn = e.target.closest("[data-dequeue]");
      if (btn) removeQueuedTask(btn.getAttribute("data-dequeue"));
    });

    document.addEventListener("click", (e) => {
      if (!e.target.closest("#queue-popover, #queue-pill")) {
        document.getElementById("queue-popover").classList.add("hidden");
      }
    });

    // Details Modal Event Listeners
    document.getElementById("btn-close-details")?.addEventListener("click", closeAppDetailsModal);
    document.getElementById("btn-detail-close")?.addEventListener("click", closeAppDetailsModal);
    document.getElementById("modal-app-details")?.addEventListener("click", (e) => {
      if (e.target.id === "modal-app-details") closeAppDetailsModal();
    });

    // The modal stays open so the user can watch the task's progress in its status pill
    document.getElementById("btn-detail-action")?.addEventListener("click", () => {
      const btn = document.getElementById("btn-detail-action");
      const op = btn.getAttribute("data-table-op");
      const id = btn.getAttribute("data-id");
      const name = btn.getAttribute("data-name");
      if (op && id) requestTask(op, id, name);
    });

    document.getElementById("btn-detail-uninstall")?.addEventListener("click", () => {
      const btn = document.getElementById("btn-detail-uninstall");
      const id = btn.getAttribute("data-id");
      if (id) requestTask("uninstall", id, btn.getAttribute("data-name"));
    });

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape") {
        closeAppDetailsModal();
        document.getElementById("modal-add-preset")?.classList.add("hidden");
        document.getElementById("queue-popover").classList.add("hidden");
      }
    });

    // Global handler for external URLs and click-to-copy across all modals and tables
    document.addEventListener("click", (e) => {
      const openUrlEl = e.target.closest("[data-open-url]");
      if (openUrlEl) {
        e.preventDefault();
        e.stopPropagation();
        const u = openUrlEl.getAttribute("data-open-url");
        if (u) sendHost({ action: "open_url", url: u });
        return;
      }

      const copyEl = e.target.closest("[data-copy]");
      if (copyEl && !e.target.closest("button, a, input, select")) {
        e.stopPropagation();
        const val = copyEl.getAttribute("data-copy");
        if (val) {
          copyToClipboard(val, `Copied: ${val}`);
        }
      }
    });

    // Listen for messages from C# Host
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener("message", (event) => {
        const msg = event.data;
        if (!msg || !msg.type) return;

        switch (msg.type) {
          case "presets_loaded":
            state.presets = msg.data || { categories: [] };
            if (msg.appVersion) document.getElementById("app-version").textContent = `v${msg.appVersion}`;
            renderCategoryPills();
            renderPresets();
            break;

          case "scan_status":
            state.isScanning = true;
            document.getElementById("scan-status-text").textContent = "Scanning installed apps...";
            document.querySelector(".icon-spin-target").classList.add("spinning");
            break;

          case "installed_list":
            state.isScanning = false;
            document.querySelector(".icon-spin-target").classList.remove("spinning");
            state.installedList = msg.packages || [];
            state.installedMap.clear();
            for (const p of state.installedList) {
              if (p.id) state.installedMap.set(p.id.toLowerCase(), p);
            }
            // Keep Deep Scan results only while they still describe what's installed: drop packages
            // that were removed or whose installed version changed (i.e. were updated)
            state.updatesList = state.updatesList.filter((u) => {
              const cur = u.id && state.installedMap.get(u.id.toLowerCase());
              return cur && (cur.version || "") === (u.version || "");
            });
            document.getElementById("scan-status-text").textContent = `Last scan: ${msg.timestamp}`;
            renderAll();
            break;

          case "updates_list":
            state.updatesList = msg.updates || [];
            state.checkingUpdates = false;
            setButtonBusy("btn-check-updates", false);
            renderUpdates();
            showToast(
              state.updatesList.length
                ? `Found ${state.updatesList.length} update${state.updatesList.length === 1 ? "" : "s"}`
                : "Everything is up to date",
              "success"
            );
            break;

          case "package_metadata":
            if (msg.id) {
              const idLower = msg.id.toLowerCase();
              const prev = state.metaMap.get(idLower) || {};
              state.metaMap.set(idLower, {
                releaseDate: msg.releaseDate || prev.releaseDate || "",
                releaseNotesUrl: msg.releaseNotesUrl || prev.releaseNotesUrl || "",
                homepage: msg.homepage || prev.homepage || "",
                iconUrl: msg.iconUrl || prev.iconUrl || "",
                fallbackIconUrl: msg.fallbackIconUrl || prev.fallbackIconUrl || "",
                trusted: typeof msg.trusted === "boolean" ? msg.trusted : prev.trusted
              });
              queueIconUpdate(idLower);
              if ((msg.releaseDate && msg.releaseDate !== prev.releaseDate) ||
                  (state.searchPending && msg.trusted !== prev.trusted)) {
                scheduleRender();
              }
            }
            break;

          case "scan_progress":
            onScanProgress(msg);
            break;

          case "scan_complete":
            state.isScanning = false;
            document.querySelector(".icon-spin-target").classList.remove("spinning");
            if (msg.error) showToast(`Scan problem: ${msg.error}`, "error");
            renderAll();
            hideSplash();
            break;

          case "search_results":
            if (typeof msg.seq === "number" && msg.seq !== state.searchSeq) break;
            state.searchInFlight = false;
            state.searchResults = msg.results || [];
            state.searchPending = state.searchResults.length > 0;
            if (state.searchResults.length === 0) {
              const note = document.getElementById("search-trust-note");
              if (note) note.textContent = "";
              document.getElementById("search-table-wrap").innerHTML = `<div class="empty-state">No packages found matching "${escapeHtml(msg.query)}".</div>`;
            } else {
              renderSearch();
            }
            break;

          case "search_complete":
            if (typeof msg.seq === "number" && msg.seq !== state.searchSeq) break;
            state.searchPending = false;
            renderSearch();
            break;

          case "package_details":
            if (activeDetailId && activeDetailId.toLowerCase() === (msg.id || "").toLowerCase()) {
              document.getElementById("detail-loading").classList.add("hidden");
              document.getElementById("detail-content").classList.remove("hidden");

              const src = (msg.source || "").toLowerCase();

              if (msg.name) document.getElementById("detail-app-name").textContent = msg.name;
              if (msg.version && msg.version !== "Unknown") {
                document.getElementById("detail-version").textContent = `v${msg.version}`;
              } else if (src === "msstore") {
                document.getElementById("detail-version").textContent = "Microsoft Store Edition";
              } else {
                document.getElementById("detail-version").textContent = "Latest";
              }

              // Trust banner
              const trustBanner = document.getElementById("detail-trust-banner");
              const trustTitle = document.getElementById("trust-title");
              const trustDesc = document.getElementById("trust-desc");
              const verifiedBadge = document.getElementById("detail-verified-badge");

              if (src === "msstore") {
                trustBanner.className = "trust-banner msstore";
                trustTitle.textContent = "✓ Official Microsoft Store Package";
                trustDesc.textContent = "Digitally signed, certified, and distributed directly through official Microsoft Store infrastructure.";
                verifiedBadge.className = "badge-source badge-msstore";
                verifiedBadge.textContent = "✓ Microsoft Store Certified";
              } else if (msg.trusted) {
                trustBanner.className = "trust-banner";
                trustTitle.textContent = "✓ Verified Publisher";
                trustDesc.textContent = "The publisher in this package's ID matches the organization behind its website or download source. Listed in winget-pkgs with automated scans and a SHA-256 installer hash.";
                verifiedBadge.className = "badge-source badge-winget";
                verifiedBadge.textContent = "✓ Verified publisher";
              } else {
                trustBanner.className = "trust-banner community";
                trustTitle.textContent = "Community Package — publisher not verified";
                trustDesc.textContent = "Submitted to the community winget-pkgs repository. It passes automated scans, but the publisher could not be matched to the package's website or download source. Check the links below before installing.";
                verifiedBadge.className = "badge-source";
                verifiedBadge.textContent = "Community";
              }
              // Keep the table in sync with what the modal just learned
              const prevMeta = state.metaMap.get(msg.id.toLowerCase()) || {};
              state.metaMap.set(msg.id.toLowerCase(), Object.assign({}, prevMeta, { trusted: src === "msstore" ? true : !!msg.trusted }));

              // Details
              document.getElementById("detail-description").textContent =
                msg.description || "No description provided in package manifest.";
              document.getElementById("detail-publisher").textContent = msg.publisher || "—";
              document.getElementById("detail-author").textContent = msg.author || msg.publisher || "—";
              document.getElementById("detail-source").textContent =
                src === "msstore" ? "Microsoft Store (msstore)" : "Official Winget (winget-pkgs)";
              document.getElementById("detail-release-date").textContent = msg.releaseDate || "—";
              document.getElementById("detail-license").textContent = msg.license || "—";
              document.getElementById("detail-installer-type").textContent = msg.installerType ? msg.installerType.toUpperCase() : (src === "msstore" ? "MSIX / Store Package" : "EXE / Native");

              // Homepage
              const rowHp = document.getElementById("row-homepage");
              const linkHp = document.getElementById("link-homepage");
              if (msg.homepage) {
                rowHp.classList.remove("hidden");
                linkHp.textContent = msg.homepage;
                linkHp.setAttribute("data-open-url", msg.homepage);
              } else {
                rowHp.classList.add("hidden");
              }

              // Publisher URL
              const rowPub = document.getElementById("row-publisher-url");
              const linkPub = document.getElementById("link-publisher");
              if (msg.publisherUrl) {
                rowPub.classList.remove("hidden");
                linkPub.textContent = msg.publisherUrl;
                linkPub.setAttribute("data-open-url", msg.publisherUrl);
              } else {
                rowPub.classList.add("hidden");
              }

              // Privacy Policy URL
              const rowPriv = document.getElementById("row-privacy-url");
              const linkPriv = document.getElementById("link-privacy");
              if (rowPriv && linkPriv) {
                if (msg.privacyUrl) {
                  rowPriv.classList.remove("hidden");
                  linkPriv.textContent = msg.privacyUrl;
                  linkPriv.setAttribute("data-open-url", msg.privacyUrl);
                } else {
                  rowPriv.classList.add("hidden");
                }
              }

              // Release Notes
              const rowNotes = document.getElementById("row-release-notes");
              const linkNotes = document.getElementById("link-release-notes");
              if (msg.releaseNotesUrl) {
                rowNotes.classList.remove("hidden");
                linkNotes.textContent = msg.releaseNotesUrl;
                linkNotes.setAttribute("data-open-url", msg.releaseNotesUrl);
              } else {
                rowNotes.classList.add("hidden");
              }

              // Download Host & Hash
              document.getElementById("detail-download-host").textContent = msg.downloadHost || (msg.installerUrl ? "Direct Download" : "—");
              const hashEl = document.getElementById("detail-sha256");
              hashEl.textContent = msg.installerSha256 || "Verified by Winget";
              if (msg.installerSha256) {
                hashEl.setAttribute("data-copy", msg.installerSha256);
              }

              // Icon
              const iconWrap = document.getElementById("detail-icon-wrap");
              const detailIconUrl = isHttpUrl(msg.iconUrl) ? msg.iconUrl : "";
              const detailFallbackUrl = isHttpUrl(msg.fallbackIconUrl) ? msg.fallbackIconUrl : "";
              if (detailIconUrl) {
                const installedInfo = state.installedMap.get(msg.id.toLowerCase());
                const localIcon = (installedInfo && installedInfo.localIcon) || "";
                const initials = escapeHtml(getInitials(msg.name || msg.id));
                iconWrap.innerHTML = `
                  <img class="app-icon-img" src="${escapeHtml(detailIconUrl)}"
                       ${detailFallbackUrl ? `data-fallback="${escapeHtml(detailFallbackUrl)}"` : ""}
                       ${localIcon ? `data-local="${escapeHtml(localIcon)}"` : ""}
                       alt=""
                       onload="window.onAppIconLoad(this)"
                       onerror="window.onAppIconError(this)" />
                  <span class="app-icon-fallback" style="display:none">${initials}</span>
                `;
              } else {
                iconWrap.innerHTML = renderAppIcon({ id: msg.id, name: msg.name, domain: msg.homepage || msg.publisherUrl || "" }, false);
              }
            }
            break;

          case "task_started":
            appendLog(`\n▶ ${msg.command}`);
            break;

          case "task_log":
            appendLog(`  ${msg.line}`);
            break;

          case "task_progress":
            if (!state.currentTask || msg.taskId !== state.currentTask.taskId) break;
            state.taskProgress = { phase: msg.phase, percent: msg.percent, detail: msg.detail || "" };
            updateTaskProgressUi();
            if (activeDetailId) refreshDetailModalState();
            break;

          case "task_done":
            if (state.currentTask && msg.taskId !== state.currentTask.taskId) break; // stale
            applyTaskResult(msg);
            reportTaskResult(msg);
            state.batchDone = (state.batchDone || 0) + 1;
            document.getElementById("btn-cancel-task").disabled = false;
            runNextTask();
            break;

          case "task_cancelled":
            if (state.currentTask && msg.taskId && msg.taskId !== state.currentTask.taskId) break;
            appendLog(`✖ ${msg.packageName || "Task"} cancelled. Its state is re-checked by the next scan.`);
            showToast(`Cancelled ${msg.packageName || "task"}`, "warn");
            state.batchDone = (state.batchDone || 0) + 1;
            document.getElementById("btn-cancel-task").disabled = false;
            runNextTask();
            break;

          case "exported_winget_txt":
            showToast(`Synced presets to ${msg.path}`);
            break;

          case "window_state_changed":
            const iconMax = document.getElementById("icon-win-max");
            const iconRestore = document.getElementById("icon-win-restore");
            const btnMax = document.getElementById("win-btn-maximize");
            if (iconMax && iconRestore && btnMax) {
              if (msg.maximized) {
                iconMax.classList.add("hidden");
                iconRestore.classList.remove("hidden");
                btnMax.setAttribute("title", "Restore");
                btnMax.setAttribute("aria-label", "Restore");
              } else {
                iconMax.classList.remove("hidden");
                iconRestore.classList.add("hidden");
                btnMax.setAttribute("title", "Maximize");
                btnMax.setAttribute("aria-label", "Maximize");
              }
            }
            break;

          case "error":
            appendLog(`✖ Error: ${msg.message}`);
            showToast(`Error: ${msg.message}`, "error");
            if (state.checkingUpdates) {
              state.checkingUpdates = false;
              setButtonBusy("btn-check-updates", false);
            }
            break;
        }
      });

      sendHost({ action: "get_presets" });
      sendHost({ action: "scan_installed", startup: true });
      // Failsafe: never trap the user behind the splash if the host stalls
      splashTimer = setTimeout(hideSplash, 60000);
    } else {
      hideSplash();
    }

    document.getElementById("splash-close")?.addEventListener("click", requestClose);
  });
})();
