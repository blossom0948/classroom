(() => {
  const APP_VERSION = "0.5.35";
  const runtimeConfig = window.CLASSROOM_CONFIG || {};
  const apiOrigin = String(runtimeConfig.apiOrigin || "").trim().replace(/\/+$/, "");
  const cookieSessionEnabled = runtimeConfig.cookieSession === true;

  const TEACHER_TOKEN_KEY = "classroom.teacherToken";
  const PENDING_FIREBASE_ENTRY_KEY = "classroom.pendingFirebaseEntry";

  function storageGet(storageName, key) {
    try { return window[storageName]?.getItem(key) || null; } catch (_) { return null; }
  }

  function storageSet(storageName, key, value) {
    try { window[storageName]?.setItem(key, value); } catch (_) { /* storage can be disabled */ }
  }

  function storageRemove(storageName, key) {
    try { window[storageName]?.removeItem(key); } catch (_) { /* storage can be disabled */ }
  }

  function readTeacherToken() {
    if (cookieSessionEnabled) return null;
    return storageGet("localStorage", TEACHER_TOKEN_KEY)
      || storageGet("sessionStorage", TEACHER_TOKEN_KEY);
  }

  function storeTeacherToken(token, isGuest = false) {
    if (cookieSessionEnabled) {
      storageRemove("localStorage", TEACHER_TOKEN_KEY);
      storageRemove("sessionStorage", TEACHER_TOKEN_KEY);
      return;
    }
    if (!token) return;
    if (isGuest) {
      storageRemove("localStorage", TEACHER_TOKEN_KEY);
      storageSet("sessionStorage", TEACHER_TOKEN_KEY, token);
      return;
    }
    storageSet("localStorage", TEACHER_TOKEN_KEY, token);
    storageRemove("sessionStorage", TEACHER_TOKEN_KEY);
  }

  const state = {
    token: readTeacherToken(),
    teacher: null,
    classes: [],
    classId: null,
    session: null,
    students: [],
    refreshInFlight: false,
    refreshQueued: false,
    refreshFailureCount: 0,
    lastSuccessfulRefreshAt: null,
    lastRefreshError: "",
    studentCodes: [],
    adminDirectory: null,
    operationsStatus: null,
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
    passwordVerificationId: null,
    confirmResolver: null,
    studentRosterClassId: null,
    screenWallTimer: null,
    screenWallOpen: false,
    screenWallRefreshInFlight: false,
    screenShareTargetIds: null,
    monitorPage: 0,
    stoppingScreenShare: false,
    detailDeviceId: null,
    detailView: "status",
    screenFrames: new Map(),
    studentExitPinStatus: null,
    guestPasswordStatus: null,
    studentSort: ["number", "name", "status"].includes(localStorage.getItem("classroom.studentSort"))
      ? localStorage.getItem("classroom.studentSort")
      : "number",
  };

  const $ = (id) => document.getElementById(id);
  const landingView = $("landing-view");
  const loginView = $("login-view");
  const appView = $("app-view");
  const loginError = $("login-error");

  function apiUrl(path) {
    return apiOrigin ? `${apiOrigin}${path}` : path;
  }

  function hasTeacherSession() {
    return Boolean(state.teacher || state.token);
  }

  async function api(path, options = {}) {
    const headers = { Accept: "application/json", ...(options.headers || {}) };
    if (!cookieSessionEnabled && state.token) headers.Authorization = `Bearer ${state.token}`;
    if (options.body && typeof options.body !== "string") {
      headers["Content-Type"] = "application/json";
      options.body = JSON.stringify(options.body);
    }
    let response;
    try {
      response = await fetch(apiUrl(path), {
        ...options,
        headers,
        credentials: cookieSessionEnabled ? "include" : "same-origin"
      });
    } catch (_) {
      throw new Error("Classroom 서버에 연결할 수 없습니다. 서버 주소와 배포 상태를 확인하세요.");
    }
    let payload = null;
    const contentType = response.headers.get("content-type") || "";
    if (contentType.includes("json")) payload = await response.json();
    if (response.status === 401) {
      if (cookieSessionEnabled) {
        fetch(apiUrl("/auth/logout"), {
          method: "POST",
          headers: { Accept: "application/json" },
          credentials: "include"
        }).catch(() => {});
      }
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
    state.refreshInFlight = false;
    state.refreshQueued = false;
    state.refreshFailureCount = 0;
    state.lastSuccessfulRefreshAt = null;
    state.lastRefreshError = "";
    state.studentCodes = [];
    state.adminDirectory = null;
    state.studentExitPinStatus = null;
    state.guestPasswordStatus = null;
    state.selectedDeviceIds.clear();
    state.weatherLoaded = false;
    state.passwordVerificationId = null;
    storageRemove("localStorage", TEACHER_TOKEN_KEY);
    storageRemove("sessionStorage", TEACHER_TOKEN_KEY);
    storageRemove("sessionStorage", "classroom.onboardingDismissed");
    storageRemove("sessionStorage", PENDING_FIREBASE_ENTRY_KEY);
    if (state.pollTimer) clearTimeout(state.pollTimer);
    state.pollTimer = null;
    if (state.screenWallTimer) clearInterval(state.screenWallTimer);
    state.screenWallTimer = null;
    state.screenWallOpen = false;
    state.screenWallRefreshInFlight = false;
    state.screenShareTargetIds = null;
    state.detailDeviceId = null;
    state.detailView = "status";
    state.screenFrames.clear();
    landingView.hidden = false;
    loginView.hidden = true;
    appView.hidden = true;
  }

  function applySessionToken(result, isGuest = false) {
    state.token = cookieSessionEnabled ? null : (result?.accessToken || null);
    storeTeacherToken(result?.accessToken, isGuest);
  }

  function showToast(message) {
    const toast = $("toast");
    toast.textContent = message;
    toast.classList.add("show");
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => toast.classList.remove("show"), 2800);
  }

  function askConfirmation(title, message, confirmLabel = "확인") {
    const dialog = $("confirm-dialog");
    if (!dialog) return Promise.resolve(false);
    if (state.confirmResolver) {
      state.confirmResolver(false);
      state.confirmResolver = null;
    }
    $("confirm-dialog-title").textContent = title;
    $("confirm-dialog-message").textContent = message;
    $("confirm-dialog-confirm").textContent = confirmLabel;
    return new Promise((resolve) => {
      state.confirmResolver = resolve;
      dialog.showModal();
    });
  }

  function currentClass() {
    return state.classes.find((item) => item.id === state.classId) || null;
  }

  function closeClassPicker() {
    const button = $("class-select-button");
    const menu = $("class-select-menu");
    if (menu) menu.hidden = true;
    if (button) button.setAttribute("aria-expanded", "false");
  }

  function renderClassPicker() {
    const select = $("class-select");
    const button = $("class-select-button");
    const menu = $("class-select-menu");
    if (!select || !button || !menu) return;
    const classes = Array.isArray(state.classes) ? state.classes : [];
    const selected = currentClass();
    select.innerHTML = classes.length
      ? classes.map((item) => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}</option>`).join("")
      : '<option value="">학급 없음</option>';
    select.value = state.classId || "";
    select.disabled = !classes.length;
    button.disabled = !classes.length;
    button.innerHTML = `<span>${escapeHtml(selected?.name || "학급 없음")}</span><span class="select-chevron" aria-hidden="true">⌄</span>`;
    menu.innerHTML = classes.length
      ? classes.map((item) => `<button type="button" class="class-select-option${item.id === state.classId ? " active" : ""}" role="option" aria-selected="${item.id === state.classId}" data-class-option="${escapeHtml(item.id)}"><span>${escapeHtml(item.name)}</span>${item.defaultSubject ? `<small>${escapeHtml(item.defaultSubject)}</small>` : ""}</button>`).join("")
      : '<div class="class-select-empty">관리자 메뉴에서 학급을 먼저 만들어 주세요.</div>';
    closeClassPicker();
  }

  function toggleClassPicker() {
    const button = $("class-select-button");
    const menu = $("class-select-menu");
    if (!button || !menu || button.disabled) return;
    const willOpen = menu.hidden;
    menu.hidden = !willOpen;
    button.setAttribute("aria-expanded", String(willOpen));
  }

  async function chooseClass(classId) {
    if (!classId || !state.classes.some((item) => item.id === classId)) return;
    if (state.screenShareTargetIds?.length) await stopScreenSharing(true);
    state.classId = classId;
    state.session = null;
    state.selectedDeviceIds.clear();
    renderClassPicker();
    await refreshClass();
  }

  function displayText(value, fallback = "확인 필요") {
    return typeof value === "string" && value.trim() ? value.trim() : fallback;
  }

  function normalizeActivity(activity) {
    if (!activity || typeof activity !== "object") return null;
    const applicationDisplayName = displayText(activity.applicationDisplayName, "");
    const processName = displayText(activity.processName, "");
    if (!applicationDisplayName && !processName) return null;
    return {
      applicationDisplayName: applicationDisplayName || "현재 앱 확인 필요",
      processName: processName || "unknown.exe",
      browserDomain: displayText(activity.browserDomain, "") || null,
      windowTitle: displayText(activity.windowTitle, "") || null,
      observedAtUtc: displayText(activity.observedAtUtc, "") || null
    };
  }

  function normalizeStudent(student) {
    if (!student || typeof student !== "object" || typeof student.deviceId !== "string" || !student.deviceId) return null;
    const asInteger = (value) => value === null || value === undefined || value === ""
      ? null
      : Number.isInteger(Number(value)) ? Number(value) : null;
    const risk = student.activityRisk && typeof student.activityRisk === "object"
      ? {
        level: displayText(student.activityRisk.level, "unknown"),
        label: displayText(student.activityRisk.label, "확인 필요"),
        reason: displayText(student.activityRisk.reason, "")
      }
      : null;
    return {
      ...student,
      deviceId: student.deviceId,
      studentDisplayName: displayText(student.studentDisplayName, "이름 미확인"),
      computerName: displayText(student.computerName, "장치 이름 미확인"),
      agentVersion: displayText(student.agentVersion, "확인 필요"),
      online: student.online === true,
      activity: normalizeActivity(student.activity),
      activityRisk: risk,
      batteryPercent: (() => {
        if (student.batteryPercent === null || student.batteryPercent === undefined || student.batteryPercent === "") return null;
        const value = Number(student.batteryPercent);
        return Number.isInteger(value) && value >= 0 && value <= 100 ? value : null;
      })(),
      networkStatus: displayText(student.networkStatus, "") || null,
      policyApplied: student.policyApplied === true,
      needsHelp: student.needsHelp === true,
      grade: asInteger(student.grade),
      classNumber: asInteger(student.classNumber),
      studentNumber: asInteger(student.studentNumber),
      lastHeartbeatUtc: displayText(student.lastHeartbeatUtc, "") || null
    };
  }

  async function loadTeacher() {
    const session = await api("/auth/me");
    state.teacher = session;
    state.classes = Array.isArray(session.classes) ? session.classes : [];
    const isGuest = session.isGuest === true;
    // Account sessions survive closing/reopening the installed console. Guest
    // sessions deliberately stay tab-scoped and are never promoted to disk.
    storeTeacherToken(state.token, isGuest);
    state.classId = state.classId && state.classes.some((item) => item.id === state.classId)
      ? state.classId
      : state.classes[0]?.id || null;
    $("teacher-name").textContent = session.displayName || (isGuest ? "게스트" : "교사");
    $("teacher-account").textContent = isGuest
      ? `${session.school?.name || "학교"} · 읽기 전용`
      : session.email || session.loginName || "Teacher";
    $("teacher-role").textContent = isGuest ? "게스트" : "관리자";
    $("teacher-role").classList.toggle("guest-badge", isGuest);
    $("teacher-role").hidden = !session.isAdmin && !isGuest;
    // The installed shell owns the Windows title-bar close action. Keeping a
    // second in-page close icon crowded the responsive header without adding
    // a reliable browser close path, so it intentionally stays out of view.
    $("close-console-button").hidden = true;
    $("admins-nav").hidden = !session.isAdmin;
    $("student-codes-nav").hidden = false;
    $("settings-nav").hidden = isGuest;
    $("admin-enroll-button").disabled = !session.isAdmin || !state.classes.length;
    $("admin-enroll-button").hidden = isGuest;
    $("admin-enroll-button").title = state.classes.length ? "학생 코드 발급" : "먼저 관리자 메뉴에서 학급을 만들어 주세요";
    $("student-code-permission").textContent = isGuest
      ? "게스트 · 수업 현황, 학생 활동과 학생 코드를 읽을 수 있습니다"
      : session.isAdmin
      ? "관리자: 코드 발급 및 재발급 가능"
      : "조회 전용 · 코드는 관리자에게 요청하세요";
    ["start-session-button", "announcement-button", "end-session-button", "screen-wall-button", "focus-on-button", "focus-off-button", "message-button", "url-button", "app-button"].forEach((id) => {
      const button = $(id);
      if (button) button.hidden = isGuest;
    });
    $("bulk-actions").hidden = isGuest;
    if (isGuest) {
      state.activeSection = "class";
      document.querySelectorAll(".nav-item").forEach((item) => item.classList.toggle("active", item.dataset.section === "class"));
      document.querySelectorAll(".section-view").forEach((section) => { section.hidden = section.id !== "class-section"; });
    }
    renderClassPicker();
    $("teacher-greeting").textContent = isGuest ? "게스트로 접속했습니다." : `${session.displayName || "선생님"}선생님 안녕하세요.`;
    $("school-name").textContent = session.school?.name || "학교를 설정해 주세요";
    $("school-name").classList.toggle("unconfigured", !session.school?.name);
    $("sidebar-school-name").textContent = session.school?.name || "학교를 설정해 주세요";
    $("sidebar-school-name").classList.toggle("unconfigured", !session.school?.name);
    renderAdminClassOptions();
    landingView.hidden = true;
    loginView.hidden = true;
    appView.hidden = false;
    applyTheme(state.theme);
    syncProfileControls(session);
    renderTodayInfo();
    loadWeather();
    await refreshClass();
    startClassPolling();
    window.setTimeout(() => maybeOpenOnboarding(session), 150);
  }

  async function refreshClass() {
    if (state.refreshInFlight) {
      state.refreshQueued = true;
      return;
    }
    state.refreshInFlight = true;
    try {
      await refreshClassOnce();
      state.refreshFailureCount = 0;
      state.lastRefreshError = "";
      state.lastSuccessfulRefreshAt = new Date().toISOString();
    } catch (error) {
      state.refreshFailureCount += 1;
      state.lastRefreshError = "학생 상태를 새로 받지 못했습니다.";
      renderRefreshStatus();
      throw error;
    } finally {
      state.refreshInFlight = false;
      if (state.refreshQueued) {
        state.refreshQueued = false;
        window.queueMicrotask(() => refreshClass().catch(() => {}));
      }
    }
  }

  // Use one scheduled request at a time. An interval can stack requests when
  // a school network is slow, which made a later error overwrite a perfectly
  // usable roster. Failed refreshes now back off while the last good cards
  // stay visible.
  function startClassPolling() {
    if (state.pollTimer) clearTimeout(state.pollTimer);
    state.pollTimer = null;
    scheduleNextClassRefresh();
  }

  function scheduleNextClassRefresh() {
    if (!hasTeacherSession()) return;
    if (state.pollTimer) clearTimeout(state.pollTimer);
    const baseDelay = document.visibilityState === "visible" ? 2_000 : 12_000;
    const retryDelay = state.refreshFailureCount
      ? Math.min(30_000, baseDelay * (2 ** Math.min(state.refreshFailureCount, 4)))
      : baseDelay;
    state.pollTimer = window.setTimeout(async () => {
      state.pollTimer = null;
      try {
        await refreshClass();
      } catch (_) {
        // The status callout has the user-facing explanation. Keep polling
        // rather than producing a toast every few seconds.
      } finally {
        scheduleNextClassRefresh();
      }
    }, retryDelay);
  }

  async function refreshClassOnce() {
    if (!state.classId) {
      state.session = null;
      state.students = [];
      $("class-subject").textContent = "";
      renderHeader();
      renderStudents();
      return;
    }
    const selected = currentClass();
    $("class-subject").textContent = selected?.defaultSubject || "";
    const [session, students] = await Promise.all([
      api(`/api/classes/${state.classId}/session`),
      api(`/api/classes/${state.classId}/students`)
    ]);
    state.session = session && typeof session === "object" ? session : null;
    state.students = Array.isArray(students)
      ? students.map(normalizeStudent).filter(Boolean)
      : [];
    const currentIds = new Set(state.students.map((student) => student.deviceId));
    state.selectedDeviceIds = new Set(
      [...state.selectedDeviceIds].filter((deviceId) => currentIds.has(deviceId))
    );
    renderHeader();
    renderStudents();
    if (state.detailDeviceId && !$("detail-pane").hidden) renderDetail();
  }

  function renderHeader() {
    const online = state.students.filter((student) => student.online).length;
    const offline = state.students.length - online;
    const needsAttention = state.students.filter(isNeedsAttention).length;
    $("total-count").textContent = String(state.students.length);
    $("metric-online-count").textContent = String(online);
    $("offline-count").textContent = String(offline);
    $("needs-attention-count").textContent = String(needsAttention);
    $("session-caption").textContent = formatSessionCaption(state.session);
    $("start-session-button").hidden = Boolean(state.teacher?.isGuest) || Boolean(state.session) || !state.classId;
    $("end-session-button").hidden = Boolean(state.teacher?.isGuest) || !state.session;
    const screenWallButton = $("screen-wall-button");
    if (screenWallButton) {
      screenWallButton.textContent = state.screenWallOpen ? "화면 보기 닫기" : "화면 보기";
      screenWallButton.setAttribute("aria-pressed", String(state.screenWallOpen));
    }
    const monitorBar = $("monitor-session-bar");
    const monitorFab = $("monitor-fullscreen-fab");
    const onlineStudents = state.students.filter((student) => student.online);
    const canMonitor = !state.teacher?.isGuest && Boolean(state.session) && onlineStudents.length > 0;
    if (monitorFab) {
      monitorFab.hidden = !canMonitor;
      monitorFab.disabled = !canMonitor;
    }
    if (monitorBar) {
      monitorBar.hidden = !state.screenWallOpen;
      if (state.screenWallOpen) {
        const targetIds = state.screenShareTargetIds || [];
        const received = targetIds.filter((deviceId) => isUsableScreenFrame(state.screenFrames.get(deviceId))).length;
        $("monitor-session-status").textContent = `${received}/${targetIds.length}명 화면 수신 중`;
        const refreshMilliseconds = monitorRefreshIntervalMs(targetIds.length);
        const refreshLabel = refreshMilliseconds < 1_000 ? "약 0.75초" : "약 1초";
        $("monitor-session-updated").textContent = received
          ? `최대 720p 자동 화질 · ${refreshLabel}마다 갱신됩니다. 학생을 누르면 크게 볼 수 있습니다.`
          : "첫 화면을 기다리는 중입니다. 학생 앱 연결 상태를 확인해 주세요.";
      }
    }
    renderSelection();
    renderStudentViewControls();
    renderRefreshStatus();
  }

  function formatSessionCaption(session) {
    if (!session) return "활성 수업이 없습니다.";
    const startedAt = new Date(session.startedAtUtc);
    if (Number.isNaN(startedAt.getTime())) return `${displayText(session.subject, "수업")} · 시작 시간 확인 필요`;
    const elapsedMinutes = Math.max(0, Math.floor((Date.now() - startedAt.getTime()) / 60_000));
    const progress = elapsedMinutes < 1 ? "방금 시작" : `${elapsedMinutes}분 진행`;
    return `${displayText(session.subject, "수업")} · ${formatTime(session.startedAtUtc)} 시작 · ${progress}`;
  }

  function attentionSignals(student) {
    if (!student?.online) return [];
    const signals = [];
    if (student.needsHelp) signals.push({ kind: "help", label: "도움 요청", detail: "학생이 도움을 요청했습니다." });
    if (student.activityRisk?.level === "warning") {
      signals.push({ kind: "risk", label: "활동 확인", detail: student.activityRisk.reason || "활동 신호를 확인해 주세요." });
    }
    if (!student.activity) signals.push({ kind: "agent", label: "상태 대기", detail: "학생 앱의 현재 활동 정보가 아직 도착하지 않았습니다." });
    if (!student.networkStatus || student.networkStatus === "unknown") {
      signals.push({ kind: "network", label: "연결 확인", detail: "네트워크 상태를 확인하지 못했습니다." });
    }
    if (student.batteryPercent != null && student.batteryPercent <= 15) {
      signals.push({ kind: "battery", label: "저배터리", detail: `배터리가 ${student.batteryPercent}%입니다.` });
    }
    return signals;
  }

  function isNeedsAttention(student) {
    return attentionSignals(student).length > 0;
  }

  function primaryAttentionSignal(student) {
    return attentionSignals(student)[0] || null;
  }

  function studentStatusRank(student) {
    if (!student.online) return 4;
    const signal = primaryAttentionSignal(student);
    if (signal?.kind === "help") return 0;
    if (signal) return 1;
    if (student.policyApplied) return 2;
    return 3;
  }

  function sortStudents(students) {
    const collator = new Intl.Collator("ko-KR", { numeric: true, sensitivity: "base" });
    return [...students].sort((left, right) => {
      if (state.studentSort === "status") {
        const statusDifference = studentStatusRank(left) - studentStatusRank(right);
        if (statusDifference) return statusDifference;
      } else if (state.studentSort === "name") {
        const nameDifference = collator.compare(left.studentDisplayName, right.studentDisplayName);
        if (nameDifference) return nameDifference;
      } else {
        const numberDifference = (left.studentNumber ?? Number.MAX_SAFE_INTEGER) - (right.studentNumber ?? Number.MAX_SAFE_INTEGER);
        if (numberDifference) return numberDifference;
      }
      const fallbackName = collator.compare(left.studentDisplayName, right.studentDisplayName);
      return fallbackName || collator.compare(left.computerName, right.computerName);
    });
  }

  function renderStudentViewControls() {
    const sort = $("student-sort");
    if (sort) sort.value = state.studentSort;
  }

  function renderRefreshStatus() {
    const callout = $("class-sync-status");
    const message = $("class-sync-message");
    if (!callout || !message) return;
    if (!state.lastRefreshError) {
      callout.hidden = true;
      return;
    }
    const attempts = state.refreshFailureCount > 1 ? ` 연속 ${state.refreshFailureCount}회 실패했습니다.` : "";
    const lastSuccess = state.lastSuccessfulRefreshAt ? ` 마지막 정상 갱신 ${formatTime(state.lastSuccessfulRefreshAt)}.` : "";
    message.textContent = `${state.lastRefreshError}${attempts} 기존 학생 목록은 유지하고 다시 연결 중입니다.${lastSuccess}`;
    callout.hidden = false;
  }

  function renderSelection() {
    const count = state.selectedDeviceIds.size;
    $("selection-caption").textContent = count ? `${count}명 선택됨` : "전체 학생 대상";
    $("clear-selection-button").hidden = count === 0;
  }

  function commandTargets() {
    return state.selectedDeviceIds.size ? [...state.selectedDeviceIds] : null;
  }

  function filteredStudentsForView() {
    const query = String(state.search || "").toLocaleLowerCase("ko-KR");
    return sortStudents(state.students.filter((student) => {
      if (state.filter === "online") return student.online;
      if (state.filter === "offline") return !student.online;
      if (state.filter === "attention") return isNeedsAttention(student);
      return true;
    }).filter((student) => !query
      || student.studentDisplayName.toLocaleLowerCase("ko-KR").includes(query)));
  }

  function renderStudents() {
    const grid = $("student-grid");
    const filtered = filteredStudentsForView();
    const monitorMode = state.screenWallOpen;
    grid.classList.toggle("monitor-grid", monitorMode);
    renderStudentViewControls();

    if (monitorMode) {
      renderMonitorGrid(grid, filtered);
      return;
    }

    renderMonitorPagination(0, 0);
    if (!filtered.length) {
      if (state.students.length) {
        grid.innerHTML = '<div class="empty-state">현재 필터에 해당하는 학생이 없습니다.</div>';
      } else {
        grid.innerHTML = `<div class="empty-state"><strong>등록된 학생 PC가 없습니다.</strong><p>관리자는 관리자 메뉴에서 학생 코드를 발급하고, 학생은 학생용 설치 앱에 코드를 입력합니다.</p></div>`;
      }
      return;
    }

    grid.innerHTML = filtered.map((student) => {
      const activity = activityForClassroom(student);
      const activityContext = activity?.browserDomain || activity?.windowTitle || "현재 창 정보 없음";
      const attention = primaryAttentionSignal(student);
      const statusClass = !student.online ? "" : attention?.kind === "help" ? "help" : attention ? "attention" : student.policyApplied ? "focus" : "online";
      const statusText = !student.online ? "오프라인" : attention?.kind === "help" ? "도움 요청" : attention ? "확인 필요" : student.policyApplied ? "집중 모드" : "온라인";
      const battery = student.batteryPercent == null ? "배터리 —" : `배터리 ${student.batteryPercent}%`;
      const selected = state.selectedDeviceIds.has(student.deviceId);
      const selector = studentSelectorMarkup(student, selected);
      const riskNotice = attention
        ? `<div class="activity-risk ${escapeHtml(attention.kind)}"><span aria-hidden="true">!</span><span>${escapeHtml(attention.detail)}</span></div>`
        : "";
      const quickMessage = state.teacher?.isGuest
        ? ""
        : `<button class="student-card-message" type="button" data-student-message="${escapeHtml(student.deviceId)}" aria-label="${escapeHtml(student.studentDisplayName)} 학생에게 개인 메시지 보내기" title="${escapeHtml(student.studentDisplayName)} 학생에게 개인 메시지">메시지</button>`;
      return `<article class="student-card${selected ? " selected" : ""}" data-device-id="${escapeHtml(student.deviceId)}">
        ${selector}
        <div class="student-head"><div><div class="student-name">${escapeHtml(student.studentDisplayName)}</div><div class="student-device">${escapeHtml(student.computerName)}</div></div><span class="status-dot ${statusClass}">${statusText}</span></div>
        <div class="student-activity"><span class="app-icon" aria-hidden="true">▣</span><div class="activity-copy"><div class="activity-label">현재 활동</div><div class="activity-app">${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</div><div class="activity-domain">${escapeHtml(activityContext)}</div>${riskNotice}</div></div>
        <div class="student-card-footer"><div class="student-meta"><span>${student.studentNumber ? `${student.studentNumber}번` : "번호 —"}</span><span>${battery}</span><span>${escapeHtml(student.networkStatus || "unknown")}</span>${student.policyApplied ? '<span class="policy-tag">🔒 집중</span>' : ""}${attention ? `<span class="risk-tag ${escapeHtml(attention.kind)}">${escapeHtml(attention.label)}</span>` : ""}</div>${quickMessage}</div>
      </article>`;
    }).join("");
    bindStudentSelection(grid);
    grid.querySelectorAll(".student-card").forEach((card) => {
      card.addEventListener("click", () => openDetail(card.dataset.deviceId));
    });
    grid.querySelectorAll("[data-student-message]").forEach((button) => {
      button.addEventListener("click", (event) => {
        event.stopPropagation();
        openCommandDialog("message", [button.dataset.studentMessage]);
      });
    });
  }

  function studentSelectorMarkup(student, selected) {
    return state.teacher?.isGuest
      ? ""
      : `<label class="student-selector" title="명령 대상 선택"><input type="checkbox" aria-label="${escapeHtml(student.studentDisplayName)} 선택" ${selected ? "checked" : ""}></label>`;
  }

  function bindStudentSelection(grid) {
    grid.querySelectorAll("[data-device-id]").forEach((card) => {
      const checkbox = card.querySelector("input[type=checkbox]");
      if (!checkbox) return;
      checkbox.addEventListener("click", (event) => event.stopPropagation());
      checkbox.addEventListener("change", () => {
        if (checkbox.checked) state.selectedDeviceIds.add(card.dataset.deviceId);
        else state.selectedDeviceIds.delete(card.dataset.deviceId);
        renderStudents();
        renderSelection();
      });
    });
  }

  // Show a short, current activity summary on the dashboard so a teacher can
  // notice a disconnected student without opening every detail panel.
  function activityForClassroom(student) {
    if (!student.online) {
      return { applicationDisplayName: "오프라인", browserDomain: "마지막 활동 없음" };
    }
    if (!student.activity) {
      return { applicationDisplayName: "학생 화면 확인 필요", browserDomain: "에이전트 연결 대기 중" };
    }
    const activity = student.activity;
    return {
      applicationDisplayName: activity.applicationDisplayName || "현재 앱 확인 필요",
      browserDomain: activity.browserDomain ? `웹 · ${activity.browserDomain}` : activity.windowTitle || "창 제목 없음"
    };
  }

  function isUsableScreenFrame(frame) {
    return frame?.screenFrame?.mimeType === "image/jpeg"
      && typeof frame.screenFrame.base64Data === "string"
      && /^[A-Za-z0-9+/=]+$/.test(frame.screenFrame.base64Data);
  }

  function monitorRefreshIntervalMs(targetCount = state.screenShareTargetIds?.length || 0) {
    // A classroom wall stays stable at one second once it reaches a typical
    // full class. Small selections get a visibly faster, 0.75-second update.
    return targetCount > 12 ? 1_000 : 750;
  }

  function screenFrameDimensions(frame) {
    const width = Number(frame?.screenFrame?.width);
    const height = Number(frame?.screenFrame?.height);
    if (!Number.isInteger(width) || !Number.isInteger(height) || width < 1 || height < 1) return null;
    return { width, height };
  }

  function screenFrameStyle(frame) {
    const dimensions = screenFrameDimensions(frame);
    return dimensions
      ? ` style="--screen-aspect-ratio: ${dimensions.width} / ${dimensions.height}"`
      : "";
  }

  function screenFrameMeta(frame) {
    const dimensions = screenFrameDimensions(frame);
    return dimensions
      ? `${dimensions.width} × ${dimensions.height} · 자동 화질`
      : "최대 720p · 자동 화질";
  }

  function monitorPageSize() {
    if (window.innerWidth <= 640) return 4;
    if (window.innerWidth <= 1120) return 8;
    return 12;
  }

  function renderMonitorGrid(grid, filteredStudents) {
    const targetIds = new Set(state.screenShareTargetIds || []);
    const targetStudents = filteredStudents.filter((student) => targetIds.has(student.deviceId));
    if (!targetStudents.length) {
      grid.innerHTML = '<div class="empty-state screen-wall-empty">화면을 공유할 학생이 없습니다. 온라인 학생을 선택하거나 필터를 바꿔 주세요.</div>';
      renderMonitorPagination(0, 0);
      return;
    }

    const pageSize = monitorPageSize();
    const pageCount = Math.max(1, Math.ceil(targetStudents.length / pageSize));
    state.monitorPage = Math.min(Math.max(state.monitorPage, 0), pageCount - 1);
    const pageStudents = targetStudents.slice(state.monitorPage * pageSize, (state.monitorPage + 1) * pageSize);
    grid.innerHTML = pageStudents.map((student) => {
      const frame = state.screenFrames.get(student.deviceId);
      const image = isUsableScreenFrame(frame)
        ? `<img src="data:image/jpeg;base64,${frame.screenFrame.base64Data}" alt="${escapeHtml(student.studentDisplayName)} 학생 화면" decoding="async">`
        : `<div class="screen-frame-empty">${student.online ? "첫 화면을 기다리는 중입니다…" : "학생이 오프라인입니다."}</div>`;
      const selected = state.selectedDeviceIds.has(student.deviceId);
      const attention = primaryAttentionSignal(student);
      const status = student.online ? "온라인" : "오프라인";
      const risk = attention
        ? `<span class="screen-risk-label ${escapeHtml(attention.kind)}">${escapeHtml(attention.label)}</span>`
        : "";
      const number = student.studentNumber ? `${student.studentNumber}번` : "번호 —";
      const quickMessage = state.teacher?.isGuest
        ? ""
        : `<button class="student-monitor-message" type="button" data-student-message="${escapeHtml(student.deviceId)}" aria-label="${escapeHtml(student.studentDisplayName)} 학생에게 개인 메시지 보내기" title="${escapeHtml(student.studentDisplayName)} 학생에게 개인 메시지">메시지</button>`;
      return `<article class="student-monitor-card${selected ? " selected" : ""}" data-device-id="${escapeHtml(student.deviceId)}">
        ${studentSelectorMarkup(student, selected)}
        <button class="student-monitor-preview" type="button" data-monitor-open="${escapeHtml(student.deviceId)}" aria-label="${escapeHtml(student.studentDisplayName)} 학생 화면 크게 보기">
          <span class="monitor-live-badge ${student.online ? "online" : "offline"}">${status}</span>
          ${risk}
          <span class="monitor-screen-frame">${image}</span>
        </button>
        ${quickMessage}
        <div class="student-monitor-hinge"><span>${escapeHtml(number)}</span><strong>${escapeHtml(student.studentDisplayName)}</strong></div>
      </article>`;
    }).join("");
    bindStudentSelection(grid);
    grid.querySelectorAll("[data-monitor-open]").forEach((button) => {
      button.addEventListener("click", () => openDetail(button.dataset.monitorOpen, "screen"));
    });
    grid.querySelectorAll("[data-student-message]").forEach((button) => {
      button.addEventListener("click", (event) => {
        event.stopPropagation();
        openCommandDialog("message", [button.dataset.studentMessage]);
      });
    });
    renderMonitorPagination(targetStudents.length, pageSize);
  }

  function renderMonitorPagination(total, pageSize) {
    const pagination = $("monitor-pagination");
    if (!pagination) return;
    const pageCount = pageSize ? Math.ceil(total / pageSize) : 0;
    pagination.hidden = pageCount <= 1;
    if (pageCount <= 1) {
      pagination.innerHTML = "";
      return;
    }
    pagination.innerHTML = Array.from({ length: pageCount }, (_, index) => {
      const page = index + 1;
      return `<button class="monitor-page-button${index === state.monitorPage ? " active" : ""}" type="button" data-monitor-page="${index}" aria-current="${index === state.monitorPage ? "page" : "false"}">${page}</button>`;
    }).join("");
    pagination.querySelectorAll("[data-monitor-page]").forEach((button) => {
      button.addEventListener("click", () => {
        state.monitorPage = Number(button.dataset.monitorPage) || 0;
        renderStudents();
      });
    });
  }

  function renderScreenWall() {
    renderHeader();
    renderStudents();
  }

  function openDetail(deviceId, view = "status") {
    if (!state.students.some((student) => student.deviceId === deviceId)) return;
    state.detailDeviceId = deviceId;
    state.detailView = view;
    $("detail-pane").hidden = false;
    renderDetail();
  }

  function renderDetail() {
    const deviceId = state.detailDeviceId;
    const student = state.students.find((item) => item.deviceId === deviceId);
    if (!student) {
      $("detail-pane").hidden = true;
      $("detail-pane").classList.remove("screen-mode");
      return;
    }
    $("detail-pane").classList.toggle("screen-mode", state.detailView === "screen");
    const activity = student.activity;
    const attention = primaryAttentionSignal(student);
    const detailStatusClass = !student.online ? "" : attention?.kind === "help" ? "help" : attention ? "attention" : student.policyApplied ? "focus" : "online";
    const detailStatusText = !student.online ? "오프라인" : attention?.kind === "help" ? "도움 요청" : attention ? "확인 필요" : student.policyApplied ? "집중 모드" : "온라인";
    const riskMarkup = attention
      ? `<div class="risk-callout ${escapeHtml(attention.kind)}"><strong>${escapeHtml(attention.label)}</strong><span>${escapeHtml(attention.detail)}</span></div>`
      : `<div class="privacy-note">현재 앱과 창 제목을 상태 요약으로 표시합니다. 화면 보기는 교사가 수업 중 직접 켠 동안만 저화질로 전송되며 학생 앱에 공유 중 표시가 나타납니다.</div>`;
    const statusRows = `<div class="detail-section"><h3>현재 상태</h3><div class="detail-row"><span>학급 / 번호</span><strong>${student.grade ? `${student.grade}학년 ${student.classNumber || ""}반 · ${student.studentNumber || "—"}번` : "학급 정보 없음"}</strong></div><div class="detail-row"><span>컴퓨터</span><strong>${escapeHtml(student.computerName)}</strong></div><div class="detail-row"><span>수업 신호</span><strong>${escapeHtml(attention?.label || "정상")}</strong></div><div class="detail-row"><span>현재 앱</span><strong>${escapeHtml(activity?.applicationDisplayName || "확인 필요")}</strong></div><div class="detail-row"><span>현재 창</span><strong>${escapeHtml(activity?.windowTitle || "창 정보 미연결")}</strong></div><div class="detail-row"><span>웹 도메인</span><strong>${escapeHtml(activity?.browserDomain || "도메인 미연결")}</strong></div><div class="detail-row"><span>배터리</span><strong>${student.batteryPercent == null ? "AC 전원 또는 정보 없음" : `${student.batteryPercent}%`}</strong></div><div class="detail-row"><span>네트워크</span><strong>${escapeHtml(student.networkStatus || "unknown")}</strong></div><div class="detail-row"><span>마지막 연결</span><strong>${formatTime(student.lastHeartbeatUtc)}</strong></div><div class="detail-row"><span>정책</span><strong>${student.policyApplied ? "집중 모드" : "일반"}</strong></div></div>`;
    const deviceRows = `<div class="detail-section"><h3>학생 앱</h3><div class="detail-row"><span>등록 상태</span><strong>${student.agentVersion && student.agentVersion !== "확인 필요" ? `연결된 설치 앱 · v${escapeHtml(student.agentVersion)}` : "설치 상태 확인 중"}</strong></div><div class="detail-row"><span>Windows 시작</span><strong>학생 설치 앱에서 자동 연결 설정</strong></div><div class="detail-row"><span>Device ID</span><code>${student.deviceId.slice(0, 8)}…</code></div></div>`;
    const header = `<div class="eyebrow">STUDENT DEVICE</div><h2 class="detail-title">${escapeHtml(student.studentDisplayName)}</h2><div class="detail-status"><span class="status-dot ${detailStatusClass}">${detailStatusText}</span></div>`;

    if (state.detailView === "screen") {
      const frame = state.screenFrames.get(student.deviceId);
      const image = isUsableScreenFrame(frame)
        ? `<img src="data:image/jpeg;base64,${frame.screenFrame.base64Data}" alt="${escapeHtml(student.studentDisplayName)} 학생 화면">`
        : '<div class="screen-frame-empty">학생 화면을 불러오는 중입니다…</div>';
      $("detail-content").innerHTML = `<div class="screen-detail-layout"><section class="detail-screen-column"><div class="screen-detail-caption"><span class="eyebrow">LIVE STUDENT SCREEN</span><strong>${escapeHtml(student.studentDisplayName)} 학생 화면</strong></div><section id="detail-screen-stage" class="detail-screen-stage"><div class="detail-screen-toolbar"><span class="screen-live-dot">● 화면 공유 중</span><span class="muted small">${screenFrameMeta(frame)} · ${monitorRefreshIntervalMs()}ms 갱신</span></div><div class="detail-screen-frame"${screenFrameStyle(frame)}>${image}</div></section></section><aside class="detail-screen-inspector">${header}${riskMarkup}${statusRows}${deviceRows}<div class="detail-section detail-screen-actions"><button id="detail-screen-fullscreen" class="primary wide" type="button">전체 화면</button><button id="detail-screen-stop" class="danger-action wide" type="button">화면 공유 종료</button></div></aside></div>`;
      $("detail-screen-fullscreen").addEventListener("click", () => openDetailScreenFullscreen().catch(() => showToast("전체 화면을 사용할 수 없습니다.")));
      $("detail-screen-stop").addEventListener("click", () => stopScreenSharing().catch((error) => showToast(error.message)));
      return;
    }

    const detailActions = state.teacher?.isGuest
      ? '<div class="detail-section"><div class="privacy-note guest-readonly-note">게스트 계정은 수업 현황과 학생 활동을 읽기 전용으로 확인합니다.</div></div>'
      : '<div class="detail-section stack"><button class="primary wide" id="detail-screen-button">이 학생 화면 보기</button><button class="secondary wide" id="detail-message-button">이 학생에게 메시지</button><button class="danger-action wide" id="detail-revoke-button">장치 연결 해제</button></div>';
    $("detail-content").innerHTML = `${header}${riskMarkup}${statusRows}${deviceRows}${detailActions}`;
    if (state.teacher?.isGuest) return;
    $("detail-screen-button").addEventListener("click", () => openStudentScreen(student.deviceId).catch((error) => showToast(error.message)));
    $("detail-message-button").addEventListener("click", () => openCommandDialog("message", [student.deviceId]));
    $("detail-revoke-button").addEventListener("click", () => revokeDevice(student).catch((error) => showToast(error.message)));
  }

  async function closeDetail() {
    $("detail-pane").hidden = true;
    $("detail-pane").classList.remove("screen-mode");
    state.detailView = "status";
  }

  async function openDetailScreenFullscreen() {
    const stage = $("detail-screen-stage");
    if (!stage) return;
    if (document.fullscreenElement === stage) {
      await document.exitFullscreen();
    } else if (stage.requestFullscreen) {
      await stage.requestFullscreen();
    } else {
      stage.classList.toggle("fullscreen-mode");
    }
  }

  async function revokeDevice(student) {
    if (!await askConfirmation("장치 연결 해제", `${student.studentDisplayName} 학생의 ${student.computerName} 연결을 해제할까요?\n이 장치는 새 등록 파일 없이는 다시 연결할 수 없습니다.`, "연결 해제")) return;
    await api(`/api/classes/${state.classId}/devices/${student.deviceId}`, { method: "DELETE" });
    $("detail-pane").hidden = true;
    showToast("학생 장치 연결을 해제했습니다.");
    await refreshClass();
  }

  async function loadStudentCodes() {
    const list = $("student-codes-list");
    if (!list) return;
    list.innerHTML = '<div class="empty-state">학생 코드를 불러오는 중입니다…</div>';
    const codes = await api("/api/student-codes");
    state.studentCodes = Array.isArray(codes) ? codes : [];
    renderStudentCodes();
  }

  function studentAdminKey(student) {
    return `${student.classId || ""}::${student.studentId || ""}`;
  }

  function renderStudentAdminOptions() {
    const select = $("student-admin-select");
    if (!select) return;
    const query = $("student-admin-search")?.value.trim().toLocaleLowerCase("ko-KR") || "";
    const candidates = [];
    const seen = new Set();
    [...state.studentCodes]
      .sort((left, right) => (Number(right.grade || 0) - Number(left.grade || 0))
        || (Number(left.classNumber || 0) - Number(right.classNumber || 0))
        || (Number(left.studentNumber || 999) - Number(right.studentNumber || 999))
        || String(left.studentDisplayName || "").localeCompare(String(right.studentDisplayName || ""), "ko"))
      .forEach((student) => {
        const key = studentAdminKey(student);
        if (!key || seen.has(key)) return;
        if (query && !String(student.studentDisplayName || "").toLocaleLowerCase("ko-KR").includes(query)) return;
        seen.add(key);
        candidates.push(student);
      });
    const previous = select.value;
    select.innerHTML = candidates.length
      ? `<option value="">학생을 선택하세요</option>${candidates.map((student) => `<option value="${escapeHtml(studentAdminKey(student))}">${escapeHtml(`${student.grade || "—"}학년 ${student.classNumber || "—"}반 · ${student.studentNumber || "—"}번 · ${student.studentDisplayName}`)}</option>`).join("")}`
      : '<option value="">검색 결과가 없습니다</option>';
    if (candidates.some((student) => studentAdminKey(student) === previous)) select.value = previous;
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
    const models = collectRosterClasses().filter((item) => !query || item.codes.some((code) => String(code.studentDisplayName || "").toLocaleLowerCase("ko-KR").includes(query)));
    if (!models.length) {
      list.innerHTML = query
        ? '<div class="empty-state">검색 조건에 맞는 학생이 없습니다.</div>'
        : '<div class="empty-state"><strong>아직 만들어진 학급이 없습니다.</strong><p>관리자가 관리자 메뉴에서 학급과 학생 코드를 준비하면 여기에 표시됩니다.</p></div>';
      return;
    }

    const grades = new Map();
    models.forEach((item) => {
      if (!grades.has(item.grade)) grades.set(item.grade, []);
      grades.get(item.grade).push(item);
    });
    list.innerHTML = [...grades.entries()].sort(([left], [right]) => right - left).map(([grade, classes]) => {
      classes.sort((left, right) => left.classNumber - right.classNumber || left.className.localeCompare(right.className, "ko"));
      return `<section class="grade-roster-group"><div class="grade-roster-heading"><div><span class="eyebrow">GRADE</span><h3>${grade}학년</h3></div><span class="muted small">${classes.length}개 반</span></div><div class="roster-class-grid">${classes.map((item) => `<button class="roster-class-card" type="button" data-roster-class="${escapeHtml(item.classId)}"><span class="roster-class-number">${item.classNumber}반</span><span class="roster-class-meta">${item.codes.length}명${item.subject ? ` · ${escapeHtml(item.subject)}` : ""}</span><span class="roster-class-action">명단 보기 <span aria-hidden="true">→</span></span></button>`).join("")}</div></section>`;
    }).join("");

    list.querySelectorAll("[data-roster-class]").forEach((button) => {
      button.addEventListener("click", () => openStudentRoster(button.dataset.rosterClass));
    });
  }

  function collectRosterClasses() {
    const models = new Map();
    state.classes.forEach((item) => {
      const grade = Number(item.grade || 0);
      const classNumber = Number(item.classNumber || 0);
      if (!grade || !classNumber) return;
      models.set(item.id, {
        classId: item.id,
        className: item.name || `${grade}학년 ${classNumber}반`,
        subject: item.defaultSubject || "",
        grade,
        classNumber,
        codes: []
      });
    });
    state.studentCodes.forEach((code) => {
      const grade = Number(code.grade || 0);
      const classNumber = Number(code.classNumber || 0);
      if (!grade || !classNumber) return;
      const classId = code.classId || `roster:${grade}:${classNumber}`;
      if (!models.has(classId)) {
        models.set(classId, {
          classId,
          className: code.className || `${grade}학년 ${classNumber}반`,
          subject: code.subject || "",
          grade,
          classNumber,
          codes: []
        });
      }
      models.get(classId).codes.push(code);
    });
    return [...models.values()];
  }

  function openStudentRoster(classId) {
    const model = collectRosterClasses().find((item) => item.classId === classId);
    const dialog = $("student-roster-dialog");
    const content = $("student-roster-content");
    if (!model || !dialog || !content) return;
    state.studentRosterClassId = classId;
    const codes = [...model.codes].sort((left, right) => (Number(left.studentNumber || 999) - Number(right.studentNumber || 999)) || String(left.studentDisplayName || "").localeCompare(String(right.studentDisplayName || ""), "ko"));
    $("student-roster-title").textContent = model.className;
    $("student-roster-subtitle").textContent = `${codes.length}명${model.subject ? ` · ${model.subject}` : ""} · 학생 이름, 번호, 코드를 확인할 수 있습니다.`;
    content.innerHTML = codes.length
      ? `<div class="student-roster-table-wrap"><table class="student-roster-table"><thead><tr><th>번호</th><th>학생</th><th>학생 코드</th><th></th></tr></thead><tbody>${codes.map((code) => `<tr><td>${escapeHtml(code.studentNumber || "—")}</td><td><strong>${escapeHtml(code.studentDisplayName)}</strong></td><td><code>${escapeHtml(code.joinCode)}</code></td><td class="roster-row-actions"><button class="secondary roster-code-copy" type="button" data-roster-code-copy="${escapeHtml(code.joinCode)}">복사</button>${state.teacher?.isAdmin ? `<button class="ghost-button code-reissue-button" type="button" data-roster-code-reissue="${escapeHtml(code.studentId)}">새 코드</button>` : ""}</td></tr>`).join("")}</tbody></table></div>`
      : '<div class="empty-state">이 반에는 아직 등록된 학생 코드가 없습니다.</div>';
    content.querySelectorAll("[data-roster-code-copy]").forEach((button) => {
      button.addEventListener("click", async () => {
        try {
          await navigator.clipboard.writeText(button.dataset.rosterCodeCopy || "");
          showToast("학생 코드를 복사했습니다.");
        } catch (_) {
          showToast("브라우저가 복사를 허용하지 않았습니다.");
        }
      });
    });
    content.querySelectorAll("[data-roster-code-reissue]").forEach((button) => {
      button.addEventListener("click", () => {
        const code = state.studentCodes.find((item) => item.studentId === button.dataset.rosterCodeReissue);
        if (code) reissueStudentCode(code).catch((error) => showToast(error.message));
      });
    });
    dialog.showModal();
  }

  async function toggleStudentRosterFullscreen() {
    const dialog = $("student-roster-dialog");
    if (!dialog) return;
    try {
      if (document.fullscreenElement === dialog) {
        await document.exitFullscreen();
      } else if (dialog.requestFullscreen) {
        await dialog.requestFullscreen();
      } else {
        dialog.classList.toggle("fullscreen-mode");
      }
    } catch (_) {
      dialog.classList.toggle("fullscreen-mode");
    }
  }

  function printStudentRoster() {
    const dialog = $("student-roster-dialog");
    if (!dialog) return;
    document.body.classList.add("printing-roster");
    window.setTimeout(() => window.print(), 0);
    window.setTimeout(() => document.body.classList.remove("printing-roster"), 1000);
  }

  async function reissueStudentCode(code) {
    if (!state.teacher?.isAdmin) {
      throw new Error("학생 코드는 관리자만 재발급할 수 있습니다.");
    }
    if (!await askConfirmation("학생 코드 재발급", `${code.studentDisplayName} 학생의 기존 코드를 폐기하고 새 코드를 발급할까요?`, "새 코드 발급")) return;
    const ticket = await api(`/api/classes/${code.classId}/enrollment-tickets`, {
      method: "POST",
      body: { studentId: code.studentId, studentDisplayName: code.studentDisplayName, studentNumber: code.studentNumber }
    });
    showToast(`${code.studentDisplayName} 학생의 새 코드를 발급했습니다: ${ticket.joinCode}`);
    const rosterClassId = state.studentRosterClassId;
    const rosterDialog = $("student-roster-dialog");
    if (rosterDialog?.open) rosterDialog.close();
    await loadStudentCodes();
    if (rosterClassId) openStudentRoster(rosterClassId);
  }

  async function loadAdminDirectory() {
    if (!state.teacher?.isAdmin) return;
    const list = $("admin-list");
    if (list) list.innerHTML = '<div class="empty-state">관리자 목록을 불러오는 중입니다…</div>';
    if (!state.studentCodes.length) await loadStudentCodes();
    const [directory, exitPinStatus, guestPasswordStatus] = await Promise.all([
      api("/api/admin/teachers"),
      api("/api/admin/student-exit-pin"),
      api("/api/admin/guest-password")
    ]);
    state.adminDirectory = directory;
    state.studentExitPinStatus = exitPinStatus;
    state.guestPasswordStatus = guestPasswordStatus;
    renderStudentAdminOptions();
    renderAdminDirectory();
    renderStudentExitPinStatus();
    renderGuestPasswordStatus();
  }

  function renderStudentExitPinStatus() {
    const target = $("student-exit-pin-status");
    if (!target) return;
    const status = state.studentExitPinStatus;
    target.textContent = status?.configured
      ? `설정됨 · 마지막 변경 ${formatTime(status.updatedAtUtc)}`
      : "미설정 · 학생 앱은 종료 비밀번호 없이 종료할 수 없습니다.";
  }

  function renderGuestPasswordStatus() {
    const target = $("guest-password-status");
    if (!target) return;
    const status = state.guestPasswordStatus;
    target.textContent = status?.configured
      ? `설정됨 · 마지막 변경 ${formatTime(status.updatedAtUtc)}`
      : "미설정 · 학교 게스트 로그인을 사용하려면 비밀번호를 설정하세요.";
  }

  async function loadOperationsStatus() {
    if (!state.teacher?.isAdmin) return;
    const target = $("operations-status");
    if (target) target.innerHTML = '<div class="empty-state">상태를 확인하는 중입니다…</div>';
    try {
      state.operationsStatus = await api("/api/admin/operations-status");
      renderOperationsStatus(state.operationsStatus);
    } catch (error) {
      state.operationsStatus = null;
      renderOperationsStatus({ error: error.message });
    }
  }

  function renderOperationsStatus(status) {
    const target = $("operations-status");
    if (!target) return;
    if (!status || status.error) {
      target.innerHTML = `<div class="operations-unavailable">운영 상태를 확인하지 못했습니다. ${escapeHtml(status?.error || "관리자 API를 확인해 주세요.")}</div>`;
      return;
    }
    const items = [
      ["학교 검색", status.schoolSearch?.configured, status.schoolSearch?.label || "NEIS 인증키 필요"],
      ["확인 메일", status.emailVerification?.configured, status.emailVerification?.label || "Resend 설정 필요"],
      ["학생 상태", status.studentStatus?.configured, status.studentStatus?.screenSharingAvailable === false ? "보이는 상태 제공 · 화면 공유 없음" : "연결됨"]
    ];
    target.innerHTML = items.map(([label, ready, copy]) => `<div class="operation-item"><span class="operation-indicator ${ready ? "ready" : "pending"}">${ready ? "✓" : "!"}</span><div><strong>${escapeHtml(label)}</strong><small>${escapeHtml(copy)}</small></div><span class="operation-state ${ready ? "ready" : "pending"}">${ready ? "준비됨" : "설정 필요"}</span></div>`).join("");
  }

  function renderAdminDirectory() {
    const list = $("admin-list");
    if (!list || !state.adminDirectory) return;
    const teachers = Array.isArray(state.adminDirectory.teachers) ? state.adminDirectory.teachers : [];
    const grants = Array.isArray(state.adminDirectory.grants) ? state.adminDirectory.grants : [];
    const students = Array.isArray(state.adminDirectory.students) ? state.adminDirectory.students : [];
    const known = new Set(teachers.map((teacher) => [teacher.email, teacher.loginName].filter(Boolean).map((value) => value.toLowerCase())) .flat());
    const pending = grants.filter((grant) => !known.has(String(grant.identifier || "").toLowerCase()));
    const teacherRows = teachers.map((teacher) => {
      const identifier = teacher.email || teacher.loginName;
      const canRemove = teacher.isAdmin && teacher.teacherId !== state.teacher?.teacherId;
      return `<div class="admin-row"><div><strong>${escapeHtml(teacher.displayName)}</strong><small>${escapeHtml(teacher.email || teacher.loginName)}</small></div><span class="admin-badge ${teacher.isAdmin ? "active" : ""}">${teacher.isAdmin ? "관리자" : "선생님"}</span>${canRemove ? `<button class="ghost-button admin-remove-button" type="button" data-admin-remove="${escapeHtml(identifier)}">해제</button>` : ""}</div>`;
    }).join("");
    const pendingRows = pending.map((grant) => `<div class="admin-row pending"><div><strong>${escapeHtml(grant.identifier)}</strong><small>아직 가입하지 않은 계정 · ${formatTime(grant.createdAtUtc)}</small></div><span class="admin-badge active">가입 시 관리자</span><button class="ghost-button admin-remove-button" type="button" data-admin-remove="${escapeHtml(grant.identifier)}">해제</button></div>`).join("");
    const studentAdminRows = students.filter((student) => student.isAdmin).map((student) => `<div class="admin-row"><div><strong>${escapeHtml(student.studentDisplayName)}</strong><small>${escapeHtml(`${student.grade || "—"}학년 ${student.classNumber || "—"}반 · ${student.studentNumber || "—"}번`)}</small></div><span class="admin-badge active">학생 관리자</span><button class="ghost-button admin-remove-button" type="button" data-student-admin-remove="${escapeHtml(studentAdminKey(student))}">해제</button></div>`).join("");
    list.innerHTML = `<div class="admin-list-heading"><strong>학교 계정</strong><span class="muted small">${teachers.length}명</span></div>${teacherRows || '<div class="empty-state">아직 등록된 선생님이 없습니다.</div>'}${pendingRows ? `<div class="admin-list-heading pending-heading"><strong>가입 대기 권한</strong></div>${pendingRows}` : ""}<div class="admin-list-heading pending-heading"><strong>학생 관리자</strong><span class="muted small">${students.filter((student) => student.isAdmin).length}명</span></div>${studentAdminRows || '<div class="empty-state">지정된 학생 관리자가 없습니다.</div>'}`;
    list.querySelectorAll("[data-admin-remove]").forEach((button) => {
      button.addEventListener("click", () => updateAdminAccess(button.dataset.adminRemove, false).catch((error) => showToast(error.message)));
    });
    list.querySelectorAll("[data-student-admin-remove]").forEach((button) => {
      const student = students.find((item) => studentAdminKey(item) === button.dataset.studentAdminRemove);
      if (student) button.addEventListener("click", () => updateStudentAdminAccess(student, false).catch((error) => showToast(error.message)));
    });
  }

  async function updateAdminAccess(identifier, isAdmin) {
    if (!identifier) return;
    if (!isAdmin && !await askConfirmation("관리자 권한 해제", `${identifier} 계정의 관리자 권한을 해제할까요?`, "권한 해제")) return;
    await api("/api/admin/teachers", { method: "POST", body: { kind: "teacher", identifier, isAdmin } });
    showToast(isAdmin ? "관리자 권한을 부여했습니다." : "관리자 권한을 해제했습니다.");
    await loadAdminDirectory();
  }

  async function updateStudentAdminAccess(student, isAdmin) {
    if (!student?.studentId || !student.classId) return;
    if (!isAdmin && !await askConfirmation("학생 관리자 권한 해제", `${student.studentDisplayName} 학생의 학생 관리자 권한을 해제할까요?`, "권한 해제")) return;
    await api("/api/admin/teachers", {
      method: "POST",
      body: { kind: "student", studentId: student.studentId, classId: student.classId, isAdmin }
    });
    showToast(isAdmin ? `${student.studentDisplayName} 학생에게 학생 관리자 권한을 지정했습니다.` : "학생 관리자 권한을 해제했습니다.");
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
    const url = runtimeConfig.studentInstallerUrl || "https://classroom-2en.pages.dev/student";
    window.open(url, "_blank", "noopener,noreferrer");
    showToast("학생용 설치 패키지 다운로드를 시작했습니다.");
  }

  function openCommandDialog(kind, targetIds = null) {
    if (state.teacher?.isGuest) {
      showToast("게스트 계정은 읽기 전용입니다.");
      return;
    }
    if (!state.session) {
      showToast("먼저 수업을 시작하세요.");
      return;
    }
    state.commandKind = kind;
    state.commandTargetIds = Array.isArray(targetIds) ? [...targetIds] : null;
    const directStudent = kind === "message" && state.commandTargetIds?.length === 1
      ? state.students.find((student) => student.deviceId === state.commandTargetIds[0])
      : null;
    $("dialog-title").textContent = kind === "url"
      ? "URL 열기"
      : kind === "app"
        ? "승인된 앱 실행"
        : directStudent
          ? "개인 메시지 보내기"
          : "메시지 보내기";
    const targetCount = state.commandTargetIds?.length || state.students.length;
    $("command-audience").textContent = state.commandTargetIds?.length
      ? directStudent
        ? `${directStudent.studentDisplayName} 학생에게만 전달합니다.`
        : `선택한 학생 ${targetCount}명에게만 전달합니다.`
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
    if (state.teacher?.isGuest) throw new Error("게스트 계정은 학생 장치에 명령을 보낼 수 없습니다.");
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

  async function refreshScreenWall() {
    if (!state.classId || !state.screenShareTargetIds?.length || !state.screenWallOpen || state.screenWallRefreshInFlight) return;
    state.screenWallRefreshInFlight = true;
    try {
      const result = await api(`/api/classes/${state.classId}/screens`);
      const frames = Array.isArray(result) ? result : result?.screens || [];
      const allowedTargets = new Set(state.screenShareTargetIds);
      state.screenFrames = new Map(frames.filter((frame) => allowedTargets.has(frame.deviceId)).map((frame) => [frame.deviceId, frame]));
      renderScreenWall();
      if (state.detailView === "screen" && !$("detail-pane").hidden) renderDetail();
    } finally {
      state.screenWallRefreshInFlight = false;
    }
  }

  async function openStudentScreen(deviceId = null) {
    if (state.teacher?.isGuest) {
      showToast("게스트 계정에서는 화면 공유를 사용할 수 없습니다.");
      return;
    }
    if (!state.session) {
      showToast("먼저 수업을 시작하세요.");
      return;
    }
    if (deviceId && state.screenWallOpen && state.screenShareTargetIds?.includes(deviceId)) {
      openDetail(deviceId, "screen");
      return;
    }
    const requestedTargets = deviceId && state.screenWallOpen && state.screenShareTargetIds?.length
      ? [...state.screenShareTargetIds, deviceId]
      : deviceId
        ? [deviceId]
        : (commandTargets() || state.students.map((student) => student.deviceId));
    const targetIds = requestedTargets.filter((targetId) => state.students.some((student) => student.deviceId === targetId && student.online));
    if (!targetIds.length) {
      showToast("온라인인 학생의 화면만 볼 수 있습니다.");
      return;
    }
    if (targetIds.length > 30) {
      showToast("한 번에 최대 30명의 화면을 볼 수 있습니다. 일부 학생을 선택해 주세요.");
      return;
    }
    state.screenShareTargetIds = [...new Set(targetIds)];
    state.monitorPage = 0;
    state.screenWallOpen = true;
    state.detailView = "status";
    renderScreenWall();
    try {
      const refreshInterval = monitorRefreshIntervalMs(state.screenShareTargetIds.length);
      await sendCommand("screenShare", state.screenShareTargetIds, {
        screenShareEnabled: true,
        screenShareIntervalMilliseconds: refreshInterval
      });
      await refreshScreenWall();
      if (state.screenWallTimer) clearInterval(state.screenWallTimer);
      state.screenWallTimer = setInterval(() => refreshScreenWall().catch(() => {}), refreshInterval);
      if (deviceId) openDetail(deviceId, "screen");
    } catch (error) {
      await stopScreenSharing();
      throw error;
    }
  }

  async function openScreenWall() {
    if (state.screenWallOpen) {
      await stopScreenSharing();
      return;
    }
    await openStudentScreen();
  }

  async function toggleScreenWallFullscreen() {
    const wall = $("monitor-stage");
    if (!wall || !state.screenWallOpen) return;
    try {
      if (document.fullscreenElement === wall) {
        await document.exitFullscreen();
      } else if (wall.requestFullscreen) {
        await wall.requestFullscreen();
      } else {
        wall.classList.toggle("fullscreen-mode");
      }
    } catch (_) {
      // Browsers can reject the Fullscreen API after an async command starts.
      // Keep the monitor wall usable with the in-page fallback in that case.
      wall.classList.toggle("fullscreen-mode");
    }
  }

  async function openMonitorFullscreen() {
    if (!state.screenWallOpen) {
      await openStudentScreen();
    }
    await toggleScreenWallFullscreen();
  }

  async function stopScreenSharing() {
    if (state.stoppingScreenShare) return;
    state.stoppingScreenShare = true;
    const targets = state.screenShareTargetIds ? [...state.screenShareTargetIds] : [];
    if (state.screenWallTimer) clearInterval(state.screenWallTimer);
    state.screenWallTimer = null;
    state.screenWallOpen = false;
    state.screenWallRefreshInFlight = false;
    state.screenShareTargetIds = null;
    state.monitorPage = 0;
    state.screenFrames.clear();
    try {
      if (targets.length && state.session) {
        await sendCommand("screenShare", targets, { screenShareEnabled: false });
      }
    } catch (error) {
      showToast(error.message);
    } finally {
      if (state.detailView === "screen" && !$("detail-pane").hidden) {
        state.detailView = "status";
        renderDetail();
      }
      renderScreenWall();
      state.stoppingScreenShare = false;
    }
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
    if (!state.session || !await askConfirmation("수업 종료", "현재 수업을 종료할까요?", "수업 종료")) return;
    if (state.screenShareTargetIds?.length) await stopScreenSharing(true);
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
    const text = `Classroom 학생 PC 등록\n학생: ${state.enrollmentBundle.studentDisplayName}\n학생 코드: ${state.enrollmentBundle.joinCode}\n1. 관리자 메뉴에서 Classroom.Student.Setup.exe를 내려받습니다.\n2. 설치 앱을 실행합니다.\n3. 학생 코드 ${state.enrollmentBundle.joinCode}를 입력하고 관리자 권한을 승인합니다.`;
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
    const date = new Date(value);
    if (!value || Number.isNaN(date.getTime())) return "시간 확인 필요";
    return date.toLocaleString("ko-KR", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
  }

  function renderTodayInfo() {
    const now = new Date();
    const date = $("current-date");
    if (date) date.textContent = now.toLocaleDateString("ko-KR", { month: "long", day: "numeric", weekday: "short" });
  }

  function describeWeather(code) {
    if (code === 0) return { icon: "☀️", label: "맑음" };
    if (code === 1) return { icon: "🌤️", label: "대체로 맑음" };
    if (code === 2) return { icon: "⛅", label: "구름 많음" };
    if (code === 3) return { icon: "☁️", label: "흐림" };
    if ([45, 48].includes(code)) return { icon: "🌫️", label: "안개" };
    if ([51, 53, 55, 56, 57].includes(code)) return { icon: "🌦️", label: "이슬비" };
    if ([61, 63, 65, 66, 67, 80, 81, 82].includes(code)) return { icon: "🌧️", label: "비" };
    if ([71, 73, 75, 77, 85, 86].includes(code)) return { icon: "🌨️", label: "눈" };
    if ([95, 96, 99].includes(code)) return { icon: "⛈️", label: "뇌우" };
    return { icon: "🌤️", label: "날씨 변동" };
  }

  function setWeatherState(icon, description, temperature = "") {
    const iconTarget = $("weather-icon");
    const descriptionTarget = $("weather-description");
    const temperatureTarget = $("weather-temperature");
    if (iconTarget) iconTarget.textContent = icon;
    if (descriptionTarget) descriptionTarget.textContent = description;
    if (temperatureTarget) temperatureTarget.textContent = temperature;
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
      const weather = describeWeather(code);
      const temperature = Number.isFinite(Number(current?.temperature_2m))
        ? `${Math.round(Number(current.temperature_2m))}°C`
        : "";
      setWeatherState(weather.icon, weather.label, temperature);
    };
    if (!navigator.geolocation) {
      setWeatherState("—", "위치 권한 필요");
      return;
    }
    navigator.geolocation.getCurrentPosition(
      (position) => load(position.coords.latitude, position.coords.longitude).catch(() => { setWeatherState("—", "날씨 확인 불가"); }),
      () => { setWeatherState("—", "위치 권한 필요"); },
      { enableHighAccuracy: false, maximumAge: 15 * 60 * 1000, timeout: 5000 }
    );
  }

  function escapeHtml(value) {
    return String(value ?? "").replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
  }

  function showAuth(mode = "school") {
    landingView.hidden = true;
    loginView.hidden = false;
    appView.hidden = true;
    if (mode === "school") {
      setAuthEntry("school");
    } else {
      setAuthEntry("admin");
      setAuthMode(mode === "signup" ? "signup" : "login");
    }
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  function showLanding() {
    landingView.hidden = false;
    loginView.hidden = true;
    appView.hidden = true;
    window.scrollTo({ top: 0, behavior: "smooth" });
  }

  async function finishConsoleExit(logout) {
    const dialog = $("console-close-dialog");
    if (dialog?.open) dialog.close(logout ? "logout" : "close");
    if (logout) {
      try { await api("/auth/logout", { method: "POST" }); } catch (_) { /* local logout still clears the token */ }
      try { await window.ClassroomFirebaseAuth?.signOut(); } catch (_) { /* local logout still clears the token */ }
      clearSession();
    }
    try { window.close(); } catch (_) { /* browsers may block closing a user-opened tab */ }
    window.setTimeout(() => {
      if (window.closed) return;
      if (logout) {
        showLanding();
        showToast("로그아웃했습니다. 이 탭은 브라우저에서 닫아 주세요.");
      } else {
        showToast("브라우저가 탭 닫기를 막았습니다. 로그인 상태는 유지됩니다.");
      }
    }, 160);
  }

  function openConsoleCloseDialog() {
    if (!state.teacher || state.teacher.isGuest) return;
    const dialog = $("console-close-dialog");
    if (dialog && !dialog.open) dialog.showModal();
  }

  function setAuthEntry(entry = "school") {
    const isSchoolEntry = entry !== "admin";
    $("school-login-panel").hidden = !isSchoolEntry;
    $("admin-auth-panel").hidden = isSchoolEntry;
    if (isSchoolEntry) {
      $("school-login-error").hidden = true;
    }
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
    $("school-login-error").hidden = true;
  }

  const legalDocuments = {
    terms: {
      kicker: "TERMS OF SERVICE",
      title: "Classroom 이용약관",
      html: `<p>시행일: 2026년 8월 30일</p><h3>1. 서비스 목적</h3><p>Classroom은 학교 수업에서 학생 PC의 연결 상태를 확인하고, 수업 안내·집중 모드·승인된 링크 및 앱 실행을 전달하기 위한 교사용 운영 도구입니다.</p><h3>2. 계정과 권한</h3><p>교사 계정은 본인만 사용해야 하며, 관리자는 학교 운영에 필요한 범위에서 다른 교사의 관리자 권한을 지정하거나 해제할 수 있습니다. 학생 코드는 학생 PC 등록 목적으로만 사용해야 하며, 노출된 코드는 즉시 재발급해야 합니다.</p><h3>3. 허용되는 기능 범위</h3><p>서비스는 수업 운영에 필요한 상태 확인과 명령 전달을 제공합니다. 교사가 수업 중 화면 보기를 직접 켠 경우에만 학생 PC의 최대 720p 자동 화질 화면이 표시되며, 학생 앱에도 화면 공유 중임을 표시합니다. 키 입력, 오디오 수집, 임의 원격 셸 실행, 개인 파일 열람 기능은 제공하지 않습니다.</p><h3>4. 학교의 책임</h3><p>학교·교육기관은 학생과 보호자에게 서비스 사용 사실, 관리 범위, 자체 운영 기준을 알리고 필요한 동의 절차를 갖추어야 합니다.</p><h3>5. 이용 제한</h3><p>타인의 계정을 사용하거나, 학생의 교육 목적과 무관한 감시·통제에 서비스를 이용해서는 안 됩니다. 보안상 우려가 있는 이용은 제한될 수 있습니다.</p>`
    },
    privacy: {
      kicker: "PRIVACY NOTICE",
      title: "개인정보처리방침",
      html: `<p>시행일: 2026년 8월 30일</p><h3>1. 수집하는 정보</h3><p>교사 계정의 이메일·이름·담당 과목, 학급명, 학생 표시 이름, 학생 PC 이름·연결 시각·현재 앱·창 제목·설정된 웹 도메인·배터리·네트워크 상태, 수업 명령 및 감사 기록을 처리합니다. 교사가 수업 중 화면 보기를 켠 동안에는 최대 720p 자동 화질 화면 프레임을 일시 처리합니다.</p><h3>2. 이용 목적</h3><p>교사 인증, 학급 운영, 학생 PC 등록, 수업 안내 전달, 연결 상태와 수업 참여 화면 확인, 보안 감사 및 장애 대응에만 사용합니다.</p><h3>3. 보관 기간</h3><p>교사·학생·수업 데이터는 학교 관리자가 삭제하거나 서비스 운영 목적이 종료될 때까지 보관합니다. 화면 프레임은 데이터베이스나 감사 기록에 저장하지 않고 메모리에서 약 15초 이내에 만료합니다. 세부 보관 기간은 학교의 정보보호·기록 관리 규정에 맞춰 운영해야 합니다.</p><h3>4. 안전성</h3><p>인증 토큰은 서버에 해시 형태로 보관하며, 전송은 HTTPS/WSS로 보호합니다. 화면 공유 중에는 학생 앱에 이를 명확히 표시하고, 서비스는 키 입력·오디오·개인 파일·임의 원격 셸을 수집하거나 실행하지 않습니다.</p><h3>5. 이용자 권리와 문의</h3><p>정보 주체는 학교 관리자에게 열람·정정·삭제 요청을 할 수 있습니다. 실제 학교 도입 전에는 해당 학교의 개인정보 보호책임자와 연락처를 별도로 고지해야 합니다.</p>`
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

  function openGuestLoginDialog() {
    const dialog = $("guest-login-dialog");
    if (!dialog) return;
    $("guest-login-form").reset();
    setSchoolControls("guest-school-search", "guest-school-results", "guest-school-id", null);
    $("guest-login-error").hidden = true;
    if (!dialog.open) dialog.showModal();
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
    state.classes = Array.isArray(result.classes) ? result.classes : [];
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
      let reloadedForUpdate = false;
      navigator.serviceWorker.addEventListener("controllerchange", () => {
        if (reloadedForUpdate) return;
        reloadedForUpdate = true;
        window.location.reload();
      });
      navigator.serviceWorker.register("/sw.js", { updateViaCache: "none" })
        .then((registration) => {
          const activateWaitingWorker = () => registration.waiting?.postMessage({ type: "SKIP_WAITING" });
          activateWaitingWorker();
          registration.addEventListener("updatefound", () => {
            const installing = registration.installing;
            installing?.addEventListener("statechange", () => {
              if (installing.state === "installed" && navigator.serviceWorker.controller) activateWaitingWorker();
            });
          });
          registration.update().catch(() => {});
          window.setInterval(() => registration.update().catch(() => {}), 5 * 60 * 1000);
        })
        .catch(() => {});
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

  function compareVersions(left, right) {
    const a = String(left || "").split(".").map((part) => Number.parseInt(part, 10) || 0);
    const b = String(right || "").split(".").map((part) => Number.parseInt(part, 10) || 0);
    const length = Math.max(a.length, b.length);
    for (let index = 0; index < length; index += 1) {
      if ((a[index] || 0) > (b[index] || 0)) return 1;
      if ((a[index] || 0) < (b[index] || 0)) return -1;
    }
    return 0;
  }

  function setUpdateStatus(message, stateName = "") {
    const target = $("update-status");
    if (!target) return;
    target.textContent = message;
    target.dataset.state = stateName;
  }

  async function refreshStudentAppVersion() {
    const target = $("student-agent-version");
    if (!target) return;
    try {
      const response = await fetch(`/classroom-update.json?now=${Date.now()}`, { cache: "no-store" });
      if (!response.ok) throw new Error("version unavailable");
      const payload = await response.json();
      target.textContent = payload?.version ? `v${payload.version}` : "확인 불가";
    } catch (_) {
      target.textContent = "오프라인";
    }
  }

  async function checkForAppUpdate(showFeedback = false) {
    const button = $("check-update-button");
    if ($("console-version")) $("console-version").textContent = `v${APP_VERSION}`;
    if (showFeedback && button) {
      button.disabled = true;
      button.textContent = "확인 중…";
      setUpdateStatus("교사 콘솔과 학생 앱 버전을 확인하고 있습니다.", "checking");
    }
    try {
      const [response] = await Promise.all([
        fetch(`/version.json?now=${Date.now()}`, { cache: "no-store" }),
        refreshStudentAppVersion()
      ]);
      if (!response.ok) throw new Error("version unavailable");
      const version = await response.json();
      if (version?.version && compareVersions(version.version, APP_VERSION) > 0) {
        setUpdateStatus(`v${version.version} 업데이트를 적용하고 있습니다.`, "available");
        const registration = await navigator.serviceWorker?.getRegistration();
        await registration?.update().catch(() => {});
        registration?.waiting?.postMessage({ type: "SKIP_WAITING" });
        window.setTimeout(() => window.location.reload(), 600);
        return;
      }
      const checkedAt = new Intl.DateTimeFormat("ko-KR", { hour: "2-digit", minute: "2-digit" }).format(new Date());
      setUpdateStatus(`최신 버전입니다 · ${checkedAt} 확인`, "current");
      if (showFeedback) showToast(`교사 콘솔 v${APP_VERSION}은 최신 버전입니다.`);
    } catch (_) {
      setUpdateStatus("오프라인 상태입니다. 연결되면 자동으로 다시 확인합니다.", "offline");
      if (showFeedback) showToast("업데이트 서버에 연결할 수 없습니다.");
    } finally {
      if (button) {
        button.disabled = false;
        button.textContent = "업데이트 확인";
      }
    }
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
    sessionStorage.removeItem(PENDING_FIREBASE_ENTRY_KEY);
    applySessionToken(result);
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

  function setSchoolLoginStatus(message, isError = false) {
    const target = $("school-login-status");
    target.textContent = message;
    target.classList.toggle("error", isError);
  }

  async function runGoogleLogin(buttonId, entry, signupProfile = {}) {
    const button = $(buttonId);
    const isSchoolEntry = entry === "school";
    const signupMode = !isSchoolEntry && !$("signup-panel").hidden;
    const errorTarget = isSchoolEntry
      ? $("school-login-error")
      : signupMode ? $("signup-error") : $("login-error");
    errorTarget.hidden = true;
    button.disabled = true;
    try {
      if (signupMode && (!signupProfile.termsAccepted || !signupProfile.privacyAccepted)) {
        throw new Error("이용약관과 개인정보처리방침 동의가 필요합니다.");
      }
      if (signupMode) {
        sessionStorage.setItem("classroom.pendingFirebaseProfile", JSON.stringify(signupProfile));
      } else {
        sessionStorage.removeItem("classroom.pendingFirebaseProfile");
      }
      sessionStorage.setItem(
        PENDING_FIREBASE_ENTRY_KEY,
        isSchoolEntry ? "school" : signupMode ? "admin-signup" : "admin-login");
      const credentials = await firebaseClient().signInGoogle();
      if (credentials) await finishFirebaseLogin(credentials, signupProfile);
    } catch (error) {
      // Keep the user on the same entry point after popup/redirect failures.
      // In particular, a successful Google credential must never fall through
      // to the landing page before the Classroom session exchange finishes.
      showAuth(isSchoolEntry ? "school" : signupMode ? "signup" : "login");
      if (error.code === "auth/popup-closed-by-user" || error.code === "auth/cancelled-popup-request") {
        if (isSchoolEntry) setSchoolLoginStatus("학교 로그인을 취소했습니다.");
        else setFirebaseStatus("Google 로그인을 취소했습니다.");
      } else {
        errorTarget.textContent = error.message;
        errorTarget.hidden = false;
      }
    } finally {
      button.disabled = false;
    }
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
        applySessionToken(result);
        await loadTeacher();
      }
    } catch (error) {
      loginError.textContent = error.message;
      loginError.hidden = false;
    } finally {
      setAuthBusy(form, false);
    }
  });

  $("guest-login-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const errorTarget = $("guest-login-error");
    errorTarget.hidden = true;
    const schoolId = $("guest-school-id").value.trim();
    const password = $("guest-login-password").value;
    if (!schoolId) {
      errorTarget.textContent = "학교 검색 결과에서 학교를 선택해 주세요.";
      errorTarget.hidden = false;
      return;
    }
    if (password.length < 6) {
      errorTarget.textContent = "게스트 비밀번호를 입력해 주세요.";
      errorTarget.hidden = false;
      return;
    }
    setAuthBusy(form, true);
    try {
      const result = await api("/auth/guest-login", {
        method: "POST",
        body: { schoolId, password }
      });
      applySessionToken(result, true);
      $("guest-login-dialog").close("success");
      await loadTeacher();
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
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

  $("school-login-button").addEventListener("click", openGuestLoginDialog);
  $("google-login-button").addEventListener("click", () => {
    const signupProfile = !$("signup-panel").hidden
      ? { termsAccepted: $("signup-terms").checked, privacyAccepted: $("signup-privacy").checked }
      : {};
    return runGoogleLogin("google-login-button", "admin", signupProfile);
  });

  setupSchoolSearch("guest-school-search", "guest-school-results", "guest-school-id");

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
    button.addEventListener("click", () => {
      setAuthEntry("admin");
      setAuthMode(button.dataset.authMode);
    });
  });
  $("landing-login-button").addEventListener("click", () => showAuth("school"));
  $("landing-student-installer-button").addEventListener("click", downloadStudentInstaller);
  $("landing-install-button").addEventListener("click", installApp);
  $("landing-start-button").addEventListener("click", () => showAuth("signup"));
  $("principles-login-button").addEventListener("click", () => showAuth("school"));
  $("landing-cta-button").addEventListener("click", () => showAuth("signup"));
  $("back-to-landing").addEventListener("click", (event) => { event.preventDefault(); showLanding(); });
  $("admin-login-choice").addEventListener("click", () => showAuth("admin"));
  $("school-login-back").addEventListener("click", () => showAuth("school"));
  $("school-student-installer-button").addEventListener("click", downloadStudentInstaller);
  $("install-app-button").addEventListener("click", installApp);
  $("settings-install-button").addEventListener("click", installApp);
  $("check-update-button").addEventListener("click", () => checkForAppUpdate(true));
  $("dismiss-install-button").addEventListener("click", () => {
    localStorage.setItem("classroom.dismissInstallPrompt", "1");
    syncInstallUi();
  });
  document.querySelectorAll("[data-legal-document]").forEach((button) => {
    button.addEventListener("click", () => openLegalDocument(button.dataset.legalDocument));
  });
  $("theme-toggle").addEventListener("click", toggleTheme);
  $("close-console-button").addEventListener("click", openConsoleCloseDialog);
  document.querySelectorAll("[data-theme-choice]").forEach((button) => {
    button.addEventListener("click", () => applyTheme(button.dataset.themeChoice));
  });

  $("logout-button").addEventListener("click", async () => {
    if (state.screenShareTargetIds?.length) await stopScreenSharing(true);
    try { await api("/auth/logout", { method: "POST" }); } catch (_) { /* local logout still clears the token */ }
    try { await window.ClassroomFirebaseAuth?.signOut(); } catch (_) { /* local logout still clears the token */ }
    clearSession();
  });
  $("class-select-button").addEventListener("click", toggleClassPicker);
  $("class-select-menu").addEventListener("click", (event) => {
    const option = event.target.closest("[data-class-option]");
    if (option) chooseClass(option.dataset.classOption).catch((error) => showToast(error.message));
  });
  $("class-select").addEventListener("change", (event) => chooseClass(event.target.value).catch((error) => showToast(error.message)));
  document.addEventListener("click", (event) => {
    if (!(event.target instanceof Element) || !event.target.closest(".custom-class-picker")) closeClassPicker();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") closeClassPicker();
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
  $("screen-wall-button").addEventListener("click", () => openScreenWall().catch((error) => showToast(error.message)));
  $("screen-wall-stop").addEventListener("click", () => stopScreenSharing().catch((error) => showToast(error.message)));
  $("screen-wall-fullscreen").addEventListener("click", () => toggleScreenWallFullscreen().catch(() => showToast("전체 화면을 사용할 수 없습니다.")));
  $("monitor-fullscreen-fab").addEventListener("click", () => openMonitorFullscreen().catch((error) => showToast(error.message)));
  $("monitor-fullscreen-exit").addEventListener("click", () => {
    const wall = $("monitor-stage");
    if (document.fullscreenElement === wall) {
      document.exitFullscreen().catch(() => wall?.classList.remove("fullscreen-mode"));
    } else {
      wall?.classList.remove("fullscreen-mode");
    }
  });
  $("clear-selection-button").addEventListener("click", () => {
    state.selectedDeviceIds.clear();
    renderStudents();
    renderSelection();
  });
  $("class-sync-retry").addEventListener("click", () => refreshClass().catch((error) => showToast(error.message)));
  $("student-search").addEventListener("input", (event) => {
    state.search = event.target.value.trim();
    renderStudents();
  });
  $("student-sort").addEventListener("change", (event) => {
    state.studentSort = event.target.value;
    localStorage.setItem("classroom.studentSort", state.studentSort);
    renderStudents();
  });
  $("student-code-search").addEventListener("input", renderStudentCodes);
  $("student-code-refresh").addEventListener("click", () => loadStudentCodes().catch((error) => showToast(error.message)));
  $("student-admin-search").addEventListener("input", renderStudentAdminOptions);
  $("admin-teacher-tab").addEventListener("click", () => {
    $("admin-teacher-tab").classList.add("active");
    $("admin-student-tab").classList.remove("active");
    $("admin-teacher-tab").setAttribute("aria-selected", "true");
    $("admin-student-tab").setAttribute("aria-selected", "false");
    $("teacher-admin-panel").hidden = false;
    $("student-admin-panel").hidden = true;
  });
  $("admin-student-tab").addEventListener("click", () => {
    $("admin-student-tab").classList.add("active");
    $("admin-teacher-tab").classList.remove("active");
    $("admin-student-tab").setAttribute("aria-selected", "true");
    $("admin-teacher-tab").setAttribute("aria-selected", "false");
    $("teacher-admin-panel").hidden = true;
    $("student-admin-panel").hidden = false;
    renderStudentAdminOptions();
  });
  $("student-admin-submit").addEventListener("click", async () => {
    const key = $("student-admin-select").value;
    const student = state.studentCodes.find((item) => studentAdminKey(item) === key);
    const errorTarget = $("admin-error");
    errorTarget.hidden = true;
    try {
      if (!student) throw new Error("관리자 권한을 줄 학생을 선택해 주세요.");
      await updateStudentAdminAccess(student, true);
      $("student-admin-select").value = "";
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });
  $("admin-enroll-button").addEventListener("click", openEnrollmentDialog);
  $("student-installer-download").addEventListener("click", downloadStudentInstaller);
  $("student-roster-fullscreen").addEventListener("click", () => toggleStudentRosterFullscreen().catch(() => showToast("전체화면을 사용할 수 없습니다.")));
  $("student-roster-print").addEventListener("click", printStudentRoster);
  $("class-form").addEventListener("submit", (event) => {
    event.preventDefault();
    createClassFromAdmin();
  });
  $("roster-import-form").addEventListener("submit", (event) => {
    event.preventDefault();
    importRoster();
  });
  $("close-detail").addEventListener("click", () => closeDetail().catch((error) => showToast(error.message)));
  $("confirm-dialog").addEventListener("close", (event) => {
    const resolver = state.confirmResolver;
    state.confirmResolver = null;
    if (resolver) resolver(event.currentTarget.returnValue === "confirm");
  });
  $("console-exit-yes").addEventListener("click", () => finishConsoleExit(false));
  $("console-exit-logout").addEventListener("click", () => finishConsoleExit(true));
  $("console-exit-cancel").addEventListener("click", () => $("console-close-dialog").close("cancel"));
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
    closeClassPicker();
    state.activeSection = button.dataset.section;
    document.querySelectorAll(".nav-item").forEach((item) => item.classList.remove("active"));
    button.classList.add("active");
    document.querySelectorAll(".section-view").forEach((section) => { section.hidden = section.id !== `${button.dataset.section}-section`; });
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
  $("student-exit-pin-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const errorTarget = $("student-exit-pin-error");
    errorTarget.hidden = true;
    const pin = $("student-exit-pin").value;
    const confirmation = $("student-exit-pin-confirm").value;
    try {
      if (pin.length < 6 || pin.length > 64) {
        throw new Error("종료 비밀번호는 6~64자로 입력해 주세요.");
      }
      if (pin !== confirmation) {
        throw new Error("종료 비밀번호가 일치하지 않습니다.");
      }
      state.studentExitPinStatus = await api("/api/admin/student-exit-pin", {
        method: "PUT",
        body: { pin }
      });
      $("student-exit-pin-form").reset();
      renderStudentExitPinStatus();
      showToast("학생 앱 종료 비밀번호를 저장했습니다.");
    } catch (error) {
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
    }
  });
  $("guest-password-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const errorTarget = $("guest-password-error");
    errorTarget.hidden = true;
    const password = $("guest-password").value;
    const confirmation = $("guest-password-confirm").value;
    try {
      if (password.length < 6 || password.length > 64) {
        throw new Error("게스트 비밀번호는 6~64자로 입력해 주세요.");
      }
      if (password !== confirmation) {
        throw new Error("게스트 비밀번호가 일치하지 않습니다.");
      }
      state.guestPasswordStatus = await api("/api/admin/guest-password", {
        method: "PUT",
        body: { password }
      });
      $("guest-password-form").reset();
      renderGuestPasswordStatus();
      showToast("학교 게스트 비밀번호를 저장했습니다.");
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
      const devLoginHint = $("dev-login-hint");
      const securitySetting = $("security-setting");
      if (devLoginHint) devLoginHint.hidden = true;
      if (securitySetting) {
        securitySetting.textContent = apiOrigin
          ? `암호화된 외부 API ${apiOrigin}에 연결됨`
          : health?.storage === "durable-object"
            ? "Cloudflare의 암호화된 영속 API에 연결됨"
            : "보안 세션으로 같은 서버에 연결됨";
      }
    })
    .catch(() => {
      const devLoginHint = $("dev-login-hint");
      if (devLoginHint) devLoginHint.hidden = true;
    });

  function refreshFirebaseAvailability() {
    const firebaseReady = window.ClassroomFirebaseAuth?.isConfigured() === true;
    $("google-login-button").disabled = !firebaseReady;
    $("school-login-button").disabled = false;
    setSchoolLoginStatus("학교 선택과 학교 비밀번호로 안전하게 연결합니다.");
    setFirebaseStatus(firebaseReady
      ? "관리자용 Google 로그인과 이메일 회원가입을 사용할 수 있습니다."
      : "관리자용 인증은 Firebase 설정 후 사용할 수 있습니다.");
    return firebaseReady;
  }

  applyTheme(state.theme);
  registerPwa();
  checkForAppUpdate();
  window.setInterval(checkForAppUpdate, 5 * 60 * 1000);
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") {
      checkForAppUpdate();
      if (hasTeacherSession()) refreshClass().catch(() => {});
    }
    if (hasTeacherSession()) startClassPolling();
  });
  window.addEventListener("online", () => {
    if (hasTeacherSession()) {
      refreshClass().catch(() => {});
      startClassPolling();
    }
  });
  window.addEventListener("offline", () => {
    if (!hasTeacherSession()) return;
    state.lastRefreshError = "인터넷 연결이 끊겼습니다.";
    renderRefreshStatus();
  });
  let monitorResizeTimer = null;
  window.addEventListener("resize", () => {
    if (!state.screenWallOpen) return;
    if (monitorResizeTimer) window.clearTimeout(monitorResizeTimer);
    monitorResizeTimer = window.setTimeout(() => {
      monitorResizeTimer = null;
      renderStudents();
    }, 120);
  });
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

  async function consumePendingFirebaseRedirect() {
    if (!window.ClassroomFirebaseAuth?.isConfigured()) return false;
    const credentials = await window.ClassroomFirebaseAuth.consumeRedirectResult();
    if (!credentials) return false;
    let profile = {};
    try {
      profile = JSON.parse(sessionStorage.getItem("classroom.pendingFirebaseProfile") || "{}");
    } catch (_) {
      profile = {};
    }
    await finishFirebaseLogin(credentials, profile);
    return true;
  }

  function pendingFirebaseAuthMode() {
    return {
      school: "school",
      "admin-signup": "signup",
      "admin-login": "login"
    }[storageGet("sessionStorage", PENDING_FIREBASE_ENTRY_KEY)] || null;
  }

  function showFirebaseRedirectRecovery(mode) {
    showAuth(mode);
    const target = mode === "school"
      ? $("school-login-status")
      : $("firebase-status");
    target.textContent = "Google 로그인 결과를 확인하지 못했습니다. 같은 버튼을 눌러 다시 시도해 주세요.";
    target.classList.add("error");
  }

  async function restoreInitialSession() {
    // Firebase returns to this page with a short-lived redirect result.  It
    // must be exchanged before /auth/me is checked, otherwise cookie mode
    // would see no session yet and send a successful Google login to landing.
    const hadStoredBearer = Boolean(state.token);
    const pendingMode = pendingFirebaseAuthMode();
    try {
      if (await consumePendingFirebaseRedirect()) return;
    } catch (error) {
      const errorMode = pendingFirebaseAuthMode() || pendingMode || "login";
      showAuth(errorMode);
      const errorTarget = errorMode === "school" ? $("school-login-error") : loginError;
      errorTarget.textContent = error.message;
      errorTarget.hidden = false;
      return;
    }

    if (!hadStoredBearer && !cookieSessionEnabled) {
      if (pendingMode) showFirebaseRedirectRecovery(pendingMode);
      return;
    }
    try {
      await loadTeacher();
    } catch (error) {
      clearSession();
      if (hadStoredBearer) {
        showAuth("login");
        loginError.textContent = error.message;
        loginError.hidden = false;
      } else {
        // A visitor without a secure cookie stays on the normal landing page.
        // This avoids treating a first visit as an expired-login error.
        showLanding();
      }
    }
  }

  restoreInitialSession();
})();
