// Classroom's production API runs in one SQLite-backed Durable Object.  A
// school is a naturally coordinated unit: teacher actions, student codes,
// class sessions and WebSocket commands all need a consistent order.
const ROOT_SCHOOL_ID = "5d445bc1-88e9-4e24-bcc3-845b20e9c6ee";
const ROOT_TEACHER_ID = "e55aeebb-7f43-4d38-9c4f-6361570afd11";
const ROOT_CLASS_ID = "42ab8f3a-0e8a-47bc-a543-7b7892fa1e00";
const ROOT_LOGIN = "blossom0948";
const ROOT_EMAIL = "blossom0948@gmail.com";
const SESSION_LIFETIME_MS = 1000 * 60 * 60 * 12;
const ONLINE_WINDOW_MS = 75 * 1000;
const PASSWORD_ITERATIONS = 100_000;
const TERMS_VERSION = "2026-08-30";
const PRIVACY_VERSION = "2026-08-30";
const MAX_MESSAGE_BYTES = 64 * 1024;
const MAX_COMMAND_TARGETS = 30;
const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

export default {
  async fetch(request, env) {
    if (request.method === "OPTIONS") return corsResponse(request, env);
    const objectId = env.CLASSROOM_STATE.idFromName("blossom-classroom:production");
    const durableObject = env.CLASSROOM_STATE.get(objectId);
    return durableObject.fetch(request);
  }
};

