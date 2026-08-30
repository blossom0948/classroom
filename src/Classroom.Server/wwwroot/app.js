(() => {
  const runtimeConfig = window.CLASSROOM_CONFIG || {};
  const apiOrigin = String(runtimeConfig.apiOrigin || "").trim().replace(/\/+$/, "");
  const state = {
    token: sessionStorage.getItem("classroom.teacherToken"),
    teacher: null,
    classes: [],
    classId: null,
    session: null,
    students: [],
    filter: "all",
    search: "",
    selectedDeviceIds: new Set(),
    commandKind: "message",
    commandTargetIds: null,
    pollTimer: null,
    toastTimer: null,
    enrollmentBundle: null,
    theme: localStorage.getItem("classroom.theme") || "light",
    activeSection: "class"
  };

  const $ = (id) => document.getElementById(id);
  const landingView = $("landing-view");
  const loginView = $("login-view");
  const appView = $("app-view");
  const loginError = $("login-error");

  function apiUrl(path) {
    return apiOrigin ? `${apiOrigin}${path}` : path;
  }

  function studentServerUrl() {
    const value = new URL(apiOrigin || window.location.origin);
    value.protocol = value.protocol === "https:" ? "wss:" : "ws:";
    value.pathname = value.pathname.replace(/\/+$/, "");
    value.search = "";
    value.hash = "";
    return value.toString().replace(/\/+$/, "");
  }

  async function api(path, options = {}) {
    const headers = { Accept: "application/json", ...(options.headers || {}) };
    if (state.token) headers.Authorization = `Bearer ${state.token}`;
    if (options.body && typeof options.body !== "string") {
      headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(options.body);
    }
    let response;
    try {
      response = await fetch(apiUrl(path), { ...options, headers });
    } catch (_) {
      throw new Error("Classroom 서버에 연결할 수 없습니다. 서버 주소와 배포 상태를 확인하세요.");
    }
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
    state.classes = [];
    state.classId = null;
    state.session = null;
    state.students = [];
    state.selectedDeviceIds.clear();
    sessionStorage.removeItem("classroom.teacherToken");
    if (state.pollTimer) clearInterval(state.pollTimer);
    state.pollTimer = null;
    landingView.hidden = false;
    loginView.hidden = true;
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
    landingView.hidden = true;
    loginView.hidden = true;
    appView.hidden = false;
    applyTheme(state.theme);
    loadLessonNote();
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
    const currentIds = new Set(state.students.map((student) => student.deviceId));
    state.selectedDeviceIds = new Set(
      [...state.selectedDeviceIds].filter((deviceId) => currentIds.has(deviceId))
    );
    renderHeader();
    renderStudents();
    renderActivity();
    $("last-updated").textContent = `마지막 갱신 ${new Date().toLocaleTimeString("ko-KR", { hour: "2-digit", minute: "2-digit", second: "2-digit" })}`;
  }

  function renderHeader() {
    const online = state.students.filter((student) => student.online).length;
    const offline = state.students.length - online;
    const focus = state.students.filter((student) => student.policyApplied).length;
    $("online-count").textContent = `${online} / ${state.students.length}`;
    $("total-count").textContent = String(state.students.length);
    $("metric-online-count").textContent = String(online);
    $("offline-count").textContent = String(offline);
    $("focus-count").textContent = String(focus);
    $("session-caption").textContent = state.session
      ? `${state.session.subject} · ${formatTime(state.session.startedAtUtc)} 시작`
      : "활성 수업이 없습니다.";
    $("end-session-button").hidden = !state.session;
    $("offline-banner").hidden = Boolean(state.session);
    $("offline-banner-text").textContent = state.session
      ? "현재 진행 중인 수업이 없습니다. 아래에서 새 수업을 시작하세요."
      : "현재 진행 중인 수업이 없습니다. 아래에서 새 수업을 시작하세요.";
    renderSelection();
  }

  function renderSelection() {
    const count = state.selectedDeviceIds.size;
    $("selection-caption").textContent = count ? `${count}명 선택됨` : "전체 학생 대상";
    $("clear-selection-button").hidden = count === 0;
  }

  function commandTargets() {
    return state.selectedDeviceIds.size ? [...state.selectedDeviceIds] : null;
  }

  function renderStudents() {
    const grid = $("student-grid");
    const query = state.search.toLocaleLowerCase("ko-KR");
    const filtered = state.students.filter((student) => {
      if (state.filter === "online") return student.online;
      if (state.filter === "offline") return !student.online;
      if (state.filter === "focus") return student.policyApplied;
      return true;
    }).filter((student) => !query
      || student.studentDisplayName.toLocaleLowerCase("ko-KR").includes(query)
      || student.computerName.toLocaleLowerCase("ko-KR").includes(query));
    if (!filtered.length) {
      if (state.students.length) {
        grid.innerHTML = '<div class="empty-state">현재 필터에 해당하는 학생이 없습니다.</div>';
      } else {
        grid.innerHTML = '<div class="empty-state"><strong>첫 학생 PC를 등록해 보세요.</strong><p>학생 이름만 입력하면 일회성 등록 파일을 만들 수 있습니다.</p><button id="empty-enroll-button" class="primary">학생 PC 등록</button></div>';
        $("empty-enroll-button").addEventListener("click", openEnrollmentDialog);
      }
      return;
    }
    grid.innerHTML = filtered.map((student) => {
      const activity = student.activity;
      const statusClass = student.policyApplied ? "focus" : student.online ? "online" : "";
      const statusText = student.policyApplied ? "집중 모드" : student.online ? "온라인" : "오프라인";
      const battery = student.batteryPercent == null ? "배터리 —" : `배터리 ${student.batteryPercent}%`;
      const selected = state.selectedDeviceIds.has(student.deviceId);
      return `<article class="student-card${selected ? " selected" : ""}" data-device-id="${student.deviceId}">
        <label class="student-selector" title="명령 대상 선택"><input type="checkbox" aria-label="${escapeHtml(student.studentDisplayName)} 선택" ${selected ? "checked" : ""}></label>
        <div class="student-head"><div><div class="student-name">${escapeHtml(student.studentDisplayName)}</div><div class="student-device">${escapeHtml(student.computerName)}</div></div><span class="status-dot ${statusClass}">${statusText}</span></div>
        <div class="student-activity"><span class="app-icon">▣</span><div><div class="activity-app">${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</div><div class="activity-domain">${escapeHtml(activity?.browserDomain || "현재 도메인 없음")}</div></div></div>
        <div class="student-meta"><span>${battery}</span><span>${escapeHtml(student.networkStatus || "unknown")}</span>${student.policyApplied ? '<span class="policy-tag">🔒 집중</span>' : ""}</div>
      </article>`;
    }).join("");
    grid.querySelectorAll(".student-card").forEach((card) => {
      const checkbox = card.querySelector("input[type=checkbox]");
      checkbox.addEventListener("click", (event) => event.stopPropagation());
      checkbox.addEventListener("change", () => {
        if (checkbox.checked) state.selectedDeviceIds.add(card.dataset.deviceId);
        else state.selectedDeviceIds.delete(card.dataset.deviceId);
        renderStudents();
        renderSelection();
      });
      card.addEventListener("click", () => openDetail(card.dataset.deviceId));
    });
  }

  function renderActivity() {
    const table = $("activity-table");
    const online = state.students.filter((student) => student.online).length;
    const focus = state.students.filter((student) => student.policyApplied).length;
    const offline = state.students.length - online;
    $("activity-summary").innerHTML = `<article class="metric-card metric-online"><span class="metric-label">연결됨</span><strong>${online}</strong><small>학생 PC</small></article><article class="metric-card metric-offline"><span class="metric-label">확인 필요</span><strong>${offline}</strong><small>오프라인</small></article><article class="metric-card metric-focus"><span class="metric-label">집중 모드</span><strong>${focus}</strong><small>정책 적용</small></article>`;
    const insights = [];
    if (offline > 0) insights.push(`<span class="insight-dot warning"></span><strong>${offline}명</strong>의 장치가 오프라인입니다.`);
    if (focus > 0) insights.push(`<span class="insight-dot focus"></span><strong>${focus}명</strong>에게 집중 모드가 적용되어 있습니다.`);
    if (!insights.length) insights.push('<span class="insight-dot good"></span>현재 확인이 필요한 학생이 없습니다.');
    $("activity-insights").innerHTML = insights.map((item) => `<div class="insight-item">${item}</div>`).join("");
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
    $("detail-content").innerHTML = `<div class="eyebrow">STUDENT DEVICE</div><h2 class="detail-title">${escapeHtml(student.studentDisplayName)}</h2><div class="detail-status"><span class="status-dot ${student.online ? "online" : ""}">${student.online ? "온라인" : "오프라인"}</span></div><div class="detail-section"><h3>현재 상태</h3><div class="detail-row"><span>컴퓨터</span><strong>${escapeHtml(student.computerName)}</strong></div><div class="detail-row"><span>현재 앱</span><strong>${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</strong></div><div class="detail-row"><span>웹 도메인</span><strong>${escapeHtml(activity?.browserDomain || "도메인 미연결")}</strong></div><div class="detail-row"><span>배터리</span><strong>${student.batteryPercent == null ? "확인 필요" : `${student.batteryPercent}%`}</strong></div><div class="detail-row"><span>네트워크</span><strong>${escapeHtml(student.networkStatus || "unknown")}</strong></div><div class="detail-row"><span>마지막 heartbeat</span><strong>${formatTime(student.lastHeartbeatUtc)}</strong></div><div class="detail-row"><span>정책</span><strong>${student.policyApplied ? "집중 모드" : "일반"}</strong></div></div><div class="detail-section"><h3>장치 식별자</h3><div class="detail-row"><span>Device ID</span><code>${student.deviceId.slice(0, 8)}…</code></div><div class="detail-row"><span>Agent</span><strong>${escapeHtml(student.agentVersion)}</strong></div></div><div class="detail-section stack"><button class="secondary wide" id="detail-message-button">이 학생에게 메시지</button><button class="danger-action wide" id="detail-revoke-button">장치 연결 해제</button></div>`;
    $("detail-message-button").addEventListener("click", () => openCommandDialog("message", [deviceId]));
    $("detail-revoke-button").addEventListener("click", () => revokeDevice(student).catch((error) => showToast(error.message)));
  }

  async function revokeDevice(student) {
    if (!confirm(`${student.studentDisplayName} 학생의 ${student.computerName} 연결을 해제할까요?\n이 장치는 새 등록 파일 없이는 다시 연결할 수 없습니다.`)) return;
    await api(`/api/classes/${state.classId}/devices/${student.deviceId}`, { method: "DELETE" });
    $("detail-pane").hidden = true;
    showToast("학생 장치 연결을 해제했습니다.");
    await refreshClass();
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
    state.commandTargetIds = Array.isArray(targetIds) ? [...targetIds] : null;
    $("dialog-title").textContent = kind === "url" ? "URL 열기" : kind === "app" ? "승인된 앱 실행" : "메시지 보내기";
    const targetCount = state.commandTargetIds?.length || state.students.length;
    $("command-audience").textContent = state.commandTargetIds?.length
      ? `선택한 학생 ${targetCount}명에게만 전달합니다.`
      : `등록된 학생 전체 ${targetCount}명에게 전달합니다.`;
    $("url-field").hidden = kind !== "url";
    $("app-field").hidden = kind !== "app";
    $("message-field").hidden = kind !== "message";
    $("seconds-field").hidden = kind !== "message";
    $("message-presets").hidden = kind !== "message";
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
    showToast(`${result.queuedCount}대 장치에 명령을 대기열로 보냈습니다.`);
    monitorCommand(result.requestId).catch((error) => showToast(error.message));
    return result;
  }

  async function monitorCommand(requestId) {
    for (let attempt = 0; attempt < 15; attempt += 1) {
      await new Promise((resolve) => setTimeout(resolve, 1000));
      const status = await api(`/api/classes/${state.classId}/commands/${requestId}`);
      if (status.finished) {
        if (status.failedCount) {
          showToast(`명령 결과: ${status.completedCount}대 성공, ${status.failedCount}대 실패`);
        } else {
          showToast(`명령 적용 완료: ${status.completedCount}/${status.totalCount}대`);
        }
        return;
      }
    }
    showToast("명령은 전달됐지만 일부 학생 PC의 응답을 기다리는 중입니다.");
  }

  function openSessionDialog() {
    const selected = currentClass();
    $("session-subject").value = selected?.defaultSubject || "정보";
    $("session-error").hidden = true;
    $("session-dialog").showModal();
  }

  async function startSession(subject) {
    const started = await api(`/api/classes/${state.classId}/sessions`, { method: "POST", body: { subject } });
    state.session = started;
    renderHeader();
    showToast("수업을 시작했습니다.");
    await refreshClass();
  }

  async function endSession() {
    if (!state.session || !confirm("현재 수업을 종료할까요?")) return;
    await api(`/api/classes/${state.classId}/sessions/${state.session.sessionId}`, { method: "DELETE" });
    showToast("수업을 종료했습니다.");
    await refreshClass();
  }

  function openEnrollmentDialog() {
    state.enrollmentBundle = null;
    $("enrollment-form").reset();
    $("enrollment-fields").hidden = false;
    $("enrollment-result").hidden = true;
    $("enrollment-create").hidden = false;
    $("enrollment-cancel").hidden = false;
    $("enrollment-download").hidden = true;
    $("enrollment-copy").hidden = true;
    $("enrollment-done").hidden = true;
    $("enrollment-error").hidden = true;
    $("enrollment-dialog").showModal();
  }

  async function createEnrollmentBundle() {
    const displayName = $("enrollment-name").value.trim();
    if (!displayName) throw new Error("학생 이름을 입력해 주세요.");
    const studentId = null;
    const ticket = await api(`/api/classes/${state.classId}/enrollment-tickets`, {
      method: "POST",
      body: { studentId, studentDisplayName: displayName }
    });
    const safeName = displayName.replace(/[^0-9A-Za-z가-힣_-]+/g, "-").replace(/^-|-$/g, "") || "student";
    state.enrollmentBundle = {
      fileName: `classroom-enrollment-${safeName}.json`,
      value: {
        format: "BLOSSOM-CLASSROOM-ENROLLMENT-V1",
        serverUrl: studentServerUrl(),
        deviceId: ticket.deviceId,
        studentId: ticket.studentId,
        studentDisplayName: displayName,
        enrollmentToken: ticket.enrollmentToken,
        expiresAtUtc: ticket.expiresAtUtc
      }
    };
    $("enrollment-result-name").textContent = `${displayName} 학생 등록 파일이 준비되었습니다.`;
    $("enrollment-expiry").textContent = `${formatTime(ticket.expiresAtUtc)}까지 한 번만 사용할 수 있습니다.`;
    $("enrollment-command").textContent = `학생용 패키지 폴더에 넣고 Install-ClassroomStudent.cmd를 두 번 클릭하세요. (${state.enrollmentBundle.fileName})`;
    $("enrollment-fields").hidden = true;
    $("enrollment-result").hidden = false;
    $("enrollment-create").hidden = true;
    $("enrollment-cancel").hidden = true;
    $("enrollment-download").hidden = false;
    $("enrollment-copy").hidden = false;
    $("enrollment-done").hidden = false;
  }

  function downloadEnrollmentBundle() {
    if (!state.enrollmentBundle) return;
    const blob = new Blob([`${JSON.stringify(state.enrollmentBundle.value, null, 2)}\n`], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = state.enrollmentBundle.fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
    showToast("등록 파일을 다운로드했습니다.");
  }

  async function copyEnrollmentInstructions() {
    if (!state.enrollmentBundle) return;
    const text = `Classroom 학생 PC 등록\n1. 학생용 패키지 압축을 풉니다.\n2. ${state.enrollmentBundle.fileName}을 패키지 폴더에 넣습니다.\n3. Install-ClassroomStudent.cmd를 두 번 클릭하고 관리자 권한을 승인합니다.`;
    try {
      await navigator.clipboard.writeText(text);
      showToast("학생 설치 안내를 복사했습니다.");
    } catch (_) {
      showToast("브라우저가 복사를 허용하지 않았습니다. 화면의 안내를 사용하세요.");
    }
  }

  function applyTheme(theme) {
    state.theme = theme === "dark" ? "dark" : "light";
    document.documentElement.dataset.theme = state.theme;
    localStorage.setItem("classroom.theme", state.theme);
    const toggle = $("theme-toggle");
    if (toggle) {
      toggle.innerHTML = state.theme === "dark" ? "☀ <span>라이트</span>" : "◐ <span>다크</span>";
      toggle.title = state.theme === "dark" ? "라이트 모드로 전환" : "다크 모드로 전환";
    }
    document.querySelectorAll("[data-theme-choice]").forEach((button) => {
      button.classList.toggle("active", button.dataset.themeChoice === state.theme);
      button.setAttribute("aria-pressed", String(button.dataset.themeChoice === state.theme));
    });
  }

  function toggleTheme() {
    applyTheme(state.theme === "dark" ? "light" : "dark");
    showToast(state.theme === "dark" ? "다크 모드를 적용했습니다." : "라이트 모드를 적용했습니다.");
  }

  function lessonNoteKey() {
    return state.classId ? `classroom.lessonNote.${state.classId}` : "";
  }

  function loadLessonNote() {
    const note = $("lesson-note");
    const key = lessonNoteKey();
    if (!note || !key) return;
    note.value = localStorage.getItem(key) || "";
    $("lesson-note-status").textContent = "학급별로 자동 저장됩니다.";
  }

  function formatTime(value) {
    if (!value) return "—";
    return new Date(value).toLocaleString("ko-KR", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
  }

  function showAuth(mode = "login") {
    landingView.hidden = true;
    loginView.hidden = false;
    appView.hidden = true;
    setAuthMode(mode);
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function showLanding() {
    landingView.hidden = false;
    loginView.hidden = true;
    appView.hidden = true;
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function setAuthMode(mode) {
    const signup = mode === "signup";
    document.querySelectorAll(".auth-tab").forEach((button) => {
      const active = button.dataset.authMode === mode;
      button.classList.toggle("active", active);
      button.setAttribute("aria-selected", String(active));
    });
    $("login-panel").hidden = signup;
    $("signup-panel").hidden = !signup;
  }

  function firebaseClient() {
    if (!window.ClassroomFirebaseAuth) {
      throw new Error("Firebase 인증 모듈을 불러오지 못했습니다. 페이지를 새로고침해 주세요.");
    }
    return window.ClassroomFirebaseAuth;
  }

  async function finishFirebaseLogin(credentials) {
    if (!credentials?.idToken) {
      throw new Error("Firebase 인증 결과를 받지 못했습니다. 다시 시도해 주세요.");
    }
    const result = await api("/auth/firebase-login", {
      method: "POST",
      body: { idToken: credentials.idToken }
    });
    state.token = result.accessToken;
    sessionStorage.setItem("classroom.teacherToken", state.token);
    await loadTeacher();
  }

  function setAuthBusy(form, busy) {
    form.querySelectorAll("button").forEach((button) => { button.disabled = busy; });
    form.setAttribute("aria-busy", String(busy));
  }

  function setFirebaseStatus(message, isError = false) {
    const target = $("firebase-status");
    target.textContent = message;
    target.classList.toggle("error", isError);
  }

  $("login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    loginError.hidden = true;
    const form = event.currentTarget;
    setAuthBusy(form, true);
    try {
      const loginName = $("login-name").value.trim();
      const password = $("login-password").value;
      if (loginName.includes("@")) {
        const credentials = await firebaseClient().signInEmail(loginName, password);
        await finishFirebaseLogin(credentials);
      } else {
        const result = await api("/auth/login", { method: "POST", body: { loginName, password } });
        state.token = result.accessToken;
        sessionStorage.setItem("classroom.teacherToken", state.token);
        await loadTeacher();
      }
    } catch (error) {
      loginError.textContent = error.message;
      loginError.hidden = false;
    } finally {
      setAuthBusy(form, false);
    }
  });

  $("signup-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const errorTarget = $("signup-error");
    errorTarget.hidden = true;
    const password = $("signup-password").value;
    if (password !== $("signup-password-confirm").value) {
      errorTarget.textContent = "비밀번호가 일치하지 않습니다.";
      errorTarget.hidden = false;
      return;
    }
    if (password.length < 12) {
      errorTarget.textContent = "비밀번호는 12자 이상이어야 합니다.";
      errorTarget.hidden = false;
      return;
    }
    setAuthBusy(form, true);
    try {
      const credentials = await firebaseClient().signUpEmail(
        $("signup-email").value,
        password,
        $("signup-display-name").value);
      await finishFirebaseLogin(credentials);
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    } finally {
      setAuthBusy(form, false);
    }
  });

  $("google-login-button").addEventListener("click", async () => {
    const button = $("google-login-button");
    const errorTarget = $("login-error");
    errorTarget.hidden = true;
    button.disabled = true;
    try {
      const credentials = await firebaseClient().signInGoogle();
      if (credentials) await finishFirebaseLogin(credentials);
    } catch (error) {
      if (error.code === "auth/popup-closed-by-user" || error.code === "auth/cancelled-popup-request") {
        setFirebaseStatus("Google 로그인을 취소했습니다.");
      } else {
        errorTarget.textContent = error.message;
        errorTarget.hidden = false;
      }
    } finally {
      button.disabled = false;
    }
  });

  $("forgot-password-button").addEventListener("click", async () => {
    const email = $("login-name").value.trim();
    loginError.hidden = true;
    if (!email.includes("@")) {
      loginError.textContent = "비밀번호 재설정은 이메일 계정에만 사용할 수 있습니다.";
      loginError.hidden = false;
      return;
    }
    try {
      await firebaseClient().sendPasswordReset(email);
      showToast("비밀번호 재설정 메일을 보냈습니다.");
    } catch (error) {
      loginError.textContent = error.message;
      loginError.hidden = false;
    }
  });

  document.querySelectorAll(".auth-tab").forEach((button) => {
    button.addEventListener("click", () => setAuthMode(button.dataset.authMode));
  });
  $("landing-login-button").addEventListener("click", () => showAuth("login"));
  $("landing-start-button").addEventListener("click", () => showAuth("signup"));
  $("principles-login-button").addEventListener("click", () => showAuth("login"));
  $("landing-cta-button").addEventListener("click", () => showAuth("signup"));
  $("back-to-landing").addEventListener("click", (event) => { event.preventDefault(); showLanding(); });
  $("theme-toggle").addEventListener("click", toggleTheme);
  document.querySelectorAll("[data-theme-choice]").forEach((button) => {
    button.addEventListener("click", () => applyTheme(button.dataset.themeChoice));
  });

  $("logout-button").addEventListener("click", async () => {
    try { await api("/auth/logout", { method: "POST" }); } catch (_) { /* local logout still clears the token */ }
    try { await window.ClassroomFirebaseAuth?.signOut(); } catch (_) { /* local logout still clears the token */ }
    clearSession();
  });
  $("class-select").addEventListener("change", async (event) => {
    state.classId = event.target.value;
    state.session = null;
    state.selectedDeviceIds.clear();
    loadLessonNote();
    await refreshClass();
  });
  $("start-session-button").addEventListener("click", openSessionDialog);
  $("enroll-button").addEventListener("click", openEnrollmentDialog);
  $("announcement-button").addEventListener("click", () => openCommandDialog("message"));
  $("hero-announcement-button").addEventListener("click", () => openCommandDialog("message"));
  $("quick-message-button").addEventListener("click", () => openCommandDialog("message"));
  $("quick-focus-button").addEventListener("click", () => sendCommand("focusMode", commandTargets(), { message: "수업에 집중해 주세요.", focusEnabled: true }).catch((error) => showToast(error.message)));
  $("quick-url-button").addEventListener("click", () => openCommandDialog("url", commandTargets()));
  $("end-session-button").addEventListener("click", () => endSession().catch((error) => showToast(error.message)));
  $("focus-on-button").addEventListener("click", () => sendCommand("focusMode", commandTargets(), { message: "수업에 집중해 주세요.", focusEnabled: true }).catch((error) => showToast(error.message)));
  $("focus-off-button").addEventListener("click", () => sendCommand("focusMode", commandTargets(), { focusEnabled: false }).catch((error) => showToast(error.message)));
  $("message-button").addEventListener("click", () => openCommandDialog("message", commandTargets()));
  $("url-button").addEventListener("click", () => openCommandDialog("url", commandTargets()));
  $("app-button").addEventListener("click", () => openCommandDialog("app", commandTargets()));
  $("clear-selection-button").addEventListener("click", () => {
    state.selectedDeviceIds.clear();
    renderStudents();
    renderSelection();
  });
  $("student-search").addEventListener("input", (event) => {
    state.search = event.target.value.trim();
    renderStudents();
  });
  $("lesson-note").addEventListener("input", (event) => {
    const key = lessonNoteKey();
    if (!key) return;
    localStorage.setItem(key, event.target.value);
    $("lesson-note-status").textContent = "방금 저장했습니다.";
  });
  $("refresh-audit-button").addEventListener("click", () => loadAudit().catch((error) => showToast(error.message)));
  $("close-detail").addEventListener("click", () => { $("detail-pane").hidden = true; });
  document.querySelectorAll("[data-dialog-cancel]").forEach((button) => {
    button.addEventListener("click", () => {
      const dialog = button.closest("dialog");
      if (dialog?.open) dialog.close("cancel");
    });
  });
  document.querySelectorAll("[data-message-preset]").forEach((button) => {
    button.addEventListener("click", () => { $("command-message").value = button.dataset.messagePreset; });
  });
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
      } else if (state.commandKind === "app") {
        await sendCommand("launchApprovedApp", state.commandTargetIds, { approvedAppId: $("command-app").value });
      } else {
        await sendCommand("message", state.commandTargetIds, { message: $("command-message").value, displaySeconds: Number($("command-seconds").value) });
      }
      $("command-dialog").close();
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });

  $("session-form").addEventListener("submit", async (event) => {
    if (event.submitter?.value === "cancel") return;
    event.preventDefault();
    const errorTarget = $("session-error");
    errorTarget.hidden = true;
    try {
      await startSession($("session-subject").value.trim());
      $("session-dialog").close();
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });

  $("enrollment-form").addEventListener("submit", async (event) => {
    if (event.submitter?.value === "cancel") return;
    event.preventDefault();
    const errorTarget = $("enrollment-error");
    errorTarget.hidden = true;
    try {
      await createEnrollmentBundle();
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });
  $("enrollment-download").addEventListener("click", downloadEnrollmentBundle);
  $("enrollment-copy").addEventListener("click", copyEnrollmentInstructions);

  fetch(apiUrl("/health"), { headers: { Accept: "application/json" } })
    .then((response) => response.ok ? response.json() : null)
    .then((health) => {
      $("dev-login-hint").hidden = !health?.devSchoolId;
      $("security-setting").textContent = apiOrigin
        ? `암호화된 외부 API ${apiOrigin}에 연결됨`
        : "Teacher session bearer token으로 같은 서버에 연결됨";
    })
    .catch(() => { $("dev-login-hint").hidden = true; });

  function refreshFirebaseAvailability() {
    const firebaseReady = window.ClassroomFirebaseAuth?.isConfigured() === true;
    $("google-login-button").disabled = !firebaseReady;
    setFirebaseStatus(firebaseReady
      ? "Google 로그인과 이메일 회원가입을 사용할 수 있습니다."
      : "Google 로그인과 이메일 회원가입은 Firebase 설정 후 사용할 수 있습니다.");
    return firebaseReady;
  }

  applyTheme(state.theme);

  if (!refreshFirebaseAvailability()) {
    let firebaseChecks = 0;
    const firebaseReadinessTimer = window.setInterval(() => {
      firebaseChecks += 1;
      if (refreshFirebaseAvailability() || firebaseChecks >= 30) {
        window.clearInterval(firebaseReadinessTimer);
      }
    }, 250);
  }

  if (state.token) {
    loadTeacher().catch((error) => {
      clearSession();
      showAuth("login");
      loginError.textContent = error.message;
      loginError.hidden = false;
    });
  } else if (window.ClassroomFirebaseAuth?.isConfigured()) {
    window.ClassroomFirebaseAuth.consumeRedirectResult()
      .then((credentials) => credentials ? finishFirebaseLogin(credentials) : null)
      .catch((error) => {
        loginError.textContent = error.message;
        loginError.hidden = false;
        showAuth("login");
      });
  }
})();
