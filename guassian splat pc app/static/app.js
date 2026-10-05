(() => {
  "use strict";

  const $ = (sel) => document.querySelector(sel);
  const ACTIVE = new Set(["uploading", "preparing", "ready", "queued", "running", "failed"]);
  const LABEL = {
    uploading: "Uploading", preparing: "Preparing", ready: "Ready", queued: "Queued",
    running: "Running", failed: "Failed", complete: "Complete", exporting: "Exporting",
  };

  const ICONS = {
    play: '<svg viewBox="0 0 24 24"><path d="M6 3.5v17l14-8.5z" fill="currentColor"/></svg>',
    stop: '<svg viewBox="0 0 24 24"><rect x="6" y="6" width="12" height="12" rx="1.5" fill="currentColor"/></svg>',
    trash: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1"><circle cx="12" cy="12" r="11"/>' +
      '<path d="M7.5 8h9M10 8V6.6h4V8M8.6 8l.8 10h5.2l.8-10M10.8 10v6.5M12 10v6.5M13.2 10v6.5" stroke-linejoin="round"/></svg>',
    download: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M10.6 3h2.8v8h3.1L12 15.6 7.5 11h3.1z"/>' +
      '<path d="M3 14.5h2v4.5h14v-4.5h2V21H3z"/></svg>',
    spinner: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"><path d="M12 3a9 9 0 1 1-9 9">' +
      '<animateTransform attributeName="transform" type="rotate" from="0 12 12" to="360 12 12" dur="0.9s" repeatCount="indefinite"/></path></svg>',
  };

  const state = { sessions: [], nerfstudio: { found: true } };
  const expanded = new Set();     // session ids with the log open
  const logs = {};                // sid -> log text
  const armed = new Map();        // sid -> timeout for "click again to delete"
  let lastRenderKey = "";

  // ------------------------------------------------------------------ helpers
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function fmtDate(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return "";
    let h = d.getHours();
    const ampm = h >= 12 ? "pm" : "am";
    h = h % 12 || 12;
    const p = (n) => String(n).padStart(2, "0");
    return `${d.getMonth() + 1}/${d.getDate()}/${d.getFullYear()} ${h}:${p(d.getMinutes())}:${p(d.getSeconds())}${ampm}`;
  }

  let toastTimer;
  function toast(msg, isError = false) {
    const t = $("#toast");
    t.textContent = msg;
    t.className = "toast" + (isError ? " error" : "");
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (t.hidden = true), isError ? 7000 : 3500);
  }

  async function api(path, opts = {}) {
    const res = await fetch(path, opts);
    let body = null;
    try { body = await res.json(); } catch { /* non-JSON */ }
    if (!res.ok) throw new Error((body && body.error) || `${res.status} ${res.statusText}`);
    return body;
  }

  // ------------------------------------------------------------------ upload
  const form = $("#upload-form");
  const card = $("#upload-card");
  const hint = $("#upload-hint");
  const chosen = {}; // field -> File

  function setFile(field, file) {
    chosen[field] = file || null;
    const label = form.querySelector(`.file[data-field="${field}"]`);
    label.classList.toggle("chosen", !!file);
    const name = label.querySelector(".file-name");
    name.textContent = file ? file.name : "No File Chosen";
    name.title = file ? file.name : "";
  }

  form.querySelectorAll(".file input").forEach((input) => {
    input.addEventListener("change", () => setFile(input.name, input.files[0]));
  });

  // Drag & drop: assign dropped files to slots by name/extension.
  function classify(file) {
    const n = file.name.toLowerCase();
    if (n.endsWith(".mp4") || n.endsWith(".mov") || file.type.startsWith("video/")) return "video";
    if (n.includes("pose")) return "poses";
    if (n.includes("intrinsic")) return "intrinsics";
    if (n.includes("imu")) return "imu";
    if (n.endsWith(".jsonl")) return "poses";
    return null;
  }
  ["dragenter", "dragover"].forEach((ev) => card.addEventListener(ev, (e) => { e.preventDefault(); card.classList.add("dragover"); }));
  ["dragleave", "drop"].forEach((ev) => card.addEventListener(ev, (e) => { e.preventDefault(); card.classList.remove("dragover"); }));
  card.addEventListener("drop", (e) => {
    const unknown = [];
    for (const f of e.dataTransfer.files) {
      const slot = classify(f);
      slot ? setFile(slot, f) : unknown.push(f.name);
    }
    if (unknown.length) toast(`Ignored: ${unknown.join(", ")}`);
  });

  form.addEventListener("submit", (e) => {
    e.preventDefault();
    hint.classList.remove("error");
    if (!chosen.video || !chosen.poses) {
      hint.textContent = "Choose at least Video.mp4 and Poses.jsonl.";
      hint.classList.add("error");
      return;
    }
    const fd = new FormData();
    for (const [k, f] of Object.entries(chosen)) if (f) fd.append(k, f, f.name);

    const btn = $("#upload-btn");
    btn.disabled = true;
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/api/upload");
    xhr.upload.onprogress = (ev) => {
      if (ev.lengthComputable) btn.textContent = `Uploading ${Math.round((ev.loaded / ev.total) * 100)}%`;
    };
    xhr.onload = () => {
      btn.disabled = false;
      btn.textContent = "Upload & Prepare";
      if (xhr.status >= 200 && xhr.status < 300) {
        ["video", "poses", "intrinsics", "imu"].forEach((f) => setFile(f, null));
        form.reset();
        hint.textContent = "Uploaded — preparing frames below.";
        refresh();
      } else {
        let msg = "Upload failed.";
        try { msg = JSON.parse(xhr.responseText).error || msg; } catch { /* ignore */ }
        hint.textContent = msg;
        hint.classList.add("error");
      }
    };
    xhr.onerror = () => {
      btn.disabled = false;
      btn.textContent = "Upload & Prepare";
      hint.textContent = "Upload failed — is the app still running?";
      hint.classList.add("error");
    };
    xhr.send(fd);
  });

  $("#env-recheck").addEventListener("click", () => refresh(true));

  // ------------------------------------------------------------------ rendering
  function pill(s) {
    if (s.status === "complete" && s.has_mesh) return '<span class="pill meshready">Mesh Ready</span>';
    return `<span class="pill ${s.status}">${LABEL[s.status] || s.status}</span>`;
  }

  function iconBtn(action, icon, title, { disabled = false, cls = "" } = {}) {
    return `<button type="button" class="icon ${cls}" data-action="${action}" title="${esc(title)}"${disabled ? " disabled" : ""}>${ICONS[icon]}</button>`;
  }

  function actions(s) {
    const del = iconBtn("delete", "trash", armed.has(s.id) ? "Click again to delete" : "Delete session",
      { cls: "trash" + (armed.has(s.id) ? " armed" : "") });
    if (ACTIVE.has(s.status)) {
      if (s.status === "queued" || s.status === "running") return iconBtn("stop", "stop", "Stop training") + del;
      const canTrain = (s.status === "ready" || s.status === "failed") && s.frames && state.nerfstudio.found;
      const why = !state.nerfstudio.found ? "Install NerfStudio to train" : s.frames ? "Train nerfacto" : "Not ready yet";
      return iconBtn("train", "play", why, { disabled: !canTrain }) + del;
    }
    if (s.status === "exporting") return iconBtn("stop", "spinner", "Exporting mesh… click to cancel") + del;
    if (s.has_mesh) return iconBtn("save", "download", "Save mesh (.zip)", { cls: "ready-dl" }) + del;
    return iconBtn("export", "download", "Export 3D mesh") + del;
  }

  function detail(s) {
    const showBar = ["preparing", "queued", "running", "exporting", "uploading"].includes(s.status);
    const showMsg = showBar || s.status === "failed" || expanded.has(s.id) ||
      (s.status === "complete" && s.message && /fail|cancel|interrupt|No trained/i.test(s.message)) ||
      (s.status === "ready" && /interrupt|stopped/i.test(s.message || ""));
    if (!showMsg) return "";
    let bar = "";
    if (showBar) {
      const pct = typeof s.progress === "number" ? Math.max(0, Math.min(100, s.progress)) : null;
      const kind = s.status === "exporting" ? " export" : s.status === "preparing" ? " prep" : "";
      bar = `<div class="bar${kind}${pct === null ? " indeterminate" : ""}"><i style="width:${pct ?? 30}%"></i></div>`;
    }
    const bits = [];
    if (s.status === "running" && typeof s.progress === "number") bits.push(`${s.progress.toFixed(1)}%`);
    if (s.message) bits.push(esc(s.message));
    if (s.eta && s.status === "running") bits.push(`ETA ${esc(s.eta)}`);
    let links = "";
    if (s.viewer_url && s.status === "running") links += `<a href="${esc(s.viewer_url)}" target="_blank" rel="noopener">Open live viewer ↗</a>`;
    if (s.has_mesh && window.pywebview) links += `<span class="link" data-action="folder">Show in folder</span>`;
    links += `<span class="link" data-action="log">${expanded.has(s.id) ? "Hide log" : "Show log"}</span>`;
    const log = expanded.has(s.id) ? `<pre class="log" data-log="${s.id}">${esc(logs[s.id] || "Loading…")}</pre>` : "";
    return `<tr class="detail" data-id="${s.id}"><td colspan="5">${bar}<span class="msg${s.status === "failed" ? " error" : ""}">${bits.join(" · ")}</span>${links}${log}</td></tr>`;
  }

  function row(s) {
    const frames = s.frames == null ? "—" : String(s.frames);
    const framesTitle = s.frames_total ? `${s.frames} of ${s.frames_total} video frames used` : "";
    return `<tr class="row" data-id="${s.id}">
      <td><span class="sid" data-action="log" title="${esc(s.id)} — click for log">${esc(s.id.slice(0, 9))}...</span></td>
      <td title="${esc(s.message || "")}">${pill(s)}</td>
      <td title="${esc(framesTitle)}">${frames}</td>
      <td>${fmtDate(s.created)}</td>
      <td><div class="actions">${actions(s)}</div></td>
    </tr>${detail(s)}`;
  }

  function render(force = false) {
    const key = JSON.stringify([state, [...expanded], [...armed.keys()]]);
    if (!force && key === lastRenderKey) return;
    lastRenderKey = key;

    // keep log scroll positions across re-renders
    const scroll = {};
    document.querySelectorAll("pre.log").forEach((p) => {
      scroll[p.dataset.log] = p.scrollHeight - p.scrollTop - p.clientHeight < 8 ? "bottom" : p.scrollTop;
    });

    $("#env-warning").hidden = !!state.nerfstudio.found;
    const active = state.sessions.filter((s) => ACTIVE.has(s.status));
    const finished = state.sessions.filter((s) => !ACTIVE.has(s.status));
    $("#active-body").innerHTML = active.map(row).join("");
    $("#finished-body").innerHTML = finished.map(row).join("");
    $("#active-empty").hidden = active.length > 0;
    $("#finished-empty").hidden = finished.length > 0;

    document.querySelectorAll("pre.log").forEach((p) => {
      const pos = scroll[p.dataset.log];
      p.scrollTop = pos === undefined || pos === "bottom" ? p.scrollHeight : pos;
    });
  }

  // ------------------------------------------------------------------ actions
  async function onAction(action, sid) {
    try {
      switch (action) {
        case "train":
          await api(`/api/sessions/${sid}/train`, { method: "POST" });
          toast("Training queued.");
          break;
        case "stop":
          await api(`/api/sessions/${sid}/stop`, { method: "POST" });
          break;
        case "export":
          await api(`/api/sessions/${sid}/export`, { method: "POST" });
          toast("Exporting mesh — this can take a few minutes.");
          break;
        case "save":
          if (window.pywebview && window.pywebview.api) {
            const r = await window.pywebview.api.save_mesh(sid);
            if (r && r.ok) toast(`Saved to ${r.path}`);
            else if (r && r.error) toast(r.error, true);
          } else {
            window.location.href = `/api/sessions/${sid}/mesh`;
          }
          break;
        case "folder":
          if (window.pywebview) await window.pywebview.api.open_folder(sid);
          break;
        case "log":
          if (expanded.has(sid)) expanded.delete(sid);
          else { expanded.add(sid); loadLog(sid); }
          render(true);
          return;
        case "delete":
          if (!armed.has(sid)) {
            armed.set(sid, setTimeout(() => { armed.delete(sid); render(true); }, 3000));
            render(true);
            return;
          }
          clearTimeout(armed.get(sid));
          armed.delete(sid);
          await api(`/api/sessions/${sid}`, { method: "DELETE" });
          expanded.delete(sid);
          break;
      }
    } catch (err) {
      toast(err.message, true);
    }
    refresh();
  }

  document.addEventListener("click", (e) => {
    const el = e.target.closest("[data-action]");
    if (!el || el.disabled) return;
    const tr = el.closest("tr[data-id]");
    if (tr) onAction(el.dataset.action, tr.dataset.id);
  });

  async function loadLog(sid) {
    try {
      const { log } = await api(`/api/sessions/${sid}/log`);
      if (logs[sid] !== log) {
        logs[sid] = log || "(no output yet)";
        const pre = document.querySelector(`pre.log[data-log="${sid}"]`);
        if (pre) {
          const atBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 8;
          pre.textContent = logs[sid];
          if (atBottom) pre.scrollTop = pre.scrollHeight;
        }
      }
    } catch { /* session gone */ }
  }

  // ------------------------------------------------------------------ polling
  let polling = false;
  async function refresh(recheckEnv = false) {
    if (polling) return;
    polling = true;
    try {
      const data = await api("/api/state" + (recheckEnv ? "?refresh=1" : ""));
      state.sessions = data.sessions;
      state.nerfstudio = data.nerfstudio;
      for (const sid of [...expanded]) {
        if (!state.sessions.some((s) => s.id === sid)) expanded.delete(sid);
      }
      render();
      if (recheckEnv) toast(data.nerfstudio.found ? `NerfStudio found: ${data.nerfstudio.path}` : "Still can't find NerfStudio.", !data.nerfstudio.found);
    } catch {
      /* server restarting; try again next tick */
    } finally {
      polling = false;
    }
  }

  refresh();
  setInterval(refresh, 1000);
  setInterval(() => expanded.forEach(loadLog), 1500);
})();