export class ClassroomState {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.sql = ctx.storage.sql;
    this.ready = ctx.blockConcurrencyWhile(async () => this.initialize());
  }

  async initialize() {
    this.exec(`CREATE TABLE IF NOT EXISTS Users (
      id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      login_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
      display_name TEXT NOT NULL,
      subject TEXT NOT NULL DEFAULT '',
      firebase_uid TEXT UNIQUE,
      firebase_email TEXT COLLATE NOCASE UNIQUE,
      password_salt TEXT,
      password_hash TEXT,
      password_iterations INTEGER,
      is_admin INTEGER NOT NULL DEFAULT 0,
      profile_completed INTEGER NOT NULL DEFAULT 0,
      legal_accepted_at_utc TEXT,
      terms_version TEXT,
      privacy_version TEXT,
      created_at_utc TEXT NOT NULL,
      updated_at_utc TEXT NOT NULL
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS Classes (
      id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      name TEXT NOT NULL,
      default_subject TEXT NOT NULL DEFAULT '',
      owner_teacher_id TEXT NOT NULL,
      created_at_utc TEXT NOT NULL,
      updated_at_utc TEXT NOT NULL
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS ClassTeachers (
      class_id TEXT NOT NULL,
      teacher_id TEXT NOT NULL,
      PRIMARY KEY (class_id, teacher_id)
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS TeacherSessions (
      token_hash TEXT PRIMARY KEY,
      teacher_id TEXT NOT NULL,
      expires_at_utc TEXT NOT NULL,
      created_at_utc TEXT NOT NULL,
      revoked_at_utc TEXT
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS AdministratorGrants (
      identifier TEXT PRIMARY KEY COLLATE NOCASE,
      school_id TEXT NOT NULL,
      granted_by_teacher_id TEXT NOT NULL,
      created_at_utc TEXT NOT NULL,
      active INTEGER NOT NULL DEFAULT 1
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS StudentCodes (
      device_id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      class_id TEXT NOT NULL,
      student_id TEXT NOT NULL,
      student_display_name TEXT NOT NULL,
      join_code TEXT NOT NULL COLLATE NOCASE UNIQUE,
      code_created_at_utc TEXT NOT NULL,
      last_used_at_utc TEXT,
      created_by_teacher_id TEXT NOT NULL,
      revoked_at_utc TEXT
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS Devices (
      device_id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      class_id TEXT NOT NULL,
      student_id TEXT NOT NULL,
      student_display_name TEXT NOT NULL,
      computer_name TEXT NOT NULL,
      agent_version TEXT NOT NULL,
      device_token_hash TEXT NOT NULL,
      issued_at_utc TEXT NOT NULL,
      last_heartbeat_utc TEXT,
      activity_json TEXT,
      battery_percent INTEGER,
      network_status TEXT,
      policy_applied INTEGER NOT NULL DEFAULT 0,
      active_session_id TEXT,
      revoked_at_utc TEXT
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS ClassSessions (
      session_id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      class_id TEXT NOT NULL,
      subject TEXT NOT NULL,
      started_at_utc TEXT NOT NULL,
      ended_at_utc TEXT,
      started_by_teacher_id TEXT NOT NULL
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS Commands (
      request_id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      class_id TEXT NOT NULL,
      session_id TEXT NOT NULL,
      kind TEXT NOT NULL,
      payload_json TEXT NOT NULL,
      created_by_teacher_id TEXT NOT NULL,
      created_at_utc TEXT NOT NULL
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS CommandTargets (
      request_id TEXT NOT NULL,
      device_id TEXT NOT NULL,
      state TEXT NOT NULL,
      acknowledged_at_utc TEXT,
      completed_at_utc TEXT,
      result_json TEXT,
      PRIMARY KEY (request_id, device_id)
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS AuditEvents (
      event_id TEXT PRIMARY KEY,
      timestamp_utc TEXT NOT NULL,
      school_id TEXT,
      class_id TEXT,
      session_id TEXT,
      teacher_id TEXT,
      student_id TEXT,
      device_id TEXT,
      request_id TEXT,
      action TEXT NOT NULL,
      result TEXT NOT NULL,
      reason TEXT
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS RateLimits (
      rate_key TEXT PRIMARY KEY,
      attempt_count INTEGER NOT NULL,
      reset_at_utc TEXT NOT NULL
    )`);
    this.exec("CREATE INDEX IF NOT EXISTS idx_devices_class ON Devices(class_id, revoked_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_codes_class ON StudentCodes(class_id, revoked_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_sessions_teacher ON TeacherSessions(teacher_id, expires_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_audit_class ON AuditEvents(class_id, timestamp_utc)");

    const existingRoot = this.one("SELECT id FROM Users WHERE id = ?", ROOT_TEACHER_ID);
    if (!existingRoot) {
      const now = isoNow();
      this.exec(`INSERT INTO Users (
        id, school_id, login_name, display_name, subject, firebase_email,
        is_admin, profile_completed, created_at_utc, updated_at_utc
      ) VALUES (?, ?, ?, ?, '', ?, 1, 0, ?, ?)`,
      ROOT_TEACHER_ID, ROOT_SCHOOL_ID, ROOT_LOGIN, "선생님", ROOT_EMAIL, now, now);
      this.exec(`INSERT INTO Classes (
        id, school_id, name, default_subject, owner_teacher_id, created_at_utc, updated_at_utc
      ) VALUES (?, ?, ?, '', ?, ?, ?)`,
      ROOT_CLASS_ID, ROOT_SCHOOL_ID, "나의 첫 수업", ROOT_TEACHER_ID, now, now);
      this.exec("INSERT OR IGNORE INTO ClassTeachers (class_id, teacher_id) VALUES (?, ?)", ROOT_CLASS_ID, ROOT_TEACHER_ID);
    }
    this.exec("UPDATE Users SET is_admin = 1, firebase_email = ? WHERE id = ?", ROOT_EMAIL, ROOT_TEACHER_ID);
    this.exec(`INSERT OR IGNORE INTO AdministratorGrants (
      identifier, school_id, granted_by_teacher_id, created_at_utc, active
    ) VALUES (?, ?, ?, ?, 1)`, ROOT_EMAIL, ROOT_SCHOOL_ID, ROOT_TEACHER_ID, isoNow());
  }

  async fetch(request) {
    await this.ready;
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";
    const cors = corsHeaders(request, this.env);
    try {
      if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: cors });
      if (path === "/health" && request.method === "GET") {
        return responseJson({ service: "Classroom.Cloud", version: 2, status: "running", storage: "durable-object" }, 200, cors);
      }
      if (path === "/health/ready" && request.method === "GET") {
        return responseJson({ service: "Classroom.Cloud", status: "ready", database: "durable-object-sqlite" }, 200, cors);
      }
      if (path === "/ws/student") return this.handleStudentWebSocket(request, url, cors);
      if (path === "/auth/login" && request.method === "POST") return this.login(request, cors);
      if (path === "/auth/firebase-login" && request.method === "POST") return this.firebaseLogin(request, cors);
      if (path === "/auth/me" && request.method === "GET") return this.getMe(request, cors);
      if (path === "/auth/logout" && request.method === "POST") return this.logout(request, cors);
      if (path === "/auth/profile" && request.method === "PUT") return this.updateProfile(request, cors);
      if (path === "/auth/change-password" && request.method === "POST") return this.changePassword(request, cors);
      if (path === "/api/classes" && request.method === "GET") return this.getClasses(request, cors);
      if (path === "/api/student-codes" && request.method === "GET") return this.getStudentCodes(request, cors);
      if (path === "/api/admin/teachers" && request.method === "GET") return this.getAdministrators(request, cors);
      if (path === "/api/admin/teachers" && request.method === "POST") return this.setAdministrator(request, cors);
      if (path === "/api/devices/enroll-code" && request.method === "POST") return this.enrollByCode(request, cors);
      if (path === "/api/devices/enroll" && request.method === "POST") {
        return responseError("ENROLLMENT_NOT_FOUND", "학생 코드로 다시 등록해 주세요.", 401, cors);
      }

      const ticketMatch = path.match(/^\/api\/classes\/([^/]+)\/enrollment-tickets$/);
      if (ticketMatch && request.method === "POST") return this.createStudentCode(request, ticketMatch[1], cors);
      const sessionReadMatch = path.match(/^\/api\/classes\/([^/]+)\/session$/);
      if (sessionReadMatch && request.method === "GET") return this.getActiveSession(request, sessionReadMatch[1], cors);
      const sessionStartMatch = path.match(/^\/api\/classes\/([^/]+)\/sessions$/);
      if (sessionStartMatch && request.method === "POST") return this.startSession(request, sessionStartMatch[1], cors);
      const sessionEndMatch = path.match(/^\/api\/classes\/([^/]+)\/sessions\/([^/]+)$/);
      if (sessionEndMatch && request.method === "DELETE") return this.endSession(request, sessionEndMatch[1], sessionEndMatch[2], cors);
      const studentsMatch = path.match(/^\/api\/classes\/([^/]+)\/students$/);
      if (studentsMatch && request.method === "GET") return this.getStudents(request, studentsMatch[1], cors);
      const revokeMatch = path.match(/^\/api\/classes\/([^/]+)\/devices\/([^/]+)$/);
      if (revokeMatch && request.method === "DELETE") return this.revokeDevice(request, revokeMatch[1], revokeMatch[2], cors);
      const commandMatch = path.match(/^\/api\/classes\/([^/]+)\/commands$/);
      if (commandMatch && request.method === "POST") return this.queueCommand(request, commandMatch[1], cors);
      const commandStatusMatch = path.match(/^\/api\/classes\/([^/]+)\/commands\/([^/]+)$/);
      if (commandStatusMatch && request.method === "GET") return this.getCommandStatus(request, commandStatusMatch[1], commandStatusMatch[2], cors);
      const auditMatch = path.match(/^\/api\/classes\/([^/]+)\/audit$/);
      if (auditMatch && request.method === "GET") return this.getAudit(request, auditMatch[1], url, cors);
      return responseError("NOT_FOUND", "요청한 Classroom API를 찾을 수 없습니다.", 404, cors);
    } catch (error) {
      console.error("Classroom cloud API failed", error);
      return responseError("INTERNAL_ERROR", "서버 처리 중 오류가 발생했습니다.", 500, cors);
    }
  }

  async login(request, cors) {
    const body = await readJson(request);
    const loginName = normalizeIdentifier(body?.loginName);
    const password = text(body?.password, 256);
    if (!this.consumeRateLimit(`${clientIp(request)}|login|${loginName}`, 10, 60_000)) {
      return responseError("LOGIN_RATE_LIMITED", "로그인 시도가 많습니다. 잠시 후 다시 시도해 주세요.", 429, cors, { "Retry-After": "60" });
    }
    const user = loginName ? this.one("SELECT * FROM Users WHERE login_name = ?", loginName) : null;
    if (!user || !password || !user.password_hash || !(await verifyPassword(password, user.password_salt, user.password_hash, user.password_iterations))) {
      const reason = user && !user.password_hash
        ? "아이디 로그인을 사용하려면 Google 로그인 후 설정에서 비밀번호를 만들어 주세요."
        : "아이디 또는 비밀번호가 올바르지 않습니다.";
      return responseError("INVALID_CREDENTIALS", reason, 401, cors);
    }
    return responseJson(await this.createSessionPayload(user), 200, cors);
  }

  async firebaseLogin(request, cors) {
    const body = await readJson(request);
    const idToken = text(body?.idToken, 16_384);
    if (!idToken) return responseError("INVALID_FIREBASE_TOKEN", "Firebase 인증 정보가 필요합니다.", 400, cors);
    if (!this.consumeRateLimit(`${clientIp(request)}|firebase`, 20, 60_000)) {
      return responseError("LOGIN_RATE_LIMITED", "로그인 시도가 많습니다. 잠시 후 다시 시도해 주세요.", 429, cors, { "Retry-After": "60" });
    }
    const identity = await this.verifyFirebaseToken(idToken);
    if (!identity) return responseError("INVALID_FIREBASE_TOKEN", "Google 또는 Firebase 로그인을 확인하지 못했습니다.", 401, cors);

    const email = normalizeEmail(identity.email);
    const now = isoNow();
    let user = this.one("SELECT * FROM Users WHERE firebase_uid = ?", identity.localId)
      || this.one("SELECT * FROM Users WHERE firebase_email = ?", email);
    if (!user) {
      const userId = crypto.randomUUID();
      const isRoot = email === ROOT_EMAIL;
      const loginName = isRoot ? ROOT_LOGIN : email;
      const displayName = text(identity.displayName, 80) || "새 선생님";
      this.exec(`INSERT INTO Users (
        id, school_id, login_name, display_name, subject, firebase_uid, firebase_email,
        is_admin, profile_completed, created_at_utc, updated_at_utc
      ) VALUES (?, ?, ?, ?, '', ?, ?, ?, 0, ?, ?)`,
      userId, ROOT_SCHOOL_ID, loginName, displayName, identity.localId, email, isRoot ? 1 : 0, now, now);
      this.createStarterClass(userId, ROOT_SCHOOL_ID, "나의 첫 수업", "");
      user = this.one("SELECT * FROM Users WHERE id = ?", userId);
    } else {
      this.exec(`UPDATE Users SET firebase_uid = ?, firebase_email = ?, updated_at_utc = ? WHERE id = ?`, identity.localId, email, now, user.id);
      user = this.one("SELECT * FROM Users WHERE id = ?", user.id);
    }

    const grant = this.one("SELECT identifier FROM AdministratorGrants WHERE active = 1 AND school_id = ? AND identifier IN (?, ?)", user.school_id, email, normalizeIdentifier(user.login_name));
    if (email === ROOT_EMAIL || grant) {
      this.exec("UPDATE Users SET is_admin = 1 WHERE id = ?", user.id);
      user = this.one("SELECT * FROM Users WHERE id = ?", user.id);
    }

    if (body?.termsAccepted === true && body?.privacyAccepted === true && !user.legal_accepted_at_utc) {
      this.recordLegalConsent(user.id);
      user = this.one("SELECT * FROM Users WHERE id = ?", user.id);
    }
    return responseJson(await this.createSessionPayload(user), 200, cors);
  }

  async getMe(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    return responseJson(this.serializeTeacherSession(user), 200, cors);
  }

  async logout(request, cors) {
    const token = bearerToken(request);
    if (token) this.exec("UPDATE TeacherSessions SET revoked_at_utc = ? WHERE token_hash = ?", isoNow(), await sha256Text(token));
    return new Response(null, { status: 204, headers: cors });
  }

  async updateProfile(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    const body = await readJson(request);
    const displayName = text(body?.displayName, 80);
    const subject = text(body?.subject, 128);
    const className = text(body?.className, 80);
    if (!displayName || displayName.length < 2) return responseError("INVALID_PROFILE", "선생님 이름을 2자 이상 입력해 주세요.", 400, cors);
    if (body?.termsAccepted !== true || body?.privacyAccepted !== true) {
      if (!user.legal_accepted_at_utc) return responseError("LEGAL_CONSENT_REQUIRED", "이용약관과 개인정보처리방침 동의가 필요합니다.", 400, cors);
    }
    const now = isoNow();
    this.exec(`UPDATE Users SET display_name = ?, subject = ?, profile_completed = 1, updated_at_utc = ? WHERE id = ?`, displayName, subject, now, user.id);
    if (body?.termsAccepted === true && body?.privacyAccepted === true) this.recordLegalConsent(user.id);
    const ownClass = this.one("SELECT * FROM Classes WHERE owner_teacher_id = ? ORDER BY created_at_utc LIMIT 1", user.id);
    if (ownClass && className) {
      this.exec("UPDATE Classes SET name = ?, default_subject = ?, updated_at_utc = ? WHERE id = ?", className, subject, now, ownClass.id);
    } else if (!ownClass) {
      this.createStarterClass(user.id, user.school_id, className || "나의 첫 수업", subject);
    }
    const updated = this.one("SELECT * FROM Users WHERE id = ?", user.id);
    return responseJson(this.serializeTeacherSession(updated), 200, cors);
  }

  async changePassword(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    const body = await readJson(request);
    const currentPassword = text(body?.currentPassword, 256);
    const newPassword = text(body?.newPassword, 256);
    if (!newPassword || newPassword.length < 6) {
      return responseError("INVALID_PASSWORD", "비밀번호는 6자 이상으로 설정해 주세요.", 400, cors);
    }
    if (user.password_hash && !(await verifyPassword(currentPassword, user.password_salt, user.password_hash, user.password_iterations))) {
      return responseError("INVALID_CREDENTIALS", "현재 비밀번호가 올바르지 않습니다.", 401, cors);
    }
    const encoded = await hashPassword(newPassword);
    this.exec(`UPDATE Users SET password_salt = ?, password_hash = ?, password_iterations = ?, updated_at_utc = ? WHERE id = ?`, encoded.salt, encoded.hash, PASSWORD_ITERATIONS, isoNow(), user.id);
    return new Response(null, { status: 204, headers: cors });
  }

  async getClasses(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    return responseJson(this.classesForTeacher(user), 200, cors);
  }

  async getActiveSession(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const session = this.one("SELECT * FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL ORDER BY started_at_utc DESC LIMIT 1", classId);
    return responseJson(session ? serializeSession(session) : null, 200, cors);
  }

  async startSession(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const subject = text(body?.subject, 128) || this.one("SELECT default_subject FROM Classes WHERE id = ?", classId)?.default_subject || "수업";
    const current = this.one("SELECT * FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL LIMIT 1", classId);
    if (current) return responseError("SESSION_ALREADY_ACTIVE", "이미 진행 중인 수업이 있습니다.", 409, cors);
    const classItem = this.one("SELECT * FROM Classes WHERE id = ?", classId);
    const now = isoNow();
    const session = { session_id: crypto.randomUUID(), school_id: classItem.school_id, class_id: classId, subject, started_at_utc: now, ended_at_utc: null, started_by_teacher_id: user.id };
    this.exec(`INSERT INTO ClassSessions (session_id, school_id, class_id, subject, started_at_utc, started_by_teacher_id) VALUES (?, ?, ?, ?, ?, ?)`, session.session_id, session.school_id, session.class_id, session.subject, session.started_at_utc, session.started_by_teacher_id);
    this.exec("UPDATE Classes SET default_subject = ?, updated_at_utc = ? WHERE id = ?", subject, now, classId);
    this.audit({ schoolId: session.school_id, classId, sessionId: session.session_id, teacherId: user.id, action: "CLASS_SESSION", result: "STARTED", reason: subject });
    this.notifyClassSession(classId, session.session_id);
    return responseJson(serializeSession(session), 200, cors);
  }

  async endSession(request, classId, sessionId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const session = this.one("SELECT * FROM ClassSessions WHERE session_id = ? AND class_id = ? AND ended_at_utc IS NULL", sessionId, classId);
    if (!session) return responseError("SESSION_NOT_FOUND", "진행 중인 수업을 찾지 못했습니다.", 404, cors);
    const now = isoNow();
    this.exec("UPDATE ClassSessions SET ended_at_utc = ? WHERE session_id = ?", now, sessionId);
    this.exec("UPDATE Devices SET policy_applied = 0, active_session_id = NULL WHERE class_id = ?", classId);
    this.audit({ schoolId: session.school_id, classId, sessionId, teacherId: user.id, action: "CLASS_SESSION", result: "ENDED", reason: session.subject });
    this.notifyClassSession(classId, "00000000-0000-0000-0000-000000000000");
    return responseJson({ ...serializeSession({ ...session, ended_at_utc: now }) }, 200, cors);
  }

  async getStudents(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const active = this.one("SELECT session_id FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL ORDER BY started_at_utc DESC LIMIT 1", classId);
    const now = Date.now();
    const devices = this.all("SELECT * FROM Devices WHERE class_id = ? AND revoked_at_utc IS NULL ORDER BY student_display_name COLLATE NOCASE, computer_name COLLATE NOCASE", classId);
    return responseJson(devices.map((device) => serializeDevice(device, active?.session_id || null, now)), 200, cors);
  }

  async revokeDevice(request, classId, deviceId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const device = this.one("SELECT * FROM Devices WHERE device_id = ? AND class_id = ? AND revoked_at_utc IS NULL", deviceId, classId);
    if (!device) return responseError("DEVICE_NOT_FOUND", "학생 장치를 찾지 못했습니다.", 404, cors);
    const now = isoNow();
    this.exec("UPDATE Devices SET revoked_at_utc = ? WHERE device_id = ?", now, deviceId);
    this.closeDeviceSockets(deviceId, 1008, "Device revoked");
    this.audit({ schoolId: device.school_id, classId, teacherId: user.id, studentId: device.student_id, deviceId, action: "DEVICE", result: "REVOKED", reason: "Teacher removed device" });
    return responseJson({ deviceId, status: "revoked", updatedAtUtc: now }, 200, cors);
  }

  async createStudentCode(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "학생 코드는 관리자만 발급할 수 있습니다.", 403, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const studentName = text(body?.studentDisplayName, 128);
    const requestedStudentId = text(body?.studentId, 80);
    if (!studentName) return responseError("INVALID_REQUEST", "학생 이름을 입력해 주세요.", 400, cors);
    const classItem = this.one("SELECT * FROM Classes WHERE id = ?", classId);
    const studentId = requestedStudentId || crypto.randomUUID();
    const existing = this.one("SELECT * FROM StudentCodes WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", classId, studentId);
    const code = await this.createUniqueJoinCode();
    const now = isoNow();
    const deviceId = existing?.device_id || crypto.randomUUID();
    if (existing) {
      this.exec(`UPDATE StudentCodes SET student_display_name = ?, join_code = ?, code_created_at_utc = ?, last_used_at_utc = NULL, created_by_teacher_id = ?, revoked_at_utc = NULL WHERE device_id = ?`, studentName, code, now, user.id, deviceId);
      const enrolledDevices = this.all("SELECT device_id FROM Devices WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", classId, studentId);
      this.exec("UPDATE Devices SET revoked_at_utc = ? WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", now, classId, studentId);
      for (const enrolledDevice of enrolledDevices) {
        this.closeDeviceSockets(enrolledDevice.device_id, 1008, "Student code reissued");
      }
    } else {
      this.exec(`INSERT INTO StudentCodes (device_id, school_id, class_id, student_id, student_display_name, join_code, code_created_at_utc, created_by_teacher_id) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`, deviceId, classItem.school_id, classId, studentId, studentName, code, now, user.id);
    }
    this.audit({ schoolId: classItem.school_id, classId, teacherId: user.id, studentId, deviceId, action: "STUDENT_CODE", result: existing ? "REISSUED" : "CREATED", reason: studentName });
    return responseJson({
      deviceId,
      schoolId: classItem.school_id,
      classId,
      studentId,
      studentDisplayName: studentName,
      expiresAtUtc: null,
      enrollmentToken: "",
      joinCode: code
    }, 200, cors);
  }

  async getStudentCodes(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    const rows = this.all(`SELECT c.*, sc.*, u.display_name AS created_by_display_name
      FROM StudentCodes sc
      JOIN Classes c ON c.id = sc.class_id
      JOIN Users u ON u.id = sc.created_by_teacher_id
      WHERE sc.school_id = ? AND sc.revoked_at_utc IS NULL
      ORDER BY c.name COLLATE NOCASE, sc.student_display_name COLLATE NOCASE`, user.school_id);
    return responseJson(rows.map((row) => ({
      deviceId: row.device_id,
      schoolId: row.school_id,
      classId: row.class_id,
      className: row.name,
      subject: row.default_subject,
      studentId: row.student_id,
      studentDisplayName: row.student_display_name,
      joinCode: row.join_code,
      createdAtUtc: row.code_created_at_utc,
      lastUsedAtUtc: row.last_used_at_utc,
      createdByDisplayName: row.created_by_display_name
    })), 200, cors);
  }

  async enrollByCode(request, cors) {
    const body = await readJson(request);
    const joinCode = normalizeJoinCode(body?.joinCode);
    const deviceName = text(body?.deviceName, 128);
    const agentVersion = text(body?.agentVersion, 128) || "Classroom Student";
    if (!joinCode || !deviceName) return responseError("INVALID_REQUEST", "학생 코드와 장치 이름이 필요합니다.", 400, cors);
    if (!this.consumeRateLimit(`${clientIp(request)}|code|${joinCode}`, 12, 60_000)) {
      return responseError("ENROLLMENT_RATE_LIMITED", "잠시 후 다시 시도해 주세요.", 429, cors, { "Retry-After": "60" });
    }
    const code = this.one("SELECT * FROM StudentCodes WHERE join_code = ? AND revoked_at_utc IS NULL", joinCode);
    if (!code) return responseError("ENROLLMENT_NOT_FOUND", "학생 코드를 찾지 못했습니다. 코드를 다시 확인해 주세요.", 401, cors);
    const deviceId = crypto.randomUUID();
    const token = await randomToken();
    const now = isoNow();
    this.exec(`INSERT INTO Devices (
      device_id, school_id, class_id, student_id, student_display_name, computer_name,
      agent_version, device_token_hash, issued_at_utc
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`, deviceId, code.school_id, code.class_id, code.student_id, code.student_display_name, deviceName, agentVersion, await sha256Text(token), now);
    this.exec("UPDATE StudentCodes SET last_used_at_utc = ? WHERE device_id = ?", now, code.device_id);
    this.audit({ schoolId: code.school_id, classId: code.class_id, studentId: code.student_id, deviceId, action: "DEVICE_ENROLLMENT", result: "SUCCESS", reason: deviceName });
    return responseJson({
      deviceId,
      schoolId: code.school_id,
      classId: code.class_id,
      studentId: code.student_id,
      deviceToken: token,
      issuedAtUtc: now
    }, 200, cors);
  }

  async queueCommand(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const activeSession = this.one("SELECT * FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL ORDER BY started_at_utc DESC LIMIT 1", classId);
    if (!activeSession) return responseError("SESSION_NOT_ACTIVE", "수업을 시작한 후 명령을 보낼 수 있습니다.", 409, cors);
    const kind = text(body?.kind, 64);
    if (!new Set(["message", "openUrl", "focusMode", "launchApprovedApp"]).has(kind)) {
      return responseError("INVALID_COMMAND", "지원하지 않는 수업 명령입니다.", 400, cors);
    }
    const requestId = validId(body?.requestId) ? body.requestId : crypto.randomUUID();
    const allDevices = this.all("SELECT device_id FROM Devices WHERE class_id = ? AND revoked_at_utc IS NULL", classId);
    const requested = Array.isArray(body?.targetDeviceIds) && body.targetDeviceIds.length ? body.targetDeviceIds : allDevices.map((row) => row.device_id);
    const targets = [...new Set(requested.filter((id) => typeof id === "string"))].slice(0, MAX_COMMAND_TARGETS);
    if (!targets.length) return responseError("NO_TARGETS", "명령을 받을 학생 장치가 없습니다.", 400, cors);
    const validTargets = new Set(allDevices.map((row) => row.device_id));
    const queued = targets.filter((id) => validTargets.has(id));
    if (!queued.length) return responseError("TARGET_FORBIDDEN", "선택한 장치에 명령을 보낼 수 없습니다.", 403, cors);

    const payload = {
      requestId,
      sessionId: activeSession.session_id,
      targetDeviceIds: queued,
      kind,
      message: text(body?.message, 2000) || null,
      url: text(body?.url, 2048) || null,
      approvedAppId: text(body?.approvedAppId, 128) || null,
      displaySeconds: numberInRange(body?.displaySeconds, 1, 3600) || null,
      requiresAcknowledgement: body?.requiresAcknowledgement !== false,
      focusEnabled: typeof body?.focusEnabled === "boolean" ? body.focusEnabled : null
    };
    if (kind === "message" && !payload.message) return responseError("INVALID_COMMAND", "보낼 메시지를 입력해 주세요.", 400, cors);
    if (kind === "openUrl" && (!payload.url || !isSafeHttpsUrl(payload.url))) return responseError("INVALID_COMMAND", "HTTPS 주소를 입력해 주세요.", 400, cors);
    if (kind === "launchApprovedApp" && !payload.approvedAppId) return responseError("INVALID_COMMAND", "실행할 앱을 선택해 주세요.", 400, cors);

    const now = isoNow();
    this.exec(`INSERT INTO Commands (request_id, school_id, class_id, session_id, kind, payload_json, created_by_teacher_id, created_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`, requestId, activeSession.school_id, classId, activeSession.session_id, kind, JSON.stringify(payload), user.id, now);
    for (const deviceId of queued) this.exec("INSERT INTO CommandTargets (request_id, device_id, state) VALUES (?, ?, 'QUEUED')", requestId, deviceId);
    if (kind === "focusMode") this.exec("UPDATE Devices SET policy_applied = ? WHERE device_id IN (" + placeholders(queued.length) + ")", payload.focusEnabled ? 1 : 0, ...queued);
    this.audit({ schoolId: activeSession.school_id, classId, sessionId: activeSession.session_id, teacherId: user.id, requestId, action: "COMMAND", result: "QUEUED", reason: kind });
    for (const deviceId of queued) this.deliverCommands(deviceId);
    return responseJson({ requestId, requestedCount: targets.length, queuedCount: queued.length, queuedDeviceIds: queued, rejectedDeviceIds: targets.filter((id) => !queued.includes(id)) }, 200, cors);
  }

  async getCommandStatus(request, classId, requestId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const command = this.one("SELECT * FROM Commands WHERE request_id = ? AND class_id = ?", requestId, classId);
    if (!command) return responseError("COMMAND_NOT_FOUND", "명령 기록을 찾지 못했습니다.", 404, cors);
    const devices = this.all("SELECT device_id, state FROM CommandTargets WHERE request_id = ?", requestId).map((row) => ({ deviceId: row.device_id, state: row.state }));
    const complete = devices.filter((row) => ["SUCCESS", "FAILED", "REJECTED"].includes(row.state));
    const failed = devices.filter((row) => ["FAILED", "REJECTED"].includes(row.state));
    return responseJson({ requestId, totalCount: devices.length, completedCount: complete.length, failedCount: failed.length, finished: complete.length === devices.length, devices }, 200, cors);
  }

  async getAudit(request, classId, url, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const limit = Math.max(1, Math.min(Number(url.searchParams.get("limit")) || 100, 200));
    const entries = this.all(`SELECT * FROM AuditEvents WHERE class_id = ? ORDER BY timestamp_utc DESC LIMIT ?`, classId, limit);
    return responseJson(entries.map((entry) => ({
      eventId: entry.event_id,
      timestampUtc: entry.timestamp_utc,
      schoolId: entry.school_id,
      classId: entry.class_id,
      sessionId: entry.session_id,
      teacherId: entry.teacher_id,
      studentId: entry.student_id,
      studentDeviceId: entry.device_id,
      requestId: entry.request_id,
      action: entry.action,
      result: entry.result,
      reason: entry.reason
    })), 200, cors);
  }

  async getAdministrators(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 권한 목록을 확인할 수 있습니다.", 403, cors);
    const teachers = this.all("SELECT id, login_name, display_name, firebase_email, is_admin FROM Users WHERE school_id = ? ORDER BY display_name COLLATE NOCASE", user.school_id)
      .map((row) => ({ teacherId: row.id, loginName: row.login_name, displayName: row.display_name, email: row.firebase_email || "", isAdmin: Boolean(row.is_admin) }));
    const grants = this.all("SELECT identifier, created_at_utc FROM AdministratorGrants WHERE school_id = ? AND active = 1 ORDER BY identifier", user.school_id)
      .map((row) => ({ identifier: row.identifier, createdAtUtc: row.created_at_utc }));
    return responseJson({ teachers, grants }, 200, cors);
  }

  async setAdministrator(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 권한을 변경할 수 있습니다.", 403, cors);
    const body = await readJson(request);
    const identifier = normalizeIdentifier(body?.identifier);
    const isAdmin = body?.isAdmin === true;
    if (!identifier || identifier.length > 254) return responseError("INVALID_IDENTIFIER", "Google 이메일 또는 아이디를 입력해 주세요.", 400, cors);
    if (!isAdmin && (identifier === ROOT_LOGIN || identifier === ROOT_EMAIL)) {
      return responseError("ADMIN_CHANGE_REJECTED", "기본 관리자 권한은 해제할 수 없습니다.", 409, cors);
    }
    const target = this.one("SELECT * FROM Users WHERE school_id = ? AND (login_name = ? OR firebase_email = ?)", user.school_id, identifier, identifier);
    const now = isoNow();
    if (isAdmin) {
      this.exec(`INSERT INTO AdministratorGrants (identifier, school_id, granted_by_teacher_id, created_at_utc, active)
        VALUES (?, ?, ?, ?, 1)
        ON CONFLICT(identifier) DO UPDATE SET school_id = excluded.school_id, granted_by_teacher_id = excluded.granted_by_teacher_id, created_at_utc = excluded.created_at_utc, active = 1`, identifier, user.school_id, user.id, now);
      if (target) this.exec("UPDATE Users SET is_admin = 1, updated_at_utc = ? WHERE id = ?", now, target.id);
    } else {
      this.exec("UPDATE AdministratorGrants SET active = 0 WHERE identifier = ? AND school_id = ?", identifier, user.school_id);
      if (target) this.exec("UPDATE Users SET is_admin = 0, updated_at_utc = ? WHERE id = ?", now, target.id);
    }
    this.audit({ schoolId: user.school_id, teacherId: user.id, action: "ADMIN_ACCESS", result: isAdmin ? "GRANTED" : "REVOKED", reason: identifier });
    return responseJson({ identifier, isAdmin, accountFound: Boolean(target) }, 200, cors);
  }

  async handleStudentWebSocket(request, url, cors) {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") return responseError("WEBSOCKET_REQUIRED", "WebSocket 연결이 필요합니다.", 400, cors);
    const deviceId = url.searchParams.get("deviceId") || "";
    const token = bearerToken(request);
    const device = token && validId(deviceId)
      ? this.one("SELECT * FROM Devices WHERE device_id = ? AND revoked_at_utc IS NULL", deviceId)
      : null;
    if (!device || !(await constantTimeEqual(await sha256Text(token), device.device_token_hash))) {
      return responseError("UNAUTHORIZED", "학생 장치 인증에 실패했습니다.", 401, cors);
    }
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.ctx.acceptWebSocket(server, [deviceId]);
    server.serializeAttachment({ deviceId, schoolId: device.school_id, classId: device.class_id });
    return new Response(null, { status: 101, webSocket: client, headers: cors });
  }

  async webSocketMessage(socket, message) {
    if (typeof message !== "string" || new TextEncoder().encode(message).byteLength > MAX_MESSAGE_BYTES) {
      socket.send(envelope("ERROR", { code: "INVALID_PROTOCOL", message: "프로토콜 메시지가 올바르지 않습니다." }));
      return;
    }
    const attachment = socket.deserializeAttachment();
    const device = attachment?.deviceId ? this.one("SELECT * FROM Devices WHERE device_id = ?", attachment.deviceId) : null;
    if (!device || device.revoked_at_utc) {
      socket.close(1008, "Device revoked");
      return;
    }
    let incoming;
    try { incoming = JSON.parse(message); } catch (_) {
      socket.send(envelope("ERROR", { code: "INVALID_PROTOCOL", message: "JSON 형식이 올바르지 않습니다." }));
      return;
    }
    if (!incoming || incoming.version !== 1 || typeof incoming.type !== "string" || !incoming.payload) {
      socket.send(envelope("ERROR", { code: "INVALID_PROTOCOL", message: "프로토콜 형식이 올바르지 않습니다." }));
      return;
    }
    if (incoming.type === "DEVICE_HELLO") {
      if (incoming.payload.deviceId !== device.device_id) {
        socket.send(envelope("ERROR", { code: "DEVICE_MISMATCH", message: "장치 ID가 일치하지 않습니다." }));
        socket.close(1008, "Device mismatch");
        return;
      }
      this.acceptDeviceSession(socket, device);
      this.deliverCommands(device.device_id);
      return;
    }
    if (incoming.type === "DEVICE_HEARTBEAT") {
      if (incoming.payload.deviceId !== device.device_id) {
        socket.send(envelope("ERROR", { code: "DEVICE_MISMATCH", message: "장치 ID가 일치하지 않습니다." }));
        return;
      }
      const active = this.activeSessionForClass(device.class_id);
      const now = isoNow();
      this.exec(`UPDATE Devices SET last_heartbeat_utc = ?, agent_version = ?, activity_json = ?, battery_percent = ?, network_status = ?, policy_applied = ?, active_session_id = ? WHERE device_id = ?`,
        now,
        text(incoming.payload.agentVersion, 128) || device.agent_version,
        incoming.payload.activity ? JSON.stringify(incoming.payload.activity) : null,
        numberInRange(incoming.payload.batteryPercent, 0, 100),
        text(incoming.payload.networkStatus, 64) || null,
        incoming.payload.policyApplied === true ? 1 : 0,
        active?.session_id || null,
        device.device_id);
      if (incoming.payload.sessionId !== (active?.session_id || "00000000-0000-0000-0000-000000000000")) this.sendSessionAccepted(socket, device.device_id, active?.session_id || "00000000-0000-0000-0000-000000000000");
      return;
    }
    if (incoming.type === "COMMAND_ACK") {
      const payload = incoming.payload;
      if (payload.deviceId !== device.device_id) return;
      const now = isoNow();
      const state = payload.accepted === false ? "REJECTED" : "ACCEPTED";
      this.exec("UPDATE CommandTargets SET state = ?, acknowledged_at_utc = ? WHERE request_id = ? AND device_id = ?", state, now, payload.requestId, device.device_id);
      return;
    }
    if (incoming.type === "COMMAND_RESULT") {
      const payload = incoming.payload;
      if (payload.deviceId !== device.device_id) return;
      const now = isoNow();
      this.exec("UPDATE CommandTargets SET state = ?, completed_at_utc = ?, result_json = ? WHERE request_id = ? AND device_id = ?", payload.success === true ? "SUCCESS" : "FAILED", now, JSON.stringify(payload), payload.requestId, device.device_id);
      return;
    }
    socket.send(envelope("ERROR", { code: "MESSAGE_NOT_ALLOWED", message: "학생 장치에서 허용되지 않은 메시지입니다." }));
  }

  webSocketClose(socket, code, reason) {
    try { socket.close(code, reason); } catch (_) { /* close handshake is handled by the runtime */ }
  }

  acceptDeviceSession(socket, device) {
    const active = this.activeSessionForClass(device.class_id);
    const sessionId = active?.session_id || "00000000-0000-0000-0000-000000000000";
    this.exec("UPDATE Devices SET last_heartbeat_utc = ?, active_session_id = ? WHERE device_id = ?", isoNow(), active?.session_id || null, device.device_id);
    this.sendSessionAccepted(socket, device.device_id, sessionId);
  }

  sendSessionAccepted(socket, deviceId, sessionId) {
    socket.send(envelope("DEVICE_SESSION_ACCEPTED", { deviceId, sessionId, acceptedAtUtc: isoNow() }));
  }

  notifyClassSession(classId, sessionId) {
    for (const socket of this.ctx.getWebSockets()) {
      const attachment = socket.deserializeAttachment();
      if (attachment?.classId === classId) this.sendSessionAccepted(socket, attachment.deviceId, sessionId);
    }
  }

  deliverCommands(deviceId) {
    const sockets = this.ctx.getWebSockets().filter((socket) => socket.deserializeAttachment()?.deviceId === deviceId);
    if (!sockets.length) return;
    const commands = this.all(`SELECT c.* FROM Commands c
      JOIN CommandTargets t ON t.request_id = c.request_id
      WHERE t.device_id = ? AND t.state = 'QUEUED'
      ORDER BY c.created_at_utc ASC`, deviceId);
    for (const command of commands) {
      let payload;
      try { payload = JSON.parse(command.payload_json); } catch (_) { continue; }
      for (const socket of sockets) socket.send(envelope("COMMAND_REQUEST", payload));
      this.exec("UPDATE CommandTargets SET state = 'DELIVERED' WHERE request_id = ? AND device_id = ? AND state = 'QUEUED'", command.request_id, deviceId);
    }
  }

  closeDeviceSockets(deviceId, code, reason) {
    for (const socket of this.ctx.getWebSockets()) {
      if (socket.deserializeAttachment()?.deviceId === deviceId) socket.close(code, reason);
    }
  }

  activeSessionForClass(classId) {
    return this.one("SELECT * FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL ORDER BY started_at_utc DESC LIMIT 1", classId);
  }

  async authenticate(request) {
    const token = bearerToken(request);
    if (!token) return null;
    const now = isoNow();
    const row = this.one(`SELECT u.* FROM TeacherSessions s JOIN Users u ON u.id = s.teacher_id
      WHERE s.token_hash = ? AND s.revoked_at_utc IS NULL AND s.expires_at_utc > ?`, await sha256Text(token), now);
    return row || null;
  }

  async createSessionPayload(user) {
    const token = await randomToken();
    const expiresAt = new Date(Date.now() + SESSION_LIFETIME_MS).toISOString();
    this.exec("INSERT INTO TeacherSessions (token_hash, teacher_id, expires_at_utc, created_at_utc) VALUES (?, ?, ?, ?)", await sha256Text(token), user.id, expiresAt, isoNow());
    const session = this.serializeTeacherSession(user);
    return { accessToken: token, expiresAtUtc: expiresAt, teacherId: user.id, displayName: user.display_name, classes: session.classes, isAdmin: Boolean(user.is_admin), profileCompleted: Boolean(user.profile_completed), subject: user.subject || "", hasPassword: Boolean(user.password_hash), legalAccepted: Boolean(user.legal_accepted_at_utc) };
  }

  serializeTeacherSession(user) {
    return {
      teacherId: user.id,
      displayName: user.display_name,
      classes: this.classesForTeacher(user),
      isAdmin: Boolean(user.is_admin),
      profileCompleted: Boolean(user.profile_completed),
      subject: user.subject || "",
      hasPassword: Boolean(user.password_hash),
      legalAccepted: Boolean(user.legal_accepted_at_utc)
    };
  }

  classesForTeacher(user) {
    const rows = user.is_admin
      ? this.all("SELECT * FROM Classes WHERE school_id = ? ORDER BY name COLLATE NOCASE", user.school_id)
      : this.all(`SELECT c.* FROM Classes c JOIN ClassTeachers ct ON ct.class_id = c.id
        WHERE ct.teacher_id = ? ORDER BY c.name COLLATE NOCASE`, user.id);
    return rows.map((row) => ({ id: row.id, schoolId: row.school_id, name: row.name, defaultSubject: row.default_subject }));
  }

  canAccessClass(user, classId) {
    const classItem = this.one("SELECT * FROM Classes WHERE id = ?", classId);
    if (!classItem || classItem.school_id !== user.school_id) return false;
    if (user.is_admin) return true;
    return Boolean(this.one("SELECT class_id FROM ClassTeachers WHERE class_id = ? AND teacher_id = ?", classId, user.id));
  }

  createStarterClass(teacherId, schoolId, className, subject) {
    const now = isoNow();
    const classId = crypto.randomUUID();
    this.exec(`INSERT INTO Classes (id, school_id, name, default_subject, owner_teacher_id, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?)`, classId, schoolId, className, subject, teacherId, now, now);
    this.exec("INSERT INTO ClassTeachers (class_id, teacher_id) VALUES (?, ?)", classId, teacherId);
    return classId;
  }

  recordLegalConsent(teacherId) {
    this.exec(`UPDATE Users SET legal_accepted_at_utc = ?, terms_version = ?, privacy_version = ?, updated_at_utc = ? WHERE id = ?`, isoNow(), TERMS_VERSION, PRIVACY_VERSION, isoNow(), teacherId);
  }

  async verifyFirebaseToken(idToken) {
    const apiKey = String(this.env.FIREBASE_WEB_API_KEY || "").trim();
    if (!apiKey) return null;
    let response;
    try {
      response = await fetch(`https://identitytoolkit.googleapis.com/v1/accounts:lookup?key=${encodeURIComponent(apiKey)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ idToken })
      });
    } catch (_) { return null; }
    if (!response.ok) return null;
    const result = await response.json().catch(() => null);
    const user = result?.users?.[0];
    if (!user?.localId || !user?.email) return null;
    return { localId: user.localId, email: user.email, displayName: user.displayName || "" };
  }

  async createUniqueJoinCode() {
    for (let attempt = 0; attempt < 20; attempt += 1) {
      const code = randomCode();
      if (!this.one("SELECT device_id FROM StudentCodes WHERE join_code = ?", code)) return code;
    }
    throw new Error("Could not allocate a unique student code.");
  }

  consumeRateLimit(rateKey, limit, windowMs) {
    const existing = this.one("SELECT * FROM RateLimits WHERE rate_key = ?", rateKey);
    const now = Date.now();
    if (!existing || Date.parse(existing.reset_at_utc) <= now) {
      this.exec(`INSERT INTO RateLimits (rate_key, attempt_count, reset_at_utc) VALUES (?, 1, ?)
        ON CONFLICT(rate_key) DO UPDATE SET attempt_count = 1, reset_at_utc = excluded.reset_at_utc`, rateKey, new Date(now + windowMs).toISOString());
      return true;
    }
    if (existing.attempt_count >= limit) return false;
    this.exec("UPDATE RateLimits SET attempt_count = attempt_count + 1 WHERE rate_key = ?", rateKey);
    return true;
  }

  audit({ schoolId = null, classId = null, sessionId = null, teacherId = null, studentId = null, deviceId = null, requestId = null, action, result, reason = null }) {
    this.exec(`INSERT INTO AuditEvents (event_id, timestamp_utc, school_id, class_id, session_id, teacher_id, student_id, device_id, request_id, action, result, reason) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`, crypto.randomUUID(), isoNow(), schoolId, classId, sessionId, teacherId, studentId, deviceId, requestId, action, result, reason);
  }

  all(statement, ...parameters) {
    return Array.from(this.sql.exec(statement, ...parameters));
  }

  one(statement, ...parameters) {
    return this.all(statement, ...parameters)[0] || null;
  }

  exec(statement, ...parameters) {
    this.sql.exec(statement, ...parameters);
  }
}

function corsHeaders(request, env) {
  const origin = request.headers.get("Origin");
  const allowed = [String(env.CONSOLE_ORIGIN || "").trim(), "https://classroom-2en.pages.dev"].filter(Boolean);
  const headers = new Headers({
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
    "Referrer-Policy": "no-referrer",
    "Access-Control-Allow-Methods": "GET, POST, PUT, DELETE, OPTIONS",
    "Access-Control-Allow-Headers": "Authorization, Content-Type",
    "Access-Control-Max-Age": "86400"
  });
  if (origin && allowed.includes(origin)) {
    headers.set("Access-Control-Allow-Origin", origin);
    headers.set("Vary", "Origin");
  }
  return headers;
}

function corsResponse(request, env) {
  return new Response(null, { status: 204, headers: corsHeaders(request, env) });
}

function responseJson(payload, status, cors, extraHeaders = {}) {
  const headers = new Headers(cors);
  headers.set("Content-Type", "application/json; charset=utf-8");
  for (const [name, value] of Object.entries(extraHeaders)) headers.set(name, value);
  return new Response(JSON.stringify(payload), { status, headers });
}

function responseError(code, message, status, cors, extraHeaders = {}) {
  return responseJson({ code, message }, status, cors, extraHeaders);
}

async function readJson(request) {
  const contentType = request.headers.get("Content-Type") || "";
  if (!contentType.includes("application/json")) return null;
  try { return await request.json(); } catch (_) { return null; }
}

function bearerToken(request) {
  const authorization = request.headers.get("Authorization") || "";
  return authorization.startsWith("Bearer ") ? authorization.slice(7).trim() : null;
}

function clientIp(request) {
  return request.headers.get("CF-Connecting-IP") || request.headers.get("X-Forwarded-For") || "unknown";
}

function isoNow() { return new Date().toISOString(); }

function text(value, maxLength) {
  if (typeof value !== "string") return "";
  const normalized = value.trim();
  if (!normalized || normalized.length > maxLength || /[\u0000-\u001F\u007F]/.test(normalized)) return "";
  return normalized;
}

function normalizeIdentifier(value) { return text(value, 254).toLowerCase(); }
function normalizeEmail(value) { return normalizeIdentifier(value); }
function normalizeJoinCode(value) { return text(value, 32).toUpperCase().replace(/[^A-Z0-9]/g, ""); }
function validId(value) { return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value); }
function numberInRange(value, min, max) { const number = Number(value); return Number.isInteger(number) && number >= min && number <= max ? number : null; }
function placeholders(count) { return Array.from({ length: count }, () => "?").join(", "); }
function isSafeHttpsUrl(value) { try { return new URL(value).protocol === "https:"; } catch (_) { return false; } }

function serializeSession(row) {
  return { sessionId: row.session_id, schoolId: row.school_id, classId: row.class_id, subject: row.subject, startedAtUtc: row.started_at_utc, endedAtUtc: row.ended_at_utc || null };
}

function serializeDevice(device, activeSessionId, now) {
  let activity = null;
  try { activity = device.activity_json ? JSON.parse(device.activity_json) : null; } catch (_) { activity = null; }
  const lastSeen = device.last_heartbeat_utc ? Date.parse(device.last_heartbeat_utc) : 0;
  return {
    deviceId: device.device_id,
    studentId: device.student_id,
    classId: device.class_id,
    sessionId: activeSessionId || "00000000-0000-0000-0000-000000000000",
    studentDisplayName: device.student_display_name,
    computerName: device.computer_name,
    online: Boolean(lastSeen && now - lastSeen <= ONLINE_WINDOW_MS),
    lastHeartbeatUtc: device.last_heartbeat_utc || device.issued_at_utc,
    agentVersion: device.agent_version,
    activity,
    batteryPercent: device.battery_percent ?? null,
    networkStatus: device.network_status || null,
    policyApplied: Boolean(device.policy_applied),
    screenSharingAvailable: false
  };
}

function envelope(type, payload) {
  return JSON.stringify({ version: 1, messageId: crypto.randomUUID(), type, sentAtUtc: isoNow(), payload });
}

function randomCode() {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => CODE_ALPHABET[byte % CODE_ALPHABET.length]).join("");
}

async function randomToken() {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return base64Url(bytes);
}

async function sha256Text(value) {
  const result = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return base64Url(new Uint8Array(result));
}

async function hashPassword(password) {
  const saltBytes = new Uint8Array(16);
  crypto.getRandomValues(saltBytes);
  const salt = base64Url(saltBytes);
  const hash = await derivePassword(password, salt, PASSWORD_ITERATIONS);
  return { salt, hash };
}

async function verifyPassword(password, salt, expectedHash, iterations) {
  if (!password || !salt || !expectedHash) return false;
  const actualHash = await derivePassword(password, salt, Number(iterations) || PASSWORD_ITERATIONS);
  return constantTimeEqual(actualHash, expectedHash);
}

async function derivePassword(password, salt, iterations) {
  const baseKey = await crypto.subtle.importKey("raw", new TextEncoder().encode(password), "PBKDF2", false, ["deriveBits"]);
  const derived = await crypto.subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt: base64UrlBytes(salt), iterations }, baseKey, 256);
  return base64Url(new Uint8Array(derived));
}

async function constantTimeEqual(left, right) {
  if (typeof left !== "string" || typeof right !== "string") return false;
  const leftBytes = new TextEncoder().encode(left);
  const rightBytes = new TextEncoder().encode(right);
  const max = Math.max(leftBytes.length, rightBytes.length);
  let difference = leftBytes.length ^ rightBytes.length;
  for (let index = 0; index < max; index += 1) difference |= (leftBytes[index] || 0) ^ (rightBytes[index] || 0);
  return difference === 0;
}

function base64Url(bytes) {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function base64UrlBytes(value) {
  const padded = value.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (value.length % 4)) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}
