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
    studentCodes: [],
    adminDirectory: null,
    filter: "all",
    search: "",
    selectedDeviceIds: new Set(),
    commandKind: "message",
    commandTargetIds: null,
    pollTimer: null,
    toastTimer: null,
    enrollmentBundle: null,
    theme: localStorage.getItem("classroom.theme") || "light",
    activeSection: "class",
    deferredInstallPrompt: null,
    schoolSearchTimers: new Map(),
    weatherLoaded: false,
    passwordVerificationId: null
  };

  const $ = (id) => document.getElementById(id);
  const landingView = $("landing-view");
  const loginView = $("login-view");
  const appView = $("app-view");
  const loginError = $("login-error");

  function apiUrl(path) {
    return apiOrigin ? `${apiOrigin}${path}` : path;
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
      const error = new Error(payload?.message || `요청에 실패했습니다. (${response.status})`);
      error.code = payload?.code;
      throw error;
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
    state.studentCodes = [];
    state.adminDirectory = null;
    state.selectedDeviceIds.clear();
    state.weatherLoaded = false;
    state.passwordVerificationId = null;
    sessionStorage.removeItem("classroom.teacherToken");
    sessionStorage.removeItem("classroom.onboardingDismissed");
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
    state.classId = state.classId && state.classes.some((item) => item.id === state.classId)
      ? state.classId
      : state.classes[0]?.id || null;
    $("teacher-name").textContent = session.displayName || "교사";
    $("teacher-account").textContent = session.email || session.loginName || "Teacher";
    $("teacher-role").hidden = !session.isAdmin;
    $("admins-nav").hidden = !session.isAdmin;
    $("admin-enroll-button").disabled = !session.isAdmin || !state.classes.length;
    $("admin-enroll-button").title = state.classes.length ? "학생 코드 발급" : "먼저 관리자 메뉴에서 학급을 만들어 주세요";
    $("student-code-permission").textContent = session.isAdmin
      ? "관리자: 코드 발급 및 재발급 가능"
      : "조회 전용 · 코드는 관리자에게 요청하세요";
    const select = $("class-select");
    select.innerHTML = state.classes.length
      ? state.classes.map((item) => `<option value="${item.id}">${escapeHtml(item.name)}</option>`).join("")
      : '<option value="">학급 없음</option>';
    select.value = state.classId || "";
    select.disabled = !state.classes.length;
    $("teacher-greeting").textContent = `${session.displayName || "선생님"}선생님 안녕하세요.`;
    $("school-name").textContent = session.school?.name || "학교를 설정해 주세요";
    $("school-name").classList.toggle("unconfigured", !session.school?.name);
    $("sidebar-school-name").textContent = session.school?.name || "학교를 설정해 주세요";
    $("sidebar-school-name").classList.toggle("unconfigured", !session.school?.name);
    renderAdminClassOptions();
    renderStudentCodeFilters();
    landingView.hidden = true;
    loginView.hidden = true;
    appView.hidden = false;
    applyTheme(state.theme);
    syncProfileControls(session);
    renderTodayInfo();
    loadWeather();
    await refreshClass();
    if (state.pollTimer) clearInterval(state.pollTimer);
    state.pollTimer = setInterval(() => refreshClass().catch((error) => showToast(error.message)), 2000);
    window.setTimeout(() => maybeOpenOnboarding(session), 150);
  }

  async function refreshClass() {
    if (!state.classId) {
      state.session = null;
      state.students = [];
      $("class-subject").textContent = "";
      renderHeader();
      renderStudents();
      renderActivity();
      return;
    }
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
  }

  function renderHeader() {
    const online = state.students.filter((student) => student.online).length;
    const offline = state.students.length - online;
    const needsAttention = state.students.filter(isNeedsAttention).length;
    $("total-count").textContent = String(state.students.length);
    $("metric-online-count").textContent = String(online);
    $("offline-count").textContent = String(offline);
    $("needs-attention-count").textContent = String(needsAttention);
    $("session-caption").textContent = state.session
      ? `${state.session.subject} · ${formatTime(state.session.startedAtUtc)} 시작`
      : "활성 수업이 없습니다.";
    $("start-session-button").hidden = Boolean(state.session) || !state.classId;
    $("end-session-button").hidden = !state.session;
    renderSelection();
  }

  function isNeedsAttention(student) {
    if (!student.online) return false;
    if (student.activityRisk?.level === "warning") return true;
    return !student.activity
      || !student.networkStatus
      || student.networkStatus === "unknown"
      || (student.batteryPercent != null && student.batteryPercent <= 15);
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
      if (state.filter === "attention") return isNeedsAttention(student);
      return true;
    }).filter((student) => !query
      || student.studentDisplayName.toLocaleLowerCase("ko-KR").includes(query));
    if (!filtered.length) {
      if (state.students.length) {
        grid.innerHTML = '<div class="empty-state">현재 필터에 해당하는 학생이 없습니다.</div>';
      } else {
        grid.innerHTML = `<div class="empty-state"><strong>등록된 학생 PC가 없습니다.</strong><p>관리자는 관리자 메뉴에서 학생 코드를 발급하고, 학생은 학생용 설치 앱에 코드를 입력합니다.</p></div>`;
      }
      return;
    }
    grid.innerHTML = filtered.map((student) => {
      const activity = student.activity;
      const statusClass = student.policyApplied ? "focus" : student.online ? "online" : "";
      const statusText = student.policyApplied ? "집중 모드" : student.online ? "온라인" : "오프라인";
      const battery = student.batteryPercent == null ? "배터리 —" : `배터리 ${student.batteryPercent}%`;
      const selected = state.selectedDeviceIds.has(student.deviceId);
      const risk = student.activityRisk;
      return `<article class="student-card${selected ? " selected" : ""}" data-device-id="${student.deviceId}">
        <label class="student-selector" title="명령 대상 선택"><input type="checkbox" aria-label="${escapeHtml(student.studentDisplayName)} 선택" ${selected ? "checked" : ""}></label>
        <div class="student-head"><div><div class="student-name">${escapeHtml(student.studentDisplayName)}</div><div class="student-device">${escapeHtml(student.computerName)}</div></div><span class="status-dot ${statusClass}">${statusText}</span></div>
        <div class="student-activity"><span class="app-icon">▣</span><div><div class="activity-app">${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</div><div class="activity-domain">${escapeHtml(activity?.browserDomain || "현재 도메인 없음")}</div></div></div>
        <div class="student-meta"><span>${student.studentNumber ? `${student.studentNumber}번` : "번호 —"}</span><span>${battery}</span><span>${escapeHtml(student.networkStatus || "unknown")}</span>${student.policyApplied ? '<span class="policy-tag">🔒 집중</span>' : ""}${risk?.level === "warning" ? '<span class="risk-tag">확인 필요</span>' : ""}</div>
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
    const attention = state.students.filter(isNeedsAttention).length;
    $("activity-summary").innerHTML = `<article class="metric-card metric-online"><span class="metric-label">온라인</span><strong>${online}</strong><small>학생 PC</small></article><article class="metric-card metric-offline"><span class="metric-label">오프라인</span><strong>${offline}</strong><small>연결 끊김</small></article><article class="metric-card metric-attention"><span class="metric-label">확인 필요</span><strong>${attention}</strong><small>상태 점검</small></article>`;
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
    const risk = student.activityRisk;
    const riskMarkup = risk?.level === "warning"
      ? `<div class="risk-callout"><strong>확인 필요</strong><span>${escapeHtml(risk.reason || "활동 신호를 확인해 주세요.")}</span></div>`
      : `<div class="privacy-note">학생에게 표시되는 상태 제공기가 보낸 앱·도메인만 표시합니다. 화면 캡처는 수집하지 않습니다.</div>`;
    $("detail-content").innerHTML = `<div class="eyebrow">STUDENT DEVICE</div><h2 class="detail-title">${escapeHtml(student.studentDisplayName)}</h2><div class="detail-status"><span class="status-dot ${student.online ? "online" : ""}">${student.online ? "온라인" : "오프라인"}</span></div>${riskMarkup}<div class="detail-section"><h3>현재 상태</h3><div class="detail-row"><span>학급 / 번호</span><strong>${student.grade ? `${student.grade}학년 ${student.classNumber || ""}반 · ${student.studentNumber || "—"}번` : "학급 정보 없음"}</strong></div><div class="detail-row"><span>컴퓨터</span><strong>${escapeHtml(student.computerName)}</strong></div><div class="detail-row"><span>현재 앱</span><strong>${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</strong></div><div class="detail-row"><span>웹 도메인</span><strong>${escapeHtml(activity?.browserDomain || "도메인 미연결")}</strong></div><div class="detail-row"><span>배터리</span><strong>${student.batteryPercent == null ? "확인 필요" : `${student.batteryPercent}%`}</strong></div><div class="detail-row"><span>네트워크</span><strong>${escapeHtml(student.networkStatus || "unknown")}</strong></div><div class="detail-row"><span>마지막 연결</span><strong>${formatTime(student.lastHeartbeatUtc)}</strong></div><div class="detail-row"><span>정책</span><strong>${student.policyApplied ? "집중 모드" : "일반"}</strong></div></div><div class="detail-section"><h3>장치 식별자</h3><div class="detail-row"><span>Device ID</span><code>${student.deviceId.slice(0, 8)}…</code></div><div class="detail-row"><span>Agent</span><strong>${escapeHtml(student.agentVersion)}</strong></div></div><div class="detail-section stack"><button class="secondary wide" id="detail-message-button">이 학생에게 메시지</button><button class="danger-action wide" id="detail-revoke-button">장치 연결 해제</button></div>`;
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

  async function loadStudentCodes() {
    const list = $("student-codes-list");
    if (!list) return;
    list.innerHTML = '<div class="empty-state">학생 코드를 불러오는 중입니다…</div>';
    const codes = await api("/api/student-codes");
    state.studentCodes = Array.isArray(codes) ? codes : [];
    renderStudentCodeFilters();
    renderStudentCodes();
  }

  function renderStudentCodeFilters() {
    const gradeSelect = $("student-code-grade");
    const classSelect = $("student-code-class");
    if (!gradeSelect || !classSelect) return;
    const previousGrade = gradeSelect.value;
    const previousClass = classSelect.value;
    const grades = [...new Set([
      ...state.classes.map((item) => Number(item.grade || 0)),
      ...state.studentCodes.map((code) => Number(code.grade || 0))
    ].filter(Boolean))].sort((a, b) => a - b);
    gradeSelect.innerHTML = '<option value="">학년 선택</option>' + grades.map((grade) => `<option value="${grade}">${grade}학년</option>`).join("");
    gradeSelect.value = grades.includes(Number(previousGrade)) ? previousGrade : "";
    const selectedGrade = Number(gradeSelect.value || 0);
    const classes = [...new Set([
      ...state.classes.filter((item) => !selectedGrade || Number(item.grade) === selectedGrade).map((item) => Number(item.classNumber || 0)),
      ...state.studentCodes.filter((code) => !selectedGrade || Number(code.grade) === selectedGrade).map((code) => Number(code.classNumber || 0))
    ].filter(Boolean))].sort((a, b) => a - b);
    classSelect.innerHTML = '<option value="">반 선택</option>' + classes.map((classNumber) => `<option value="${classNumber}">${classNumber}반</option>`).join("");
    classSelect.value = classes.includes(Number(previousClass)) ? previousClass : "";
  }

  function renderAdminClassOptions() {
    const optionMarkup = state.classes.map((item) => `<option value="${item.id}">${escapeHtml(item.name)}${item.defaultSubject ? ` · ${escapeHtml(item.defaultSubject)}` : ""}</option>`).join("");
    [$("roster-class-select"), $("enrollment-class-id")].forEach((select) => {
      if (!select) return;
      const previous = select.value || state.classId || "";
      select.innerHTML = optionMarkup;
      if (state.classes.some((item) => item.id === previous)) select.value = previous;
    });
  }

  function renderStudentCodes() {
    const list = $("student-codes-list");
    if (!list) return;
    const query = $("student-code-search")?.value.trim().toLocaleLowerCase("ko-KR") || "";
    const selectedGrade = Number($("student-code-grade")?.value || 0);
    const selectedClass = Number($("student-code-class")?.value || 0);
    if (!selectedGrade || !selectedClass) {
      list.innerHTML = '<div class="empty-state"><strong>학년과 반을 선택해 주세요.</strong><p>선택한 반의 학생 코드만 표시됩니다.</p></div>';
      return;
    }
    const filtered = state.studentCodes.filter((code) =>
      Number(code.grade || 0) === selectedGrade
      && Number(code.classNumber || 0) === selectedClass
      && (!query || String(code.studentDisplayName || "").toLocaleLowerCase("ko-KR").includes(query)));
    if (!filtered.length) {
      list.innerHTML = state.studentCodes.length
        ? '<div class="empty-state">검색 조건에 맞는 학생 코드가 없습니다.</div>'
        : '<div class="empty-state"><strong>아직 발급된 학생 코드가 없습니다.</strong><p>관리자가 수업 화면에서 학생을 등록하면 이곳에 표시됩니다.</p></div>';
      return;
    }

    const groups = new Map();
    filtered.forEach((code) => {
      const key = code.classId;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(code);
    });
    list.innerHTML = [...groups.values()].map((group) => {
      const first = group[0];
      group.sort((left, right) => (Number(left.studentNumber || 999) - Number(right.studentNumber || 999)) || String(left.studentDisplayName).localeCompare(String(right.studentDisplayName), "ko"));
      return `<section class="student-code-group"><div class="student-code-group-heading"><div><span class="eyebrow">CLASS</span><h3>${escapeHtml(first.className)}</h3></div><span class="class-subject-small">${escapeHtml(first.subject || "정보")} · ${group.length}명</span></div><div class="student-code-grid">${group.map((code) => `<article class="student-code-card"><div class="student-code-student"><span class="student-number">${code.studentNumber || "—"}</span><span class="avatar">${escapeHtml(code.studentDisplayName.slice(0, 1))}</span><div><strong>${escapeHtml(code.studentDisplayName)}</strong><small>${escapeHtml(code.createdByDisplayName || "관리자")} 발급 · ${formatTime(code.createdAtUtc)}</small></div></div><div class="student-code-value"><code>${escapeHtml(code.joinCode)}</code><button class="secondary code-copy-button" type="button" data-code-copy="${escapeHtml(code.joinCode)}">복사</button></div><div class="student-code-meta"><span>${code.lastUsedAtUtc ? `최근 사용 ${formatTime(code.lastUsedAtUtc)}` : "아직 사용하지 않음"}</span>${state.teacher?.isAdmin ? `<button class="ghost-button code-reissue-button" type="button" data-code-reissue="${escapeHtml(code.studentId)}">새 코드 발급</button>` : ""}</div></article>`).join("")}</div></section>`;
    }).join("");

    list.querySelectorAll("[data-code-copy]").forEach((button) => {
      button.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(button.dataset.codeCopy || "");
          showToast("학생 코드를 복사했습니다.");
        } catch (_) {
          showToast("브라우저가 복사를 허용하지 않았습니다.");
        }
      });
    });
    list.querySelectorAll("[data-code-reissue]").forEach((button) => {
      button.addEventListener("click", () => {
        const code = state.studentCodes.find((item) => item.studentId === button.dataset.codeReissue);
        if (code) reissueStudentCode(code).catch((error) => showToast(error.message));
      });
    });
  }

  async function reissueStudentCode(code) {
    if (!state.teacher?.isAdmin) {
      throw new Error("학생 코드는 관리자만 재발급할 수 있습니다.");
    }
    if (!confirm(`${code.studentDisplayName} 학생의 기존 코드를 폐기하고 새 코드를 발급할까요?`)) return;
    const ticket = await api(`/api/classes/${code.classId}/enrollment-tickets`, {
      method: "POST",
      body: { studentId: code.studentId, studentDisplayName: code.studentDisplayName, studentNumber: code.studentNumber }
    });
    showToast(`${code.studentDisplayName} 학생의 새 코드를 발급했습니다: ${ticket.joinCode}`);
    await loadStudentCodes();
  }

  async function loadAdminDirectory() {
    if (!state.teacher?.isAdmin) return;
    const list = $("admin-list");
    if (list) list.innerHTML = '<div class="empty-state">관리자 목록을 불러오는 중입니다…</div>';
    state.adminDirectory = await api("/api/admin/teachers");
    renderAdminDirectory();
  }

  function renderAdminDirectory() {
    const list = $("admin-list");
    if (!list || !state.adminDirectory) return;
    const teachers = Array.isArray(state.adminDirectory.teachers) ? state.adminDirectory.teachers : [];
    const grants = Array.isArray(state.adminDirectory.grants) ? state.adminDirectory.grants : [];
    const known = new Set(teachers.map((teacher) => [teacher.email, teacher.loginName].filter(Boolean).map((value) => value.toLowerCase())) .flat());
    const pending = grants.filter((grant) => !known.has(String(grant.identifier || "").toLowerCase()));
    const teacherRows = teachers.map((teacher) => {
      const identifier = teacher.email || teacher.loginName;
      const canRemove = teacher.isAdmin && teacher.teacherId !== state.teacher?.teacherId;
      return `<div class="admin-row"><div><strong>${escapeHtml(teacher.displayName)}</strong><small>${escapeHtml(teacher.email || teacher.loginName)}</small></div><span class="admin-badge ${teacher.isAdmin ? "active" : ""}">${teacher.isAdmin ? "관리자" : "선생님"}</span>${canRemove ? `<button class="ghost-button admin-remove-button" type="button" data-admin-remove="${escapeHtml(identifier)}">해제</button>` : ""}</div>`;
    }).join("");
    const pendingRows = pending.map((grant) => `<div class="admin-row pending"><div><strong>${escapeHtml(grant.identifier)}</strong><small>아직 가입하지 않은 계정 · ${formatTime(grant.createdAtUtc)}</small></div><span class="admin-badge active">가입 시 관리자</span><button class="ghost-button admin-remove-button" type="button" data-admin-remove="${escapeHtml(grant.identifier)}">해제</button></div>`).join("");
    list.innerHTML = `<div class="admin-list-heading"><strong>학교 계정</strong><span class="muted small">${teachers.length}명</span></div>${teacherRows || '<div class="empty-state">아직 등록된 선생님이 없습니다.</div>'}${pendingRows ? `<div class="admin-list-heading pending-heading"><strong>가입 대기 권한</strong></div>${pendingRows}` : ""}`;
    list.querySelectorAll("[data-admin-remove]").forEach((button) => {
      button.addEventListener("click", () => updateAdminAccess(button.dataset.adminRemove, false).catch((error) => showToast(error.message)));
    });
  }

  async function updateAdminAccess(identifier, isAdmin) {
    if (!identifier) return;
    if (!isAdmin && !confirm(`${identifier} 계정의 관리자 권한을 해제할까요?`)) return;
    await api("/api/admin/teachers", { method: "POST", body: { identifier, isAdmin } });
    showToast(isAdmin ? "관리자 권한을 부여했습니다." : "관리자 권한을 해제했습니다.");
    await loadAdminDirectory();
  }

  async function createClassFromAdmin() {
    const grade = Number($("admin-class-grade").value);
    const classNumber = Number($("admin-class-number").value);
    const subject = $("admin-class-subject").value.trim();
    const result = $("class-error");
    result.hidden = true;
    try {
      await api("/api/admin/classes", { method: "POST", body: { grade, classNumber, subject } });
      showToast(`${grade}학년 ${classNumber}반을 저장했습니다.`);
      await loadTeacher();
    } catch (error) {
      result.textContent = error.message;
      result.hidden = false;
    }
  }

  function parseDelimitedLine(line, delimiter) {
    const values = [];
    let value = "";
    let quoted = false;
    for (let index = 0; index < line.length; index += 1) {
      const character = line[index];
      if (character === '"' && line[index + 1] === '"' && quoted) {
        value += '"';
        index += 1;
      } else if (character === '"') {
        quoted = !quoted;
      } else if (character === delimiter && !quoted) {
        values.push(value.trim());
        value = "";
      } else {
        value += character;
      }
    }
    values.push(value.trim());
    return values;
  }

  function rosterRowsFromMatrix(matrix) {
    const rows = matrix.map((row) => row.map((value) => String(value ?? "").trim())).filter((row) => row.some(Boolean));
    if (!rows.length) return [];
    const headerIndex = rows.findIndex((row) => row.join(" ").match(/번호|학번|이름|성명|학생/i));
    let numberIndex = -1;
    let nameIndex = -1;
    const dataStart = headerIndex >= 0 ? headerIndex + 1 : 0;
    if (headerIndex >= 0) {
      const headers = rows[headerIndex].map((value) => value.replace(/\s/g, "").toLowerCase());
      numberIndex = headers.findIndex((value) => /번호|학번/.test(value));
      nameIndex = headers.findIndex((value) => /이름|성명|학생명/.test(value));
    }
    return rows.slice(dataStart).map((row) => {
      const numeric = numberIndex >= 0
        ? row[numberIndex]
        : [...row].reverse().find((value) => /^\d{1,3}$/.test(value));
      const name = nameIndex >= 0
        ? row[nameIndex]
        : row.filter((value) => value && value !== numeric && !/^\d{1,3}$/.test(value)).pop();
      const studentNumber = Number(String(numeric || "").replace(/[^0-9]/g, ""));
      return { studentNumber, studentDisplayName: name || "" };
    }).filter((row) => row.studentNumber >= 1 && row.studentNumber <= 99 && row.studentDisplayName);
  }

  async function unzipXlsx(arrayBuffer) {
    const bytes = new Uint8Array(arrayBuffer);
    const view = new DataView(arrayBuffer);
    let end = -1;
    for (let index = bytes.length - 22; index >= Math.max(0, bytes.length - 65557); index -= 1) {
      if (view.getUint32(index, true) === 0x06054b50) {
        end = index;
        break;
      }
    }
    if (end < 0) throw new Error("XLSX 압축 구조를 읽지 못했습니다.");
    const entryCount = view.getUint16(end + 10, true);
    const centralOffset = view.getUint32(end + 16, true);
    let cursor = centralOffset;
    const files = new Map();
    const decoder = new TextDecoder();
    for (let entry = 0; entry < entryCount; entry += 1) {
      if (view.getUint32(cursor, true) !== 0x02014b50) throw new Error("XLSX 파일 형식이 올바르지 않습니다.");
      const compression = view.getUint16(cursor + 10, true);
      const compressedSize = view.getUint32(cursor + 20, true);
      const nameLength = view.getUint16(cursor + 28, true);
      const extraLength = view.getUint16(cursor + 30, true);
      const commentLength = view.getUint16(cursor + 32, true);
      const localOffset = view.getUint32(cursor + 42, true);
      const name = decoder.decode(bytes.slice(cursor + 46, cursor + 46 + nameLength));
      const localNameLength = view.getUint16(localOffset + 26, true);
      const localExtraLength = view.getUint16(localOffset + 28, true);
      const compressed = bytes.slice(localOffset + 30 + localNameLength + localExtraLength, localOffset + 30 + localNameLength + localExtraLength + compressedSize);
      let content;
      if (compression === 0) content = compressed;
      else if (compression === 8 && window.DecompressionStream) {
        const stream = new Blob([compressed]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
        content = new Uint8Array(await new Response(stream).arrayBuffer());
      } else throw new Error("이 브라우저는 압축된 XLSX 명단을 읽을 수 없습니다. CSV로 저장해 다시 올려 주세요.");
      files.set(name, content);
      cursor += 46 + nameLength + extraLength + commentLength;
    }
    return files;
  }

  async function parseXlsxFile(file) {
    const files = await unzipXlsx(await file.arrayBuffer());
    const decoder = new TextDecoder();
    const xmlParser = new DOMParser();
    const sharedStrings = files.has("xl/sharedStrings.xml")
      ? [...xmlParser.parseFromString(decoder.decode(files.get("xl/sharedStrings.xml")), "application/xml").querySelectorAll("si")].map((node) => [...node.querySelectorAll("t")].map((item) => item.textContent || "").join(""))
      : [];
    const sheetBytes = files.get("xl/worksheets/sheet1.xml");
    if (!sheetBytes) throw new Error("첫 번째 시트를 찾지 못했습니다.");
    const sheet = xmlParser.parseFromString(decoder.decode(sheetBytes), "application/xml");
    const matrix = [];
    for (const rowNode of sheet.querySelectorAll("sheetData > row")) {
      const row = [];
      for (const cell of rowNode.querySelectorAll(":scope > c")) {
        const reference = cell.getAttribute("r") || "A1";
        const column = reference.match(/[A-Z]+/i)?.[0] || "A";
        let columnIndex = 0;
        for (const character of column.toUpperCase()) columnIndex = columnIndex * 26 + character.charCodeAt(0) - 64;
        columnIndex -= 1;
        const type = cell.getAttribute("t");
        const valueNode = cell.querySelector("v");
        let value = valueNode?.textContent || "";
        if (type === "s") value = sharedStrings[Number(value)] || "";
        if (type === "inlineStr") value = [...cell.querySelectorAll("t")].map((item) => item.textContent || "").join("");
        row[columnIndex] = value;
      }
      matrix.push(row.map((value) => value || ""));
    }
    return rosterRowsFromMatrix(matrix);
  }

  async function parseRosterFile(file) {
    if (/\.xlsx?$/i.test(file.name)) return parseXlsxFile(file);
    const text = await file.text();
    const lines = text.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    if (!lines.length) return [];
    const delimiter = lines.some((line) => line.includes("\t")) ? "\t" : lines.some((line) => line.includes(",")) ? "," : " ";
    const matrix = delimiter === " " ? lines.map((line) => {
      const match = line.match(/^\s*(\d{1,3})\s+(.+?)\s*$/);
      return match ? [match[1], match[2]] : [line];
    }) : lines.map((line) => parseDelimitedLine(line, delimiter));
    return rosterRowsFromMatrix(matrix);
  }

  async function importRoster() {
    const file = $("roster-file").files?.[0];
    const classId = $("roster-class-select").value;
    const message = $("roster-import-message");
    if (!file || !classId) {
      message.textContent = "대상 반과 명단 파일을 선택해 주세요.";
      return;
    }
    message.textContent = "명단을 읽는 중입니다…";
    try {
      const students = (await parseRosterFile(file)).slice(0, 100);
      if (!students.length) throw new Error("번호와 이름이 포함된 명단을 찾지 못했습니다.");
      const result = await api("/api/admin/student-codes/import", { method: "POST", body: { classId, students } });
      message.textContent = `${result.imported}명의 코드를 준비했습니다${result.skipped ? ` · ${result.skipped}행 건너뜀` : ""}.`;
      showToast(`${result.imported}명 학생 코드가 준비되었습니다.`);
      $("roster-import-form").reset();
      await loadStudentCodes();
    } catch (error) {
      message.textContent = error.message;
    }
  }

  function downloadStudentInstaller() {
    const url = runtimeConfig.studentInstallerUrl || "https://github.com/blossom0948/classroom/releases/latest/download/Classroom-Windows-x64.zip";
    window.open(url, "_blank", "noopener,noreferrer");
    showToast("학생용 설치 패키지 다운로드를 시작했습니다.");
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
    if (!state.teacher?.isAdmin) {
      showToast("학생 코드 발급은 관리자 메뉴에서만 가능합니다.");
      return;
    }
    state.enrollmentBundle = null;
    $("enrollment-form").reset();
    $("enrollment-fields").hidden = false;
    $("enrollment-result").hidden = true;
    $("enrollment-create").hidden = false;
    $("enrollment-cancel").hidden = false;
    $("enrollment-copy").hidden = true;
    $("enrollment-done").hidden = true;
    $("enrollment-code").textContent = "--------";
    $("enrollment-class-id").value = state.classId || "";
    $("enrollment-class-label").textContent = "학생이 소속될 학급과 이름을 입력하세요.";
    $("enrollment-error").hidden = true;
    $("enrollment-dialog").showModal();
  }

  async function createEnrollmentBundle() {
    const displayName = $("enrollment-name").value.trim();
    if (!displayName) throw new Error("학생 이름을 입력해 주세요.");
    const classId = $("enrollment-class-id").value;
    if (!classId) throw new Error("학생이 소속될 반을 선택해 주세요.");
    const studentId = null;
    const ticket = await api(`/api/classes/${classId}/enrollment-tickets`, {
      method: "POST",
      body: { studentId, studentDisplayName: displayName, studentNumber: Number($("enrollment-number").value) || null }
    });
    if (!ticket.joinCode) {
      throw new Error("학생 코드를 만들지 못했습니다. 서버를 최신 버전으로 배포한 뒤 다시 시도해 주세요.");
    }
    state.enrollmentBundle = {
      joinCode: ticket.joinCode,
      studentDisplayName: displayName,
      expiresAtUtc: ticket.expiresAtUtc
    };
    $("enrollment-result-name").textContent = `${displayName} 학생 코드를 만들었습니다.`;
    $("enrollment-expiry").textContent = "관리자가 새 코드를 발급하기 전까지 계속 유효합니다.";
    $("enrollment-code").textContent = ticket.joinCode;
    $("enrollment-command").textContent = "학생용 설치 파일: Classroom.Student.Setup.exe";
    $("enrollment-fields").hidden = true;
    $("enrollment-result").hidden = false;
    $("enrollment-create").hidden = true;
    $("enrollment-cancel").hidden = true;
    $("enrollment-copy").hidden = false;
    $("enrollment-done").hidden = false;
  }

  async function copyEnrollmentInstructions() {
    if (!state.enrollmentBundle) return;
    const text = `Classroom 학생 PC 등록\n학생: ${state.enrollmentBundle.studentDisplayName}\n학생 코드: ${state.enrollmentBundle.joinCode}\n1. 학생용 패키지 압축을 풉니다.\n2. Classroom.Student.Setup.exe를 실행합니다.\n3. 학생 코드 ${state.enrollmentBundle.joinCode}를 입력하고 관리자 권한을 승인합니다.`;
    try {
      await navigator.clipboard.writeText(text);
      showToast("학생 설치 안내를 복사했습니다.");
    } catch (_) {
      showToast("브라우저가 복사를 허용하지 않았습니다. 화면의 안내를 사용하세요.");
    }
  }

  async function copyEnrollmentCode() {
    if (!state.enrollmentBundle) return;
    try {
      await navigator.clipboard.writeText(state.enrollmentBundle.joinCode);
      showToast("학생 코드를 복사했습니다.");
    } catch (_) {
      showToast("브라우저가 복사를 허용하지 않았습니다. 화면의 코드를 사용하세요.");
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

  function formatTime(value) {
    if (!value) return "—";
    return new Date(value).toLocaleString("ko-KR", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  function renderTodayInfo() {
    const now = new Date();
    const date = $("current-date");
    if (date) date.textContent = now.toLocaleDateString("ko-KR", { month: "long", day: "numeric", weekday: "short" });
  }

  async function loadWeather() {
    const target = $("weather-info");
    if (!target || state.weatherLoaded) return;
    state.weatherLoaded = true;
    const load = async (latitude, longitude) => {
      const response = await fetch(`https://api.open-meteo.com/v1/forecast?latitude=${latitude}&longitude=${longitude}&current=temperature_2m,weather_code&timezone=auto`);
      if (!response.ok) throw new Error("weather");
      const payload = await response.json();
      const current = payload.current;
      const code = Number(current?.weather_code);
      const description = code === 0 ? "맑음" : code <= 3 ? "구름" : code <= 67 ? "비" : code <= 77 ? "눈" : "날씨 변동";
      target.textContent = `현재 위치 · ${description} ${Math.round(Number(current?.temperature_2m))}°C`;
    };
    if (!navigator.geolocation) {
      target.textContent = "날씨 · 위치 권한 필요";
      return;
    }
    navigator.geolocation.getCurrentPosition(
      (position) => load(position.coords.latitude, position.coords.longitude).catch(() => { target.textContent = "날씨를 확인할 수 없습니다"; }),
      () => { target.textContent = "날씨 · 위치 권한 필요"; },
      { enableHighAccuracy: false, maximumAge: 15 * 60 * 1000, timeout: 5000 }
    );
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
    $("login-error").hidden = true;
    $("signup-error").hidden = true;
  }

  const legalDocuments = {
    terms: {
      kicker: "TERMS OF SERVICE",
      title: "Classroom 이용약관",
      html: `<p>시행일: 2026년 8월 30일</p><h3>1. 서비스 목적</h3><p>Classroom은 학교 수업에서 학생 PC의 연결 상태를 확인하고, 수업 안내·집중 모드·승인된 링크 및 앱 실행을 전달하기 위한 교사용 운영 도구입니다.</p><h3>2. 계정과 권한</h3><p>교사 계정은 본인만 사용해야 하며, 관리자는 학교 운영에 필요한 범위에서 다른 교사의 관리자 권한을 지정하거나 해제할 수 있습니다. 학생 코드는 학생 PC 등록 목적으로만 사용해야 하며, 노출된 코드는 즉시 재발급해야 합니다.</p><h3>3. 허용되는 기능 범위</h3><p>서비스는 수업 운영에 필요한 상태 확인과 명령 전달만 제공합니다. 화면 캡처, 임의 원격 셸 실행, 개인 파일 열람 기능은 제공하지 않습니다.</p><h3>4. 학교의 책임</h3><p>학교·교육기관은 학생과 보호자에게 서비스 사용 사실, 관리 범위, 자체 운영 기준을 알리고 필요한 동의 절차를 갖추어야 합니다.</p><h3>5. 이용 제한</h3><p>타인의 계정을 사용하거나, 학생의 교육 목적과 무관한 감시·통제에 서비스를 이용해서는 안 됩니다. 보안상 우려가 있는 이용은 제한될 수 있습니다.</p>`
    },
    privacy: {
      kicker: "PRIVACY NOTICE",
      title: "개인정보처리방침",
      html: `<p>시행일: 2026년 8월 30일</p><h3>1. 수집하는 정보</h3><p>교사 계정의 이메일·이름·담당 과목, 학급명, 학생 표시 이름, 학생 PC 이름·연결 시각·앱 이름·제한된 웹 도메인·배터리·네트워크 상태, 수업 명령 및 감사 기록을 처리합니다.</p><h3>2. 이용 목적</h3><p>교사 인증, 학급 운영, 학생 PC 등록, 수업 안내 전달, 연결 상태 확인, 보안 감사 및 장애 대응에만 사용합니다.</p><h3>3. 보관 기간</h3><p>교사·학생·수업 데이터는 학교 관리자가 삭제하거나 서비스 운영 목적이 종료될 때까지 보관합니다. 세부 보관 기간은 학교의 정보보호·기록 관리 규정에 맞춰 운영해야 합니다.</p><h3>4. 안전성</h3><p>인증 토큰은 서버에 해시 형태로 보관하며, 전송은 HTTPS/WSS로 보호합니다. 서비스는 화면 캡처와 개인 파일을 수집하지 않습니다.</p><h3>5. 이용자 권리와 문의</h3><p>정보 주체는 학교 관리자에게 열람·정정·삭제 요청을 할 수 있습니다. 실제 학교 도입 전에는 해당 학교의 개인정보 보호책임자와 연락처를 별도로 고지해야 합니다.</p>`
    }
  };

  function openLegalDocument(kind) {
    const documentInfo = legalDocuments[kind] || legalDocuments.terms;
    $("legal-kicker").textContent = documentInfo.kicker;
    $("legal-title").textContent = documentInfo.title;
    $("legal-content").innerHTML = documentInfo.html;
    const dialog = $("legal-dialog");
    if (!dialog.open) dialog.showModal();
  }

  function setupSchoolSearch(inputId, resultsId, hiddenId) {
    const input = $(inputId);
    const results = $(resultsId);
    const hidden = $(hiddenId);
    if (!input || !results || !hidden || input.dataset.bound === "1") return;
    input.dataset.bound = "1";
    input.addEventListener("input", () => {
      hidden.value = "";
      const query = input.value.trim();
      const previous = state.schoolSearchTimers.get(inputId);
      if (previous) window.clearTimeout(previous);
      if (query.length < 2) {
        results.hidden = true;
        results.innerHTML = "";
        return;
      }
      const timer = window.setTimeout(async () => {
        results.hidden = false;
        results.innerHTML = '<div class="school-search-status">학교를 검색하는 중입니다…</div>';
        try {
          const schools = await api(`/api/schools/search?q=${encodeURIComponent(query)}`);
          if (!Array.isArray(schools) || !schools.length) {
            results.innerHTML = '<div class="school-search-status">검색 결과가 없습니다. 학교 이름을 더 정확히 입력해 주세요.</div>';
            return;
          }
          results.innerHTML = schools.map((school) => `<button type="button" class="school-result" data-school-id="${escapeHtml(school.id)}" data-school-name="${escapeHtml(school.name)}"><strong>${escapeHtml(school.name)}</strong><small>${escapeHtml(school.address || school.schoolType || "")}</small></button>`).join("");
          results.querySelectorAll("[data-school-id]").forEach((button) => button.addEventListener("click", () => {
            hidden.value = button.dataset.schoolId || "";
            input.value = button.dataset.schoolName || "";
            results.hidden = true;
          }));
        } catch (error) {
          results.innerHTML = `<div class="school-search-status error">${escapeHtml(error.message)}</div>`;
        }
      }, 280);
      state.schoolSearchTimers.set(inputId, timer);
    });
  }

  function setSchoolControls(inputId, resultsId, hiddenId, school) {
    const input = $(inputId);
    const hidden = $(hiddenId);
    if (input) input.value = school?.name || "";
    if (hidden) hidden.value = school?.id || "";
    const results = $(resultsId);
    if (results) results.hidden = true;
  }

  function syncProfileControls(session) {
    $("profile-display-name").value = session.displayName || "";
    $("profile-subject").value = session.subject || "";
    setSchoolControls("profile-school-search", "profile-school-results", "profile-school-id", session.school);
    setupSchoolSearch("profile-school-search", "profile-school-results", "profile-school-id");
    const consentRequired = !session.legalAccepted;
    $("profile-consent-fields").hidden = !consentRequired;
    $("profile-terms").checked = false;
    $("profile-privacy").checked = false;
    const hasPassword = session.hasPassword === true;
    $("password-card-title").textContent = hasPassword ? "교사 비밀번호" : "교사 아이디 로그인 비밀번호";
    $("password-card-copy").textContent = hasPassword
      ? "이메일 확인 후 비밀번호를 변경할 수 있습니다."
      : "이메일 확인 후 교사 아이디로 로그인할 비밀번호를 설정합니다.";
    $("password-open-button").textContent = hasPassword ? "비밀번호 변경" : "비밀번호 설정";
    $("password-setting-status").textContent = session.email ? `확인 메일: ${session.email}` : "확인 가능한 이메일이 없습니다.";
  }

  function openPasswordDialog() {
    if (!state.teacher?.email) {
      showToast("확인 메일을 받을 이메일이 계정에 없습니다.");
      return;
    }
    state.passwordVerificationId = null;
    $("password-dialog-copy").textContent = `${state.teacher.email}로 6자리 확인 코드를 보내고, 코드를 확인한 뒤 비밀번호를 저장합니다.`;
    $("password-form").reset();
    $("password-verification-field").hidden = true;
    $("verify-password-code").hidden = true;
    $("change-password-submit").disabled = true;
    $("password-verification-status").textContent = "";
    $("password-message").hidden = true;
    $("password-dialog").showModal();
  }

  async function sendPasswordVerification() {
    const button = $("send-password-verification");
    const status = $("password-verification-status");
    const message = $("password-message");
    message.hidden = true;
    button.disabled = true;
    try {
      const result = await api("/auth/password-verification/start", { method: "POST", body: {} });
      state.passwordVerificationId = result.verificationId;
      $("password-verification-field").hidden = false;
      $("verify-password-code").hidden = false;
      status.textContent = `${result.email || "이메일"}로 확인 코드를 보냈습니다.`;
    } catch (error) {
      message.textContent = error.code === "VERIFICATION_EMAIL_NOT_CONFIGURED"
        ? "이메일 발송 설정이 아직 완료되지 않았습니다. 관리자에게 Classroom 메일 발송 설정을 요청해 주세요."
        : error.message;
      message.hidden = false;
    } finally {
      button.disabled = false;
    }
  }

  async function verifyPasswordCode() {
    const message = $("password-message");
    message.hidden = true;
    if (!state.passwordVerificationId) {
      message.textContent = "먼저 확인 메일을 보내 주세요.";
      message.hidden = false;
      return;
    }
    try {
      await api("/auth/password-verification/verify", {
        method: "POST",
        body: { verificationId: state.passwordVerificationId, code: $("password-verification-code").value.trim() }
      });
      $("change-password-submit").disabled = false;
      $("password-verification-status").textContent = "이메일 확인이 완료되었습니다.";
      showToast("이메일 확인이 완료되었습니다.");
    } catch (error) {
      message.textContent = error.message;
      message.hidden = false;
    }
  }

  function maybeOpenOnboarding(session) {
    if ((session.profileCompleted && session.schoolSelected) || !appView || appView.hidden) return;
    $("onboarding-display-name").value = session.displayName === "새 선생님" || session.displayName === "선생님" ? "" : session.displayName || "";
    $("onboarding-subject").value = session.subject || "";
    setSchoolControls("onboarding-school-search", "onboarding-school-results", "onboarding-school-id", session.school?.name === "Classroom 학교" ? null : session.school);
    setupSchoolSearch("onboarding-school-search", "onboarding-school-results", "onboarding-school-id");
    const consentRequired = !session.legalAccepted;
    $("onboarding-consent").hidden = !consentRequired;
    $("onboarding-terms").checked = false;
    $("onboarding-privacy").checked = false;
    const dialog = $("onboarding-dialog");
    if (!dialog.open) dialog.showModal();
  }

  async function saveProfile(values) {
    const result = await api("/auth/profile", { method: "PUT", body: values });
    state.teacher = result;
    state.classes = result.classes || [];
    sessionStorage.removeItem("classroom.onboardingDismissed");
    await loadTeacher();
  }

  function syncInstallUi() {
    const available = Boolean(state.deferredInstallPrompt);
    $("landing-install-button").hidden = !available;
    $("settings-install-button").hidden = !available;
    $("install-app-prompt").hidden = !available || localStorage.getItem("classroom.dismissInstallPrompt") === "1";
  }

  async function installApp() {
    const deferred = state.deferredInstallPrompt;
    if (!deferred) return;
    deferred.prompt();
    const choice = await deferred.userChoice.catch(() => ({ outcome: "dismissed" }));
    state.deferredInstallPrompt = null;
    syncInstallUi();
    if (choice.outcome === "accepted") showToast("Classroom 앱 설치를 시작했습니다.");
  }

  function registerPwa() {
    if ("serviceWorker" in navigator) {
      navigator.serviceWorker.register("/sw.js", { updateViaCache: "none" }).catch(() => {});
    }
    window.addEventListener("beforeinstallprompt", (event) => {
      event.preventDefault();
      state.deferredInstallPrompt = event;
      syncInstallUi();
    });
    window.addEventListener("appinstalled", () => {
      state.deferredInstallPrompt = null;
      localStorage.removeItem("classroom.dismissInstallPrompt");
      syncInstallUi();
      showToast("Classroom 앱이 설치되었습니다.");
    });
  }

  function firebaseClient() {
    if (!window.ClassroomFirebaseAuth) {
      throw new Error("Firebase 인증 모듈을 불러오지 못했습니다. 페이지를 새로고침해 주세요.");
    }
    return window.ClassroomFirebaseAuth;
  }

  async function finishFirebaseLogin(credentials, profile = {}) {
    if (!credentials?.idToken) {
      throw new Error("Firebase 인증 결과를 받지 못했습니다. 다시 시도해 주세요.");
    }
    const result = await api("/auth/firebase-login", {
      method: "POST",
      body: {
        idToken: credentials.idToken,
        displayName: String(profile.displayName || "").trim(),
        subject: String(profile.subject || "").trim(),
        termsAccepted: profile.termsAccepted === true,
        privacyAccepted: profile.privacyAccepted === true
      }
    });
    sessionStorage.removeItem("classroom.pendingFirebaseProfile");
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
    const email = $("signup-email").value.trim();
    const password = $("signup-password").value;
    if (!email || !email.includes("@")) {
      errorTarget.textContent = "학교 이메일 주소를 입력해 주세요.";
      errorTarget.hidden = false;
      return;
    }
    if (password !== $("signup-password-confirm").value) {
      errorTarget.textContent = "비밀번호가 일치하지 않습니다.";
      errorTarget.hidden = false;
      return;
    }
    if (password.length < 6) {
      errorTarget.textContent = "비밀번호는 6자 이상이어야 합니다.";
      errorTarget.hidden = false;
      return;
    }
    if (!$("signup-terms").checked || !$("signup-privacy").checked) {
      errorTarget.textContent = "이용약관과 개인정보처리방침 동의가 필요합니다.";
      errorTarget.hidden = false;
      return;
    }
    setAuthBusy(form, true);
    try {
      const credentials = await firebaseClient().signUpEmail(
        email,
        password,
        "");
      await finishFirebaseLogin(credentials, {
        termsAccepted: true,
        privacyAccepted: true
      });
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    } finally {
      setAuthBusy(form, false);
    }
  });

  $("google-login-button").addEventListener("click", async () => {
    const button = $("google-login-button");
    const signupMode = !$("signup-panel").hidden;
    const errorTarget = signupMode ? $("signup-error") : $("login-error");
    errorTarget.hidden = true;
    button.disabled = true;
    try {
      const signupProfile = !$("signup-panel").hidden
        ? { termsAccepted: $("signup-terms").checked, privacyAccepted: $("signup-privacy").checked }
        : {};
      if (!$("signup-panel").hidden && (!signupProfile.termsAccepted || !signupProfile.privacyAccepted)) {
        throw new Error("이용약관과 개인정보처리방침 동의가 필요합니다.");
      }
      if (signupProfile.termsAccepted || signupProfile.privacyAccepted) {
        sessionStorage.setItem("classroom.pendingFirebaseProfile", JSON.stringify(signupProfile));
      } else {
        sessionStorage.removeItem("classroom.pendingFirebaseProfile");
      }
      const credentials = await firebaseClient().signInGoogle();
      if (credentials) await finishFirebaseLogin(credentials, signupProfile);
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

  $("account-lookup-button").addEventListener("click", () => {
    $("account-lookup-form").reset();
    $("account-lookup-result").hidden = true;
    $("account-lookup-dialog").showModal();
  });

  $("account-lookup-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const email = $("account-lookup-email").value.trim().toLowerCase();
    const result = $("account-lookup-result");
    if (!email || !email.includes("@")) {
      result.textContent = "가입 이메일을 입력해 주세요.";
      result.hidden = false;
      return;
    }
    try {
      const methods = await firebaseClient().lookupAccount(email);
      if (email === "blossom0948@gmail.com") {
        result.textContent = "관리자 계정입니다. Google 로그인 또는 교사 아이디 blossom0948로 로그인하세요.";
      } else if (methods.includes("google.com") && methods.includes("password")) {
        result.textContent = "이 이메일은 Google 로그인과 이메일·비밀번호 로그인을 사용할 수 있습니다.";
      } else if (methods.includes("google.com")) {
        result.textContent = "이 이메일은 Google 로그인으로 가입되어 있습니다.";
      } else if (methods.includes("password")) {
        result.textContent = "이 이메일은 이메일·비밀번호 로그인으로 가입되어 있습니다.";
      } else {
        result.textContent = "해당 이메일의 로그인 방법을 확인하지 못했습니다. 이메일 주소를 다시 확인해 주세요.";
      }
      result.hidden = false;
    } catch (error) {
      result.textContent = error.message;
      result.hidden = false;
    }
  });

  document.querySelectorAll(".auth-tab").forEach((button) => {
    button.addEventListener("click", () => setAuthMode(button.dataset.authMode));
  });
  $("landing-login-button").addEventListener("click", () => showAuth("login"));
  $("landing-install-button").addEventListener("click", installApp);
  $("landing-start-button").addEventListener("click", () => showAuth("signup"));
  $("principles-login-button").addEventListener("click", () => showAuth("login"));
  $("landing-cta-button").addEventListener("click", () => showAuth("signup"));
  $("back-to-landing").addEventListener("click", (event) => { event.preventDefault(); showLanding(); });
  $("install-app-button").addEventListener("click", installApp);
  $("settings-install-button").addEventListener("click", installApp);
  $("dismiss-install-button").addEventListener("click", () => {
    localStorage.setItem("classroom.dismissInstallPrompt", "1");
    syncInstallUi();
  });
  document.querySelectorAll("[data-legal-document]").forEach((button) => {
    button.addEventListener("click", () => openLegalDocument(button.dataset.legalDocument));
  });
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
    await refreshClass();
  });
  $("start-session-button").addEventListener("click", () => {
    const subject = currentClass()?.defaultSubject || state.teacher?.subject || "수업";
    startSession(subject).catch((error) => showToast(error.message));
  });
  $("announcement-button").addEventListener("click", () => openCommandDialog("message"));
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
  $("student-code-search").addEventListener("input", renderStudentCodes);
  $("student-code-grade").addEventListener("change", () => {
    renderStudentCodeFilters();
    renderStudentCodes();
  });
  $("student-code-class").addEventListener("change", renderStudentCodes);
  $("student-code-refresh").addEventListener("click", () => loadStudentCodes().catch((error) => showToast(error.message)));
  $("refresh-audit-button").addEventListener("click", () => loadAudit().catch((error) => showToast(error.message)));
  $("admin-enroll-button").addEventListener("click", openEnrollmentDialog);
  $("student-installer-download").addEventListener("click", downloadStudentInstaller);
  $("class-form").addEventListener("submit", (event) => {
    event.preventDefault();
    createClassFromAdmin();
  });
  $("roster-import-form").addEventListener("submit", (event) => {
    event.preventDefault();
    importRoster();
  });
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
  $("password-open-button").addEventListener("click", openPasswordDialog);
  $("send-password-verification").addEventListener("click", () => sendPasswordVerification().catch((error) => showToast(error.message)));
  $("verify-password-code").addEventListener("click", () => verifyPasswordCode().catch((error) => showToast(error.message)));
  $("password-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const message = $("password-message");
    message.hidden = true;
    if (!state.passwordVerificationId || $("change-password-submit").disabled) {
      message.textContent = "이메일 확인 코드를 먼저 확인해 주세요.";
      message.hidden = false;
      return;
    }
    if ($("new-password").value !== $("new-password-confirm").value) {
      message.textContent = "새 비밀번호가 일치하지 않습니다.";
      message.hidden = false;
      return;
    }
    try {
      await api("/auth/change-password", {
        method: "POST",
        body: {
          verificationId: state.passwordVerificationId,
          newPassword: $("new-password").value
        }
      });
      $("password-dialog").close("saved");
      state.passwordVerificationId = null;
      await loadTeacher();
      showToast("비밀번호를 변경했습니다.");
    } catch (error) {
      message.textContent = error.message;
      message.hidden = false;
    }
  });
  $("profile-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const message = $("profile-message");
    message.hidden = true;
    try {
      await saveProfile({
        displayName: $("profile-display-name").value.trim(),
        subject: $("profile-subject").value.trim(),
        schoolId: $("profile-school-id").value,
        termsAccepted: $("profile-terms").checked || state.teacher?.legalAccepted === true,
        privacyAccepted: $("profile-privacy").checked || state.teacher?.legalAccepted === true
      });
      showToast("교사 프로필을 저장했습니다.");
    } catch (error) {
      message.textContent = error.message;
      message.hidden = false;
    }
  });
  $("onboarding-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const errorTarget = $("onboarding-error");
    errorTarget.hidden = true;
    try {
      if (!state.teacher?.legalAccepted && (!$("onboarding-terms").checked || !$("onboarding-privacy").checked)) {
        throw new Error("이용약관과 개인정보처리방침 동의가 필요합니다.");
      }
      await saveProfile({
        displayName: $("onboarding-display-name").value.trim(),
        subject: $("onboarding-subject").value.trim(),
        className: "",
        schoolId: $("onboarding-school-id").value,
        termsAccepted: $("onboarding-terms").checked || state.teacher?.legalAccepted === true,
        privacyAccepted: $("onboarding-privacy").checked || state.teacher?.legalAccepted === true
      });
      $("onboarding-dialog").close("saved");
      showToast("프로필 설정을 저장했습니다.");
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });
  document.querySelectorAll(".filter").forEach((button) => button.addEventListener("click", () => {
    document.querySelectorAll(".filter").forEach((item) => item.classList.remove("active"));
    button.classList.add("active");
    state.filter = button.dataset.filter;
    renderStudents();
  }));
  document.querySelectorAll(".nav-item").forEach((button) => button.addEventListener("click", async () => {
    state.activeSection = button.dataset.section;
    document.querySelectorAll(".nav-item").forEach((item) => item.classList.remove("active"));
    button.classList.add("active");
    document.querySelectorAll(".section-view").forEach((section) => { section.hidden = section.id !== `${button.dataset.section}-section`; });
    if (button.dataset.section === "history") await loadAudit().catch((error) => showToast(error.message));
    if (button.dataset.section === "student-codes") await loadStudentCodes().catch((error) => showToast(error.message));
    if (button.dataset.section === "admins") await loadAdminDirectory().catch((error) => showToast(error.message));
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
  $("admin-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const errorTarget = $("admin-error");
    errorTarget.hidden = true;
    try {
      const identifier = $("admin-identifier").value.trim();
      if (!identifier) throw new Error("관리자 이메일 또는 아이디를 입력해 주세요.");
      await updateAdminAccess(identifier, true);
      $("admin-form").reset();
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });
  $("enrollment-copy").addEventListener("click", copyEnrollmentInstructions);
  $("enrollment-code-copy").addEventListener("click", copyEnrollmentCode);

  fetch(apiUrl("/health"), { headers: { Accept: "application/json" } })
    .then((response) => response.ok ? response.json() : null)
    .then((health) => {
      $("dev-login-hint").hidden = true;
      $("security-setting").textContent = apiOrigin
        ? `암호화된 외부 API ${apiOrigin}에 연결됨`
        : health?.storage === "durable-object"
          ? "Cloudflare의 암호화된 영속 API에 연결됨"
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
  registerPwa();
  syncInstallUi();

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
      .then((credentials) => {
        if (!credentials) return null;
        let profile = {};
        try {
          profile = JSON.parse(sessionStorage.getItem("classroom.pendingFirebaseProfile") || "{}");
        } catch (_) {
          profile = {};
        }
        return finishFirebaseLogin(credentials, profile);
      })
      .catch((error) => {
        loginError.textContent = error.message;
        loginError.hidden = false;
        showAuth("login");
      });
  }
})();
