(() => {
  const state = {
    token: sessionStorage.getItem("classroom.teacherToken"),
    teacher: null,
    classes: [],
    classId: null,
    session: null,
    students: [],
    filter: "all",
    commandKind: "message",
    commandTargetIds: null,
    pollTimer: null,
    toastTimer: null
  };

  const $ = (id) => document.getElementById(id);
  const loginView = $("login-view");
  const appView = $("app-view");
  const loginError = $("login-error");

  async function api(path, options = {}) {
    const headers = { Accept: "application/json", ...(options.headers || {}) };
    if (state.token) headers.Authorization = `Bearer ${state.token}`;
    if (options.body && typeof options.body !== "string") {
      headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(options.body);
    }
    const response = await fetch(path, { ...options, headers });
    let payload = null;
    const contentType = response.headers.get("content-type") || "";
    if (contentType.includes("json")) payload = await response.json();
    if (response.status === 401) {
      clearSession();
      throw new Error("로그인이 만료되었습니다.");
    }
    if (!response.ok) {
      throw new Error(payload?.message || `요청에 실패했습니다. (${response.status})`);
    }
    return payload;
  }

  function clearSession() {
    state.token = null;
    state.teacher = null;
    sessionStorage.removeItem("classroom.teacherToken");
    if (state.pollTimer) clearInterval(state.pollTimer);
    loginView.hidden = false;
    appView.hidden = true;
  }

  function showToast(message) {
    const toast = $("toast");
    toast.textContent = message;
    toast.classList.add("show");
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => toast.classList.remove("show"), 2800);
  }

  function currentClass() {
    return state.classes.find((item) => item.id === state.classId) || null;
  }

  async function loadTeacher() {
    const session = await api("/auth/me");
    state.teacher = session;
    state.classes = session.classes || [];
    if (!state.classes.length) throw new Error("담당 학급이 없습니다.");
    state.classId = state.classId && state.classes.some((item) => item.id === state.classId)
      ? state.classId
      : state.classes[0].id;
    $("teacher-name").textContent = session.displayName || "교사";
    const select = $("class-select");
    select.innerHTML = state.classes.map((item) => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join("");
    select.value = state.classId;
    loginView.hidden = true;
    appView.hidden = false;
    await refreshClass();
    if (state.pollTimer) clearInterval(state.pollTimer);
    state.pollTimer = setInterval(() => refreshClass().catch((error) => showToast(error.message)), 2000);
  }

  async function refreshClass() {
    if (!state.classId) return;
    const selected = currentClass();
    $("class-subject").textContent = selected?.defaultSubject || "";
    const [session, students] = await Promise.all([
      api(`/api/classes/${state.classId}/session`),
      api(`/api/classes/${state.classId}/students`)
    ]);
    state.session = session;
    state.students = students || [];
    renderHeader();
    renderStudents();
    renderActivity();
    $("last-updated").textContent = `마지막 갱신 ${new Date().toLocaleTimeString("ko-KR", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
  }

  function renderHeader() {
    const online = state.students.filter((student) => student.online).length;
    $("online-count").textContent = `${online} / ${state.students.length}`;
    $("session-caption").textContent = state.session
      ? `${state.session.subject} · ${formatTime(state.session.startedAtUtc)} 시작`
      : "활성 수업이 없습니다.";
    $("end-session-button").hidden = !state.session;
    $("offline-banner").hidden = Boolean(state.session);
  }

  function renderStudents() {
    const grid = $("student-grid");
    const filtered = state.students.filter((student) => {
      if (state.filter === "online") return student.online;
      if (state.filter === "offline") return !student.online;
      if (state.filter === "focus") return student.policyApplied;
      return true;
    });
    if (!filtered.length) {
      grid.innerHTML = `<div class="empty-state">${state.students.length ? "현재 필터에 해당하는 학생이 없습니다." : "등록된 학생 장치가 아직 없습니다."}</div>`;
      return;
    }
    grid.innerHTML = filtered.map((student) => {
      const activity = student.activity;
      const statusClass = student.policyApplied ? "focus" : student.online ? "online" : "";
      const statusText = student.policyApplied ? "집중 모드" : student.online ? "온라인" : "오프라인";
      const battery = student.batteryPercent == null ? "배터리 —" : `배터리 ${student.batteryPercent}%`;
      return `<article class="student-card" data-device-id="${student.deviceId}">
        <div class="student-head"><div><div class="student-name">${escapeHtml(student.studentDisplayName)}</div><div class="student-device">${escapeHtml(student.computerName)}</div></div><span class="status-dot ${statusClass}">${statusText}</span></div>
        <div class="student-activity"><span class="app-icon">▣</span><div><div class="activity-app">${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</div><div class="activity-domain">${escapeHtml(activity?.browserDomain || "현재 도메인 없음")}</div></div></div>
        <div class="student-meta"><span>${battery}</span><span>${escapeHtml(student.networkStatus || "unknown")}</span>${student.policyApplied ? '<span class="policy-tag">🔒 집중</span>' : ""}</div>
      </article>`;
    }).join("");
    grid.querySelectorAll(".student-card").forEach((card) => card.addEventListener("click", () => openDetail(card.dataset.deviceId)));
  }

  function renderActivity() {
    const table = $("activity-table");
    const rows = state.students.map((student) => {
      const activity = student.activity;
      return `<div class="activity-row"><div><strong>${escapeHtml(student.studentDisplayName)}</strong><div class="sub">${escapeHtml(student.computerName)}</div></div><div>${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</div><div>${escapeHtml(activity?.browserDomain || "도메인 미연결")}</div><div><span class="status-dot ${student.online ? "online" : ""}">${student.online ? "온라인" : "오프라인"}</span></div></div>`;
    }).join("");
    table.innerHTML = `<div class="activity-row header"><div>학생</div><div>현재 앱</div><div>웹 도메인</div><div>상태</div></div>${rows || '<div class="empty-state">표시할 학생이 없습니다.</div>'}`;
  }

  function openDetail(deviceId) {
    const student = state.students.find((item) => item.deviceId === deviceId);
    if (!student) return;
    $("detail-pane").hidden = false;
    const activity = student.activity;
    $("detail-content").innerHTML = `<div class="eyebrow">STUDENT DEVICE</div><h2 class="detail-title">${escapeHtml(student.studentDisplayName)}</h2><div class="detail-status"><span class="status-dot ${student.online ? "online" : ""}">${student.online ? "온라인" : "오프라인"}</span></div><div class="detail-section"><h3>현재 상태</h3><div class="detail-row"><span>컴퓨터</span><strong>${escapeHtml(student.computerName)}</strong></div><div class="detail-row"><span>현재 앱</span><strong>${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</strong></div><div class="detail-row"><span>웹 도메인</span><strong>${escapeHtml(activity?.browserDomain || "도메인 미연결")}</strong></div><div class="detail-row"><span>배터리</span><strong>${student.batteryPercent == null ? "확인 필요" : `${student.batteryPercent}%`}</strong></div><div class="detail-row"><span>네트워크</span><strong>${escapeHtml(student.networkStatus || "unknown")}</strong></div><div class="detail-row"><span>마지막 heartbeat</span><strong>${formatTime(student.lastHeartbeatUtc)}</strong></div><div class="detail-row"><span>정책</span><strong>${student.policyApplied ? "집중 모드" : "일반"}</strong></div></div><div class="detail-section"><h3>장치 식별자</h3><div class="detail-row"><span>Device ID</span><code>${student.deviceId.slice(0, 8)}…</code></div><div class="detail-row"><span>Agent</span><strong>${escapeHtml(student.agentVersion)}</strong></div></div><div class="detail-section"><button class="secondary wide" id="detail-message-button">이 학생에게 메시지</button></div>`;
    $("detail-message-button").addEventListener("click", () => openCommandDialog("message", [deviceId]));
  }

  async function loadAudit() {
    if (!state.classId) return;
    const entries = await api(`/api/classes/${state.classId}/audit?limit=100`);
    $("audit-list").innerHTML = entries?.length ? entries.map((entry) => {
      const good = ["SUCCESS", "STARTED", "CONNECTED", "QUEUED", "ACCEPTED"].includes(entry.result);
      const bad = ["FAILED", "REJECTED", "QUEUE_FULL"].includes(entry.result);
      return `<div class="audit-item"><span class="audit-time">${formatTime(entry.timestampUtc)}</span><span class="audit-action">${escapeHtml(entry.action)}</span><span class="audit-reason">${escapeHtml(entry.reason || "—")}</span><span class="result-pill ${good ? "good" : bad ? "bad" : "neutral"}">${escapeHtml(entry.result)}</span></div>`;
    }).join("") : '<div class="empty-state">아직 기록이 없습니다.</div>';
  }

  function openCommandDialog(kind, targetIds = null) {
    if (!state.session) {
      showToast("먼저 수업을 시작하세요.");
      return;
    }
    state.commandKind = kind;
    state.commandTargetIds = targetIds;
    $("dialog-title").textContent = kind === "url" ? "URL 열기" : "메시지 보내기";
    $("url-field").hidden = kind !== "url";
    $("message-field").hidden = kind === "url";
    $("seconds-field").hidden = kind === "url";
    $("command-message").value = "";
    $("command-url").value = "";
    $("dialog-error").hidden = true;
    $("command-dialog").showModal();
  }

  async function sendCommand(kind, targetIds, extra = {}) {
    const targets = targetIds || state.students.map((student) => student.deviceId);
    if (!targets.length) throw new Error("대상 학생 장치가 없습니다.");
    if (!state.session) throw new Error("활성 수업이 없습니다.");
    const payload = {
      requestId: crypto.randomUUID(),
      sessionId: state.session.sessionId,
      targetDeviceIds: targets,
      kind,
      ...extra,
      requiresAcknowledgement: true
    };
    const result = await api(`/api/classes/${state.classId}/commands`, { method: "POST", body: payload });
    showToast(`${result.queuedCount}대 장치에 명령을 전달했습니다.`);
    return result;
  }

  async function startSession() {
    const selected = currentClass();
    const subject = prompt("수업 과목을 입력하세요.", selected?.defaultSubject || "정보");
    if (!subject) return;
    await api(`/api/classes/${state.classId}/sessions`, { method: "POST", body: { subject } });
    showToast("수업을 시작했습니다.");
    await refreshClass();
  }

  async function endSession() {
    if (!state.session || !confirm("현재 수업을 종료할까요?")) return;
    await api(`/api/classes/${state.classId}/sessions/${state.session.sessionId}`, { method: "DELETE" });
    showToast("수업을 종료했습니다.");
    await refreshClass();
  }

  async function enrollDevice() {
    const displayName = prompt("학생 이름을 입력하세요.");
    if (!displayName) return;
    const studentId = prompt("학생 ID를 입력하세요. (예: 학교 계정 UUID)");
    if (!studentId) return;
    const ticket = await api(`/api/classes/${state.classId}/enrollment-tickets`, {
      method: "POST",
      body: { studentId, studentDisplayName: displayName }
    });
    const message = `학생: ${ticket.studentId}\nDevice ID: ${ticket.deviceId}\nEnrollment token: ${ticket.enrollmentToken}\n\n학생 PC에서 이 값을 사용해 등록하세요.`;
    try { await navigator.clipboard.writeText(message); showToast("등록 정보가 클립보드에 복사되었습니다."); } catch (_) { /* clipboard is optional */ }
    window.prompt("학생 PC 등록에 전달할 일회성 정보입니다.", message);
  }

  function formatTime(value) {
    if (!value) return "—";
    return new Date(value).toLocaleString("ko-KR", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
  }

  $("login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    loginError.hidden = true;
    try {
      const result = await api("/auth/login", { method: "POST", body: { loginName: $("login-name").value, password: $("login-password").value } });
      state.token = result.accessToken;
      sessionStorage.setItem("classroom.teacherToken", state.token);
      await loadTeacher();
    } catch (error) {
      loginError.textContent = error.message;
      loginError.hidden = false;
    }
  });
  $("logout-button").addEventListener("click", async () => {
    try { await api("/auth/logout", { method: "POST" }); } catch (_) { /* local logout still clears the token */ }
    clearSession();
  });
  $("class-select").addEventListener("change", async (event) => {
    state.classId = event.target.value;
    state.session = null;
    await refreshClass();
  });
  $("start-session-button").addEventListener("click", () => startSession().catch((error) => showToast(error.message)));
  $("enroll-button").addEventListener("click", () => enrollDevice().catch((error) => showToast(error.message)));
  $("end-session-button").addEventListener("click", () => endSession().catch((error) => showToast(error.message)));
  $("focus-on-button").addEventListener("click", () => sendCommand("focusMode", null, { message: "수업에 집중해 주세요.", focusEnabled: true }).catch((error) => showToast(error.message)));
  $("focus-off-button").addEventListener("click", () => sendCommand("focusMode", null, { focusEnabled: false }).catch((error) => showToast(error.message)));
  $("message-button").addEventListener("click", () => openCommandDialog("message"));
  $("url-button").addEventListener("click", () => openCommandDialog("url"));
  $("refresh-audit-button").addEventListener("click", () => loadAudit().catch((error) => showToast(error.message)));
  $("close-detail").addEventListener("click", () => { $("detail-pane").hidden = true; });
  $("password-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const message = $("password-message");
    message.hidden = true;
    if ($("new-password").value !== $("new-password-confirm").value) {
      message.textContent = "새 비밀번호가 일치하지 않습니다.";
      message.hidden = false;
      return;
    }
    try {
      await api("/auth/change-password", {
        method: "POST",
        body: {
          currentPassword: $("current-password").value,
          newPassword: $("new-password").value
        }
      });
      $("password-form").reset();
      showToast("비밀번호를 변경했습니다.");
    } catch (error) {
      message.textContent = error.message;
      message.hidden = false;
    }
  });
  document.querySelectorAll(".filter").forEach((button) => button.addEventListener("click", () => {
    document.querySelectorAll(".filter").forEach((item) => item.classList.remove("active"));
    button.classList.add("active");
    state.filter = button.dataset.filter;
    renderStudents();
  }));
  document.querySelectorAll(".nav-item").forEach((button) => button.addEventListener("click", async () => {
    document.querySelectorAll(".nav-item").forEach((item) => item.classList.remove("active"));
    button.classList.add("active");
    document.querySelectorAll(".section-view").forEach((section) => { section.hidden = section.id !== `${button.dataset.section}-section`; });
    if (button.dataset.section === "history") await loadAudit().catch((error) => showToast(error.message));
  }));
  $("command-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const errorTarget = $("dialog-error");
    errorTarget.hidden = true;
    try {
      if (state.commandKind === "url") {
        await sendCommand("openUrl", state.commandTargetIds, { url: $("command-url").value });
      } else {
        await sendCommand("message", state.commandTargetIds, { message: $("command-message").value, displaySeconds: Number($("command-seconds").value) });
      }
      $("command-dialog").close();
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });

  if (state.token) {
    loadTeacher().catch((error) => { clearSession(); loginError.textContent = error.message; loginError.hidden = false; });
  }
})();
