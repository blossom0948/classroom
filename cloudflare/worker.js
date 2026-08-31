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
const PASSWORD_VERIFICATION_LIFETIME_MS = 10 * 60 * 1000;
const MAX_PASSWORD_VERIFICATION_ATTEMPTS = 5;
const TERMS_VERSION = "2026-08-30";
const PRIVACY_VERSION = "2026-08-30";
const MAX_MESSAGE_BYTES = 64 * 1024;
const MAX_COMMAND_TARGETS = 30;
const MAX_ROSTER_ROWS = 100;
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
    this.screenFrames = new Map();
    this.ready = ctx.blockConcurrencyWhile(async () => this.initialize());
  }

  async initialize() {
    this.exec(`CREATE TABLE IF NOT EXISTS Schools (
      id TEXT PRIMARY KEY,
      name TEXT NOT NULL,
      education_office_code TEXT,
      school_code TEXT,
      address TEXT,
      school_type TEXT,
      created_at_utc TEXT NOT NULL,
      updated_at_utc TEXT NOT NULL
    )`);
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
      school_selected INTEGER NOT NULL DEFAULT 0,
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
      grade INTEGER,
      class_number INTEGER,
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
    this.exec(`CREATE TABLE IF NOT EXISTS GuestSessions (
      token_hash TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
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
    this.exec(`CREATE TABLE IF NOT EXISTS StudentAdminGrants (
      school_id TEXT NOT NULL,
      class_id TEXT NOT NULL,
      student_id TEXT NOT NULL,
      student_display_name TEXT NOT NULL,
      granted_by_teacher_id TEXT NOT NULL,
      created_at_utc TEXT NOT NULL,
      active INTEGER NOT NULL DEFAULT 1,
      PRIMARY KEY (school_id, class_id, student_id)
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS StudentCodes (
      device_id TEXT PRIMARY KEY,
      school_id TEXT NOT NULL,
      class_id TEXT NOT NULL,
      student_id TEXT NOT NULL,
      student_display_name TEXT NOT NULL,
      grade INTEGER,
      class_number INTEGER,
      student_number INTEGER,
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
      grade INTEGER,
      class_number INTEGER,
      student_number INTEGER,
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
    this.exec(`CREATE TABLE IF NOT EXISTS PasswordVerifications (
      verification_id TEXT PRIMARY KEY,
      teacher_id TEXT NOT NULL,
      email TEXT NOT NULL,
      code_hash TEXT NOT NULL,
      expires_at_utc TEXT NOT NULL,
      attempts INTEGER NOT NULL DEFAULT 0,
      verified_at_utc TEXT,
      consumed_at_utc TEXT,
      created_at_utc TEXT NOT NULL
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS StudentExitPins (
      school_id TEXT PRIMARY KEY,
      pin_salt TEXT NOT NULL,
      pin_hash TEXT NOT NULL,
      pin_iterations INTEGER NOT NULL,
      updated_by_teacher_id TEXT NOT NULL,
      updated_at_utc TEXT NOT NULL
    )`);
    this.exec(`CREATE TABLE IF NOT EXISTS GuestPasswords (
      school_id TEXT PRIMARY KEY,
      password_salt TEXT NOT NULL,
      password_hash TEXT NOT NULL,
      password_iterations INTEGER NOT NULL,
      updated_by_teacher_id TEXT NOT NULL,
      updated_at_utc TEXT NOT NULL
    )`);
    this.exec("CREATE INDEX IF NOT EXISTS idx_devices_class ON Devices(class_id, revoked_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_codes_class ON StudentCodes(class_id, revoked_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_sessions_teacher ON TeacherSessions(teacher_id, expires_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_guest_sessions_school ON GuestSessions(school_id, expires_at_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_audit_class ON AuditEvents(class_id, timestamp_utc)");
    this.exec("CREATE INDEX IF NOT EXISTS idx_student_admin_school ON StudentAdminGrants(school_id, active)");

    // Existing Durable Object databases predate school and roster metadata.
    // Additive migrations keep already-enrolled devices and accounts intact.
    this.ensureColumn("Users", "school_selected", "INTEGER NOT NULL DEFAULT 0");
    this.ensureColumn("Classes", "grade", "INTEGER");
    this.ensureColumn("Classes", "class_number", "INTEGER");
    this.ensureColumn("StudentCodes", "grade", "INTEGER");
    this.ensureColumn("StudentCodes", "class_number", "INTEGER");
    this.ensureColumn("StudentCodes", "student_number", "INTEGER");
    this.ensureColumn("Devices", "grade", "INTEGER");
    this.ensureColumn("Devices", "class_number", "INTEGER");
    this.ensureColumn("Devices", "student_number", "INTEGER");
    // Older releases created a new device row every time a persistent
    // student code was entered again. Reconcile those rows before adding the
    // invariant that one student has one active device per class.
    this.deduplicateActiveDevices();
    this.exec("CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_active_student ON Devices(class_id, student_id) WHERE revoked_at_utc IS NULL");

    const rootSchool = this.one("SELECT id FROM Schools WHERE id = ?", ROOT_SCHOOL_ID);
    if (!rootSchool) {
      const now = isoNow();
      this.exec(`INSERT INTO Schools (id, name, school_type, created_at_utc, updated_at_utc)
        VALUES (?, ?, ?, ?, ?)`, ROOT_SCHOOL_ID, "Classroom 학교", "미설정", now, now);
    }

    const existingRoot = this.one("SELECT id FROM Users WHERE id = ?", ROOT_TEACHER_ID);
    if (!existingRoot) {
      const now = isoNow();
      this.exec(`INSERT INTO Users (
        id, school_id, login_name, display_name, subject, firebase_email,
        is_admin, profile_completed, school_selected, created_at_utc, updated_at_utc
      ) VALUES (?, ?, ?, ?, '', ?, 1, 0, 0, ?, ?)`,
      ROOT_TEACHER_ID, ROOT_SCHOOL_ID, ROOT_LOGIN, "선생님", ROOT_EMAIL, now, now);
      this.exec(`INSERT INTO Classes (
        id, school_id, name, default_subject, grade, class_number, owner_teacher_id, created_at_utc, updated_at_utc
      ) VALUES (?, ?, ?, '', NULL, NULL, ?, ?, ?)`,
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
        return responseJson({
          service: "Classroom.Cloud",
          status: "ready",
          database: "durable-object-sqlite",
          integrations: {
            schoolSearch: Boolean(String(this.env.NEIS_API_KEY || "").trim()),
            emailVerification: Boolean(String(this.env.RESEND_API_KEY || "").trim() && String(this.env.CLASSROOM_EMAIL_FROM || "").trim())
          }
        }, 200, cors);
      }
      if (path === "/ws/student") return this.handleStudentWebSocket(request, url, cors);
      if (path === "/auth/login" && request.method === "POST") return this.login(request, cors);
      if (path === "/auth/guest-login" && request.method === "POST") return this.guestLogin(request, cors);
      if (path === "/auth/firebase-login" && request.method === "POST") return this.firebaseLogin(request, cors);
      if (path === "/auth/me" && request.method === "GET") return this.getMe(request, cors);
      if (path === "/auth/logout" && request.method === "POST") return this.logout(request, cors);
      if (path === "/auth/profile" && request.method === "PUT") return this.updateProfile(request, cors);
      if (path === "/auth/password-verification/start" && request.method === "POST") return this.startPasswordVerification(request, cors);
      if (path === "/auth/password-verification/verify" && request.method === "POST") return this.verifyPasswordVerification(request, cors);
      if (path === "/auth/change-password" && request.method === "POST") return this.changePassword(request, cors);
      if (path === "/api/schools/search" && request.method === "GET") return this.searchSchools(request, url, cors);
      if (path === "/api/classes" && request.method === "GET") return this.getClasses(request, cors);
      if (path === "/api/admin/operations-status" && request.method === "GET") return this.getOperationsStatus(request, cors);
      if (path === "/api/student-codes" && request.method === "GET") return this.getStudentCodes(request, cors);
      if (path === "/api/admin/classes" && request.method === "POST") return this.createClass(request, cors);
      if (path === "/api/admin/student-codes/import" && request.method === "POST") return this.importStudentCodes(request, cors);
      if (path === "/api/admin/teachers" && request.method === "GET") return this.getAdministrators(request, cors);
      if (path === "/api/admin/teachers" && request.method === "POST") return this.setAdministrator(request, cors);
      if (path === "/api/admin/student-exit-pin" && request.method === "GET") return this.getStudentExitPinStatus(request, cors);
      if (path === "/api/admin/student-exit-pin" && request.method === "PUT") return this.setStudentExitPin(request, cors);
      if (path === "/api/admin/guest-password" && request.method === "GET") return this.getGuestPasswordStatus(request, cors);
      if (path === "/api/admin/guest-password" && request.method === "PUT") return this.setGuestPassword(request, cors);
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
      const screensMatch = path.match(/^\/api\/classes\/([^/]+)\/screens$/);
      if (screensMatch && request.method === "GET") return this.getScreens(request, screensMatch[1], cors);
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

  async guestLogin(request, cors) {
    const body = await readJson(request);
    const schoolId = normalizeSchoolId(body?.schoolId);
    const password = normalizeGuestPassword(body?.password);
    const rateKey = `${clientIp(request)}|guest|${schoolId || "unknown"}`;
    if (!this.consumeRateLimit(rateKey, 8, 60_000)) {
      return responseError("LOGIN_RATE_LIMITED", "게스트 로그인 시도가 많습니다. 잠시 후 다시 시도해 주세요.", 429, cors, { "Retry-After": "60" });
    }

    const school = schoolId ? this.one("SELECT * FROM Schools WHERE id = ?", schoolId) : null;
    const configured = schoolId ? this.one("SELECT * FROM GuestPasswords WHERE school_id = ?", schoolId) : null;
    const valid = Boolean(
      school
      && configured
      && password
      && await verifyPassword(password, configured.password_salt, configured.password_hash, configured.password_iterations)
    );
    if (!valid) {
      return responseError("INVALID_GUEST_CREDENTIALS", "학교 또는 게스트 비밀번호가 올바르지 않습니다.", 401, cors);
    }

    const token = await randomToken();
    const expiresAt = new Date(Date.now() + SESSION_LIFETIME_MS).toISOString();
    this.exec("INSERT INTO GuestSessions (token_hash, school_id, expires_at_utc, created_at_utc) VALUES (?, ?, ?, ?)", await sha256Text(token), schoolId, expiresAt, isoNow());
    return responseJson(this.serializeGuestSession(token, school, expiresAt), 200, cors);
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
        is_admin, profile_completed, school_selected, created_at_utc, updated_at_utc
      ) VALUES (?, ?, ?, ?, '', ?, ?, ?, 0, 0, ?, ?)`,
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
    if (token) {
      const tokenHash = await sha256Text(token);
      this.exec("UPDATE TeacherSessions SET revoked_at_utc = ? WHERE token_hash = ?", isoNow(), tokenHash);
      this.exec("UPDATE GuestSessions SET revoked_at_utc = ? WHERE token_hash = ?", isoNow(), tokenHash);
    }
    return new Response(null, { status: 204, headers: cors });
  }

  async updateProfile(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 프로필을 변경할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const displayName = text(body?.displayName, 80);
    const subject = text(body?.subject, 128);
    const className = text(body?.className, 80);
    const requestedSchoolId = text(body?.schoolId, 120);
    if (!displayName || displayName.length < 2) return responseError("INVALID_PROFILE", "선생님 이름을 2자 이상 입력해 주세요.", 400, cors);
    const schoolId = requestedSchoolId || (user.school_selected ? user.school_id : "");
    const school = schoolId ? this.one("SELECT * FROM Schools WHERE id = ?", schoolId) : null;
    if (!school) return responseError("SCHOOL_REQUIRED", "학교 검색 결과에서 학교를 선택해 주세요.", 400, cors);
    if (body?.termsAccepted !== true || body?.privacyAccepted !== true) {
      if (!user.legal_accepted_at_utc) return responseError("LEGAL_CONSENT_REQUIRED", "이용약관과 개인정보처리방침 동의가 필요합니다.", 400, cors);
    }
    const now = isoNow();
    if (user.school_id !== school.id) {
      this.exec("UPDATE Classes SET school_id = ? WHERE owner_teacher_id = ?", school.id, user.id);
      this.exec("UPDATE StudentCodes SET school_id = ? WHERE class_id IN (SELECT id FROM Classes WHERE owner_teacher_id = ?)", school.id, user.id);
      this.exec("UPDATE Devices SET school_id = ? WHERE class_id IN (SELECT id FROM Classes WHERE owner_teacher_id = ?)", school.id, user.id);
    }
    this.exec(`UPDATE Users SET school_id = ?, school_selected = 1, display_name = ?, subject = ?, profile_completed = 1, updated_at_utc = ? WHERE id = ?`, school.id, displayName, subject, now, user.id);
    if (body?.termsAccepted === true && body?.privacyAccepted === true) this.recordLegalConsent(user.id);
    const ownClass = this.one("SELECT * FROM Classes WHERE owner_teacher_id = ? ORDER BY created_at_utc LIMIT 1", user.id);
    const parsedClass = parseClassLabel(className);
    const grade = numberInRange(body?.grade, 1, 12) || parsedClass.grade;
    const classNumber = numberInRange(body?.classNumber, 1, 99) || parsedClass.classNumber;
    if (ownClass && !className && ownClass.name === "나의 첫 수업"
      && !this.one("SELECT device_id FROM StudentCodes WHERE class_id = ?", ownClass.id)
      && !this.one("SELECT device_id FROM Devices WHERE class_id = ?", ownClass.id)) {
      this.exec("DELETE FROM ClassTeachers WHERE class_id = ?", ownClass.id);
      this.exec("DELETE FROM Classes WHERE id = ?", ownClass.id);
    } else if (ownClass && className) {
      this.exec("UPDATE Classes SET name = ?, default_subject = ?, grade = ?, class_number = ?, updated_at_utc = ? WHERE id = ?", className, subject, grade, classNumber, now, ownClass.id);
    } else if (!ownClass) {
      this.createStarterClass(user.id, school.id, className || "나의 첫 수업", subject, grade, classNumber);
    }
    const updated = this.one("SELECT * FROM Users WHERE id = ?", user.id);
    return responseJson(this.serializeTeacherSession(updated), 200, cors);
  }

  async changePassword(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 비밀번호를 변경할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const newPassword = text(body?.newPassword, 256);
    if (!newPassword || newPassword.length < 6) {
      return responseError("INVALID_PASSWORD", "비밀번호는 6자 이상으로 설정해 주세요.", 400, cors);
    }
    const verificationId = text(body?.verificationId, 80);
    const verification = verificationId
      ? this.one("SELECT * FROM PasswordVerifications WHERE verification_id = ? AND teacher_id = ? AND consumed_at_utc IS NULL", verificationId, user.id)
      : null;
    if (!verification || !verification.verified_at_utc || Date.parse(verification.expires_at_utc) <= Date.now()) {
      return responseError("PASSWORD_VERIFICATION_REQUIRED", "이메일로 받은 확인 코드를 먼저 확인해 주세요.", 409, cors);
    }
    const encoded = await hashPassword(newPassword);
    this.exec(`UPDATE Users SET password_salt = ?, password_hash = ?, password_iterations = ?, updated_at_utc = ? WHERE id = ?`, encoded.salt, encoded.hash, PASSWORD_ITERATIONS, isoNow(), user.id);
    this.exec("UPDATE PasswordVerifications SET consumed_at_utc = ? WHERE verification_id = ?", isoNow(), verification.verification_id);
    const currentToken = bearerToken(request);
    if (currentToken) this.exec("UPDATE TeacherSessions SET revoked_at_utc = ? WHERE teacher_id = ? AND token_hash <> ? AND revoked_at_utc IS NULL", isoNow(), user.id, await sha256Text(currentToken));
    return new Response(null, { status: 204, headers: cors });
  }

  async startPasswordVerification(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 계정 보안을 변경할 수 없습니다.", 403, cors);
    const email = normalizeEmail(user.firebase_email);
    if (!email) return responseError("PASSWORD_EMAIL_REQUIRED", "확인 메일을 받을 이메일이 계정에 없습니다.", 400, cors);
    if (!this.consumeRateLimit(`${clientIp(request)}|password-verification|${user.id}`, 3, 10 * 60_000)) {
      return responseError("VERIFICATION_RATE_LIMITED", "확인 메일 요청이 많습니다. 잠시 후 다시 시도해 주세요.", 429, cors, { "Retry-After": "600" });
    }
    const emailApiKey = String(this.env.RESEND_API_KEY || "").trim();
    const emailFrom = String(this.env.CLASSROOM_EMAIL_FROM || "").trim();
    if (!emailApiKey || !emailFrom) {
      return responseError("VERIFICATION_EMAIL_NOT_CONFIGURED", "확인 메일 서버가 아직 설정되지 않았습니다. 관리자에게 이메일 발송 설정을 요청해 주세요.", 503, cors);
    }
    const code = randomVerificationCode();
    const verificationId = crypto.randomUUID();
    const now = isoNow();
    const expiresAtUtc = new Date(Date.now() + PASSWORD_VERIFICATION_LIFETIME_MS).toISOString();
    this.exec("UPDATE PasswordVerifications SET consumed_at_utc = ? WHERE teacher_id = ? AND consumed_at_utc IS NULL", now, user.id);
    this.exec(`INSERT INTO PasswordVerifications (verification_id, teacher_id, email, code_hash, expires_at_utc, created_at_utc)
      VALUES (?, ?, ?, ?, ?, ?)`, verificationId, user.id, email, await sha256Text(code), expiresAtUtc, now);
    try {
      const response = await fetch("https://api.resend.com/emails", {
        method: "POST",
        headers: { Authorization: `Bearer ${emailApiKey}`, "Content-Type": "application/json" },
        body: JSON.stringify({
          from: emailFrom,
          to: [email],
          subject: "Classroom 비밀번호 확인 코드",
          text: `Classroom 비밀번호 확인 코드: ${code}\n\n이 코드는 10분 동안 유효합니다. 본인이 요청하지 않았다면 이 메일을 무시하세요.`,
          html: `<p>Classroom 비밀번호 확인 코드</p><p style="font-size:28px;font-weight:700;letter-spacing:6px">${code}</p><p>이 코드는 10분 동안 유효합니다. 본인이 요청하지 않았다면 이 메일을 무시하세요.</p>`
        })
      });
      if (!response.ok) throw new Error("Email provider rejected the request.");
    } catch (_) {
      this.exec("DELETE FROM PasswordVerifications WHERE verification_id = ?", verificationId);
      return responseError("VERIFICATION_EMAIL_FAILED", "확인 메일을 보내지 못했습니다. 잠시 후 다시 시도해 주세요.", 502, cors);
    }
    return responseJson({ verificationId, email: maskEmail(email), expiresAtUtc }, 200, cors);
  }

  async verifyPasswordVerification(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 계정 보안을 변경할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const verificationId = text(body?.verificationId, 80);
    const code = text(body?.code, 6);
    if (!verificationId || !/^\d{6}$/.test(code)) return responseError("INVALID_VERIFICATION_CODE", "6자리 확인 코드를 입력해 주세요.", 400, cors);
    const verification = this.one("SELECT * FROM PasswordVerifications WHERE verification_id = ? AND teacher_id = ? AND consumed_at_utc IS NULL", verificationId, user.id);
    if (!verification || Date.parse(verification.expires_at_utc) <= Date.now() || Number(verification.attempts) >= MAX_PASSWORD_VERIFICATION_ATTEMPTS) {
      return responseError("VERIFICATION_EXPIRED", "확인 코드가 만료되었습니다. 새 코드를 요청해 주세요.", 400, cors);
    }
    if (!(await constantTimeEqual(await sha256Text(code), verification.code_hash))) {
      this.exec("UPDATE PasswordVerifications SET attempts = attempts + 1 WHERE verification_id = ?", verificationId);
      return responseError("INVALID_VERIFICATION_CODE", "확인 코드가 올바르지 않습니다.", 400, cors);
    }
    this.exec("UPDATE PasswordVerifications SET verified_at_utc = ? WHERE verification_id = ?", isoNow(), verificationId);
    return responseJson({ verificationId, verified: true, expiresAtUtc: verification.expires_at_utc }, 200, cors);
  }

  async getClasses(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    return responseJson(this.classesForTeacher(user), 200, cors);
  }

  async getOperationsStatus(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 운영 상태를 확인할 수 있습니다.", 403, cors);

    const neisConfigured = Boolean(String(this.env.NEIS_API_KEY || "").trim());
    const resendKeyConfigured = Boolean(String(this.env.RESEND_API_KEY || "").trim());
    const senderConfigured = Boolean(String(this.env.CLASSROOM_EMAIL_FROM || "").trim());
    return responseJson({
      checkedAtUtc: isoNow(),
      schoolSearch: {
        configured: neisConfigured,
        label: neisConfigured ? "NEIS 학교 검색 사용 가능" : "NEIS 인증키 필요"
      },
      emailVerification: {
        configured: resendKeyConfigured && senderConfigured,
        providerKeyConfigured: resendKeyConfigured,
        senderConfigured,
        label: resendKeyConfigured && senderConfigured ? "확인 메일 발송 사용 가능" : "Resend 키와 인증 발신 주소 필요"
      },
      studentStatus: {
        configured: true,
        mode: "visible-status",
        heartbeatSeconds: 10,
        screenSharingAvailable: true,
        remoteControlMode: "allow-listed"
      }
    }, 200, cors);
  }

  async searchSchools(request, url, cors) {
    const query = text(url.searchParams.get("q"), 80);
    if (query.length < 2) return responseJson([], 200, cors);
    if (!this.consumeRateLimit(`${clientIp(request)}|school-search`, 30, 60_000)) {
      return responseError("SCHOOL_SEARCH_RATE_LIMITED", "학교 검색 요청이 많습니다. 잠시 후 다시 시도해 주세요.", 429, cors, { "Retry-After": "60" });
    }
    const apiKey = String(this.env.NEIS_API_KEY || "").trim();
    if (!apiKey) {
      return responseError("SCHOOL_SEARCH_NOT_CONFIGURED", "학교 검색을 준비하는 중입니다. 관리자에게 NEIS 인증키 설정을 요청해 주세요.", 503, cors);
    }

    const endpoint = new URL("https://open.neis.go.kr/hub/schoolInfo");
    endpoint.searchParams.set("KEY", apiKey);
    endpoint.searchParams.set("Type", "json");
    endpoint.searchParams.set("pIndex", "1");
    endpoint.searchParams.set("pSize", "20");
    endpoint.searchParams.set("SCHUL_NM", query);
    let response;
    try {
      response = await fetch(endpoint);
    } catch (_) {
      return responseError("SCHOOL_SEARCH_UNAVAILABLE", "학교 검색 서버에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.", 502, cors);
    }
    const payload = await response.json().catch(() => null);
    if (!response.ok) return responseError("SCHOOL_SEARCH_UNAVAILABLE", "학교 검색 서버가 응답하지 않았습니다.", 502, cors);
    const neisResultCode = String(payload?.RESULT?.CODE || "").trim();
    if (neisResultCode.startsWith("ERROR")) {
      return responseError("SCHOOL_SEARCH_AUTH_FAILED", "NEIS 인증키가 유효하지 않거나 아직 활성화되지 않았습니다. NEIS에서 발급 상태를 확인해 주세요.", 503, cors);
    }
    const rows = Array.isArray(payload?.schoolInfo)
      ? payload.schoolInfo.find((item) => Array.isArray(item?.row))?.row || []
      : [];
    if (!rows.length) {
      const message = payload?.RESULT?.MESSAGE || "검색 결과가 없습니다.";
      return responseJson([], 200, cors, { "X-Classroom-School-Search": message });
    }
    const now = isoNow();
    const schools = rows.map((row) => {
      const educationOfficeCode = text(row.ATPT_OFCDC_SC_CODE, 32);
      const schoolCode = text(row.SD_SCHUL_CODE, 32);
      const school = {
        id: `neis:${educationOfficeCode}:${schoolCode}`,
        name: text(row.SCHUL_NM, 160),
        educationOfficeCode,
        schoolCode,
        address: text(row.ORG_RDNMA, 256),
        schoolType: text(row.SCHUL_KND_SC_NM, 80)
      };
      if (school.name && educationOfficeCode && schoolCode) {
        this.exec(`INSERT INTO Schools (id, name, education_office_code, school_code, address, school_type, created_at_utc, updated_at_utc)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?)
          ON CONFLICT(id) DO UPDATE SET name = excluded.name, address = excluded.address, school_type = excluded.school_type, updated_at_utc = excluded.updated_at_utc`,
        school.id, school.name, school.educationOfficeCode, school.schoolCode, school.address, school.schoolType, now, now);
      }
      return school;
    }).filter((school) => school.name && school.id !== "neis::");
    return responseJson(schools, 200, cors);
  }

  async createClass(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "학급 생성은 관리자만 할 수 있습니다.", 403, cors);
    const body = await readJson(request);
    const grade = numberInRange(body?.grade, 1, 12);
    const classNumber = numberInRange(body?.classNumber, 1, 99);
    if (!grade || !classNumber) return responseError("INVALID_CLASS", "학년과 반을 올바르게 입력해 주세요.", 400, cors);
    const subject = text(body?.subject, 128);
    const name = `${grade}학년 ${classNumber}반`;
    const existing = this.one("SELECT * FROM Classes WHERE school_id = ? AND grade = ? AND class_number = ?", user.school_id, grade, classNumber);
    const now = isoNow();
    let classItem;
    if (existing) {
      this.exec("UPDATE Classes SET name = ?, default_subject = ?, updated_at_utc = ? WHERE id = ?", name, subject, now, existing.id);
      classItem = this.one("SELECT * FROM Classes WHERE id = ?", existing.id);
    } else {
      const classId = crypto.randomUUID();
      this.exec(`INSERT INTO Classes (id, school_id, name, default_subject, grade, class_number, owner_teacher_id, created_at_utc, updated_at_utc)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`, classId, user.school_id, name, subject, grade, classNumber, user.id, now, now);
      // A school admin-created class is visible to every teacher in that school;
      // every API request remains school-scoped and audited.
      const teachers = this.all("SELECT id FROM Users WHERE school_id = ?", user.school_id);
      for (const teacher of teachers) this.exec("INSERT OR IGNORE INTO ClassTeachers (class_id, teacher_id) VALUES (?, ?)", classId, teacher.id);
      classItem = this.one("SELECT * FROM Classes WHERE id = ?", classId);
    }
    this.audit({ schoolId: user.school_id, classId: classItem.id, teacherId: user.id, action: "CLASS", result: existing ? "UPDATED" : "CREATED", reason: name });
    return responseJson(serializeClass(classItem), 200, cors);
  }

  async importStudentCodes(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "학생 코드 발급은 관리자만 할 수 있습니다.", 403, cors);
    const body = await readJson(request);
    const classId = text(body?.classId, 80);
    const classItem = classId ? this.one("SELECT * FROM Classes WHERE id = ?", classId) : null;
    if (!classItem || !this.canAccessClass(user, classId)) return responseError("CLASS_NOT_FOUND", "학생 코드를 만들 학급을 선택해 주세요.", 400, cors);
    if (!Array.isArray(body?.students)) return responseError("INVALID_ROSTER", "학생 명단을 확인해 주세요.", 400, cors);
    const rows = body.students.slice(0, MAX_ROSTER_ROWS);
    const seenNumbers = new Set();
    const codes = [];
    let skipped = 0;
    for (const row of rows) {
      const studentName = text(row?.studentDisplayName || row?.name, 128);
      const studentNumber = numberInRange(row?.studentNumber || row?.number, 1, 99);
      if (!studentName || !studentNumber || seenNumbers.has(studentNumber)) {
        skipped += 1;
        continue;
      }
      seenNumbers.add(studentNumber);
      codes.push(await this.upsertStudentCode(user, classItem, studentName, `${classId}:number:${studentNumber}`, studentNumber, false));
    }
    if (!codes.length) return responseError("EMPTY_ROSTER", "번호와 이름이 포함된 학생 명단을 찾지 못했습니다.", 400, cors);
    this.audit({ schoolId: classItem.school_id, classId, teacherId: user.id, action: "STUDENT_ROSTER", result: "IMPORTED", reason: `${codes.length}명` });
    return responseJson({ imported: codes.length, skipped, codes }, 200, cors);
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
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 수업을 시작할 수 없습니다.", 403, cors);
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
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 수업을 종료할 수 없습니다.", 403, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const session = this.one("SELECT * FROM ClassSessions WHERE session_id = ? AND class_id = ? AND ended_at_utc IS NULL", sessionId, classId);
    if (!session) return responseError("SESSION_NOT_FOUND", "진행 중인 수업을 찾지 못했습니다.", 404, cors);
    const now = isoNow();
    this.exec("UPDATE ClassSessions SET ended_at_utc = ? WHERE session_id = ?", now, sessionId);
    this.exec("UPDATE Devices SET policy_applied = 0, active_session_id = NULL WHERE class_id = ?", classId);
    for (const device of this.all("SELECT device_id FROM Devices WHERE class_id = ?", classId)) this.screenFrames.delete(device.device_id);
    this.audit({ schoolId: session.school_id, classId, sessionId, teacherId: user.id, action: "CLASS_SESSION", result: "ENDED", reason: session.subject });
    this.notifyClassSession(classId, "00000000-0000-0000-0000-000000000000");
    return responseJson({ ...serializeSession({ ...session, ended_at_utc: now }) }, 200, cors);
  }

  async getStudents(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    this.deduplicateActiveDevices();
    const active = this.one("SELECT session_id FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL ORDER BY started_at_utc DESC LIMIT 1", classId);
    const now = Date.now();
    const devices = this.all("SELECT * FROM Devices WHERE class_id = ? AND revoked_at_utc IS NULL ORDER BY COALESCE(student_number, 999), student_display_name COLLATE NOCASE, computer_name COLLATE NOCASE", classId);
    return responseJson(devices.map((device) => serializeDevice(device, active?.session_id || null, now)), 200, cors);
  }

  async getScreens(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 화면 공유를 사용할 수 없습니다.", 403, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const now = Date.now();
    const devices = new Map(this.all("SELECT device_id, student_display_name FROM Devices WHERE class_id = ? AND revoked_at_utc IS NULL", classId)
      .map((device) => [device.device_id, device]));
    const screens = [];
    for (const [deviceId, value] of this.screenFrames.entries()) {
      if (now - value.receivedAt > 15_000) {
        this.screenFrames.delete(deviceId);
        continue;
      }
      const device = devices.get(deviceId);
      if (!device) continue;
      screens.push({
        deviceId,
        studentDisplayName: device.student_display_name,
        screenFrame: value.screenFrame,
        receivedAtUtc: new Date(value.receivedAt).toISOString()
      });
    }
    screens.sort((left, right) => left.studentDisplayName.localeCompare(right.studentDisplayName, "ko"));
    return responseJson(screens, 200, cors);
  }

  async revokeDevice(request, classId, deviceId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 장치 연결을 변경할 수 없습니다.", 403, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const device = this.one("SELECT * FROM Devices WHERE device_id = ? AND class_id = ? AND revoked_at_utc IS NULL", deviceId, classId);
    if (!device) return responseError("DEVICE_NOT_FOUND", "학생 장치를 찾지 못했습니다.", 404, cors);
    const now = isoNow();
    this.exec("UPDATE Devices SET revoked_at_utc = ? WHERE device_id = ?", now, deviceId);
    this.screenFrames.delete(deviceId);
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
    if (!studentName) return responseError("INVALID_REQUEST", "학생 이름을 입력해 주세요.", 400, cors);
    const classItem = this.one("SELECT * FROM Classes WHERE id = ?", classId);
    const studentNumber = numberInRange(body?.studentNumber, 1, 99);
    const requestedStudentId = text(body?.studentId, 80);
    const studentId = requestedStudentId || (studentNumber ? `${classId}:number:${studentNumber}` : crypto.randomUUID());
    const result = await this.upsertStudentCode(user, classItem, studentName, studentId, studentNumber, true);
    return responseJson(result, 200, cors);
  }

  async upsertStudentCode(user, classItem, studentName, studentId, studentNumber, reissueCode) {
    const classId = classItem.id;
    const existing = this.one("SELECT * FROM StudentCodes WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", classId, studentId);
    if (existing && !reissueCode) {
      this.exec("UPDATE StudentCodes SET student_display_name = ?, grade = ?, class_number = ?, student_number = ? WHERE device_id = ?", studentName, classItem.grade, classItem.class_number, studentNumber, existing.device_id);
      this.exec("UPDATE Devices SET student_display_name = ?, grade = ?, class_number = ?, student_number = ? WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", studentName, classItem.grade, classItem.class_number, studentNumber, classId, studentId);
      return this.serializeStudentCode({ ...existing, student_display_name: studentName, grade: classItem.grade, class_number: classItem.class_number, student_number: studentNumber });
    }
    const code = await this.createUniqueJoinCode();
    const now = isoNow();
    const deviceId = existing?.device_id || crypto.randomUUID();
    if (existing) {
      this.exec(`UPDATE StudentCodes SET student_display_name = ?, grade = ?, class_number = ?, student_number = ?, join_code = ?, code_created_at_utc = ?, last_used_at_utc = NULL, created_by_teacher_id = ?, revoked_at_utc = NULL WHERE device_id = ?`, studentName, classItem.grade, classItem.class_number, studentNumber, code, now, user.id, deviceId);
      const enrolledDevices = this.all("SELECT device_id FROM Devices WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", classId, studentId);
      this.exec("UPDATE Devices SET revoked_at_utc = ? WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL", now, classId, studentId);
      for (const enrolledDevice of enrolledDevices) {
        this.closeDeviceSockets(enrolledDevice.device_id, 1008, "Student code reissued");
      }
    } else {
      this.exec(`INSERT INTO StudentCodes (device_id, school_id, class_id, student_id, student_display_name, grade, class_number, student_number, join_code, code_created_at_utc, created_by_teacher_id) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`, deviceId, classItem.school_id, classId, studentId, studentName, classItem.grade, classItem.class_number, studentNumber, code, now, user.id);
    }
    this.audit({ schoolId: classItem.school_id, classId, teacherId: user.id, studentId, deviceId, action: "STUDENT_CODE", result: existing ? "REISSUED" : "CREATED", reason: studentName });
    return {
      deviceId,
      schoolId: classItem.school_id,
      classId,
      studentId,
      studentDisplayName: studentName,
      grade: classItem.grade ?? null,
      classNumber: classItem.class_number ?? null,
      studentNumber: studentNumber ?? null,
      expiresAtUtc: null,
      enrollmentToken: "",
      joinCode: code
    };
  }

  serializeStudentCode(row) {
    return {
      deviceId: row.device_id,
      schoolId: row.school_id,
      classId: row.class_id,
      studentId: row.student_id,
      studentDisplayName: row.student_display_name,
      grade: row.grade ?? null,
      classNumber: row.class_number ?? null,
      studentNumber: row.student_number ?? null,
      expiresAtUtc: null,
      enrollmentToken: "",
      joinCode: row.join_code,
      createdAtUtc: row.code_created_at_utc || null,
      lastUsedAtUtc: row.last_used_at_utc || null
    };
  }

  async getStudentCodes(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    const rows = this.all(`SELECT sc.*, c.name AS class_name, c.default_subject AS class_subject, c.grade AS class_grade, c.class_number AS class_class_number, u.display_name AS created_by_display_name
      FROM StudentCodes sc
      JOIN Classes c ON c.id = sc.class_id
      JOIN Users u ON u.id = sc.created_by_teacher_id
      WHERE sc.school_id = ? AND sc.revoked_at_utc IS NULL
      ORDER BY COALESCE(c.grade, 999), COALESCE(c.class_number, 999), COALESCE(sc.student_number, 999), sc.student_display_name COLLATE NOCASE`, user.school_id);
    return responseJson(rows.map((row) => ({
      deviceId: row.device_id,
      schoolId: row.school_id,
      classId: row.class_id,
      className: row.class_name,
      subject: row.class_subject,
      studentId: row.student_id,
      studentDisplayName: row.student_display_name,
      grade: row.grade ?? row.class_grade ?? null,
      classNumber: row.class_number ?? row.class_class_number ?? null,
      studentNumber: row.student_number ?? null,
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
    // A persistent student code identifies a roster entry, not a one-time
    // installation. Reinstalling the agent must renew the same device record
    // instead of creating another card for the same student.
    const existingDevices = this.all(`SELECT * FROM Devices
      WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL
      ORDER BY CASE WHEN last_heartbeat_utc IS NULL THEN 1 ELSE 0 END,
        last_heartbeat_utc DESC, issued_at_utc DESC, device_id ASC`, code.class_id, code.student_id);
    const existingDevice = existingDevices[0] || null;
    const deviceId = existingDevice?.device_id || crypto.randomUUID();
    const token = await randomToken();
    const now = isoNow();
    for (const duplicate of existingDevices.slice(1)) {
      this.exec("UPDATE Devices SET revoked_at_utc = ?, active_session_id = NULL, policy_applied = 0 WHERE device_id = ?", now, duplicate.device_id);
      this.closeDeviceSockets(duplicate.device_id, 1008, "Duplicate student device replaced");
    }
    if (existingDevice) {
      this.closeDeviceSockets(existingDevice.device_id, 1008, "Student device re-enrolled");
      this.exec(`UPDATE Devices SET
        school_id = ?, class_id = ?, student_id = ?, student_display_name = ?, grade = ?, class_number = ?, student_number = ?, computer_name = ?,
        agent_version = ?, device_token_hash = ?, issued_at_utc = ?, last_heartbeat_utc = NULL, activity_json = NULL,
        battery_percent = NULL, network_status = NULL, policy_applied = 0, active_session_id = NULL, revoked_at_utc = NULL
        WHERE device_id = ?`, code.school_id, code.class_id, code.student_id, code.student_display_name, code.grade, code.class_number, code.student_number, deviceName, agentVersion, await sha256Text(token), now, deviceId);
    } else {
      this.exec(`INSERT INTO Devices (
        device_id, school_id, class_id, student_id, student_display_name, grade, class_number, student_number, computer_name,
        agent_version, device_token_hash, issued_at_utc
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`, deviceId, code.school_id, code.class_id, code.student_id, code.student_display_name, code.grade, code.class_number, code.student_number, deviceName, agentVersion, await sha256Text(token), now);
    }
    this.exec("UPDATE StudentCodes SET last_used_at_utc = ? WHERE device_id = ?", now, code.device_id);
    this.audit({ schoolId: code.school_id, classId: code.class_id, studentId: code.student_id, deviceId, action: "DEVICE_ENROLLMENT", result: existingDevice ? "REUSED" : "SUCCESS", reason: deviceName });
    return responseJson({
      deviceId,
      schoolId: code.school_id,
      classId: code.class_id,
      studentId: code.student_id,
      deviceToken: token,
      issuedAtUtc: now,
      reused: Boolean(existingDevice)
    }, 200, cors);
  }

  async queueCommand(request, classId, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 학생 장치에 명령을 보낼 수 없습니다.", 403, cors);
    if (!this.canAccessClass(user, classId)) return responseError("FORBIDDEN", "이 학급에 접근할 수 없습니다.", 403, cors);
    const body = await readJson(request);
    const activeSession = this.one("SELECT * FROM ClassSessions WHERE class_id = ? AND ended_at_utc IS NULL ORDER BY started_at_utc DESC LIMIT 1", classId);
    if (!activeSession) return responseError("SESSION_NOT_ACTIVE", "수업을 시작한 후 명령을 보낼 수 있습니다.", 409, cors);
    const kind = text(body?.kind, 64);
    if (!new Set(["message", "openUrl", "focusMode", "launchApprovedApp", "screenShare"]).has(kind)) {
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
      focusEnabled: typeof body?.focusEnabled === "boolean" ? body.focusEnabled : null,
      screenShareEnabled: typeof body?.screenShareEnabled === "boolean" ? body.screenShareEnabled : null
    };
    if (kind === "message" && !payload.message) return responseError("INVALID_COMMAND", "보낼 메시지를 입력해 주세요.", 400, cors);
    if (kind === "openUrl" && (!payload.url || !isSafeHttpsUrl(payload.url))) return responseError("INVALID_COMMAND", "HTTPS 주소를 입력해 주세요.", 400, cors);
    if (kind === "launchApprovedApp" && !payload.approvedAppId) return responseError("INVALID_COMMAND", "실행할 앱을 선택해 주세요.", 400, cors);
    if (kind === "screenShare" && payload.screenShareEnabled === null) return responseError("INVALID_COMMAND", "화면 공유 상태를 확인해 주세요.", 400, cors);

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
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 명령 기록을 확인할 수 없습니다.", 403, cors);
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
    if (user.is_guest) return responseError("GUEST_READ_ONLY", "게스트 로그인에서는 감사 기록을 확인할 수 없습니다.", 403, cors);
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
    const students = this.all(`SELECT sc.school_id, sc.class_id, sc.student_id, sc.student_display_name,
        sc.grade, sc.class_number, sc.student_number, c.name AS class_name,
        COALESCE(sag.active, 0) AS is_admin
      FROM StudentCodes sc
      JOIN Classes c ON c.id = sc.class_id
      LEFT JOIN StudentAdminGrants sag
        ON sag.school_id = sc.school_id AND sag.class_id = sc.class_id AND sag.student_id = sc.student_id AND sag.active = 1
      WHERE sc.school_id = ? AND sc.revoked_at_utc IS NULL
      ORDER BY COALESCE(sc.grade, 999), COALESCE(sc.class_number, 999), COALESCE(sc.student_number, 999), sc.student_display_name COLLATE NOCASE`, user.school_id)
      .map((row) => ({
        studentId: row.student_id,
        classId: row.class_id,
        className: row.class_name,
        studentDisplayName: row.student_display_name,
        grade: row.grade ?? null,
        classNumber: row.class_number ?? null,
        studentNumber: row.student_number ?? null,
        isAdmin: Boolean(row.is_admin)
      }));
    return responseJson({ teachers, grants, students }, 200, cors);
  }

  async getStudentExitPinStatus(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 학생 앱 종료 비밀번호를 확인할 수 있습니다.", 403, cors);
    const pin = this.one("SELECT updated_at_utc FROM StudentExitPins WHERE school_id = ?", user.school_id);
    return responseJson({
      configured: Boolean(pin),
      updatedAtUtc: pin?.updated_at_utc || null
    }, 200, cors);
  }

  async setStudentExitPin(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 학생 앱 종료 비밀번호를 설정할 수 있습니다.", 403, cors);
    const body = await readJson(request);
    const pin = normalizeStudentExitPin(body?.pin);
    if (!pin) return responseError("INVALID_EXIT_PIN", "학생 앱 종료 비밀번호는 6~64자로 입력해 주세요.", 400, cors);
    const encoded = await hashPassword(pin);
    const now = isoNow();
    this.exec(`INSERT INTO StudentExitPins (
      school_id, pin_salt, pin_hash, pin_iterations, updated_by_teacher_id, updated_at_utc
    ) VALUES (?, ?, ?, ?, ?, ?)
    ON CONFLICT(school_id) DO UPDATE SET
      pin_salt = excluded.pin_salt,
      pin_hash = excluded.pin_hash,
      pin_iterations = excluded.pin_iterations,
      updated_by_teacher_id = excluded.updated_by_teacher_id,
      updated_at_utc = excluded.updated_at_utc`,
    user.school_id, encoded.salt, encoded.hash, PASSWORD_ITERATIONS, user.id, now);
    this.audit({ schoolId: user.school_id, teacherId: user.id, action: "STUDENT_EXIT_PIN", result: "SET" });
    return responseJson({ configured: true, updatedAtUtc: now }, 200, cors);
  }

  async getGuestPasswordStatus(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest || !user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 게스트 비밀번호를 확인할 수 있습니다.", 403, cors);
    const password = this.one("SELECT updated_at_utc FROM GuestPasswords WHERE school_id = ?", user.school_id);
    return responseJson({
      configured: Boolean(password),
      updatedAtUtc: password?.updated_at_utc || null
    }, 200, cors);
  }

  async setGuestPassword(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (user.is_guest || !user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 게스트 비밀번호를 설정할 수 있습니다.", 403, cors);
    const body = await readJson(request);
    const password = normalizeGuestPassword(body?.password);
    if (!password) return responseError("INVALID_GUEST_PASSWORD", "게스트 비밀번호는 6~64자로 입력해 주세요.", 400, cors);
    const encoded = await hashPassword(password);
    const now = isoNow();
    this.exec(`INSERT INTO GuestPasswords (
      school_id, password_salt, password_hash, password_iterations, updated_by_teacher_id, updated_at_utc
    ) VALUES (?, ?, ?, ?, ?, ?)
    ON CONFLICT(school_id) DO UPDATE SET
      password_salt = excluded.password_salt,
      password_hash = excluded.password_hash,
      password_iterations = excluded.password_iterations,
      updated_by_teacher_id = excluded.updated_by_teacher_id,
      updated_at_utc = excluded.updated_at_utc`,
    user.school_id, encoded.salt, encoded.hash, PASSWORD_ITERATIONS, user.id, now);
    this.audit({ schoolId: user.school_id, teacherId: user.id, action: "GUEST_PASSWORD", result: "SET" });
    return responseJson({ configured: true, updatedAtUtc: now }, 200, cors);
  }

  async setAdministrator(request, cors) {
    const user = await this.authenticate(request);
    if (!user) return responseError("UNAUTHORIZED", "로그인이 필요합니다.", 401, cors);
    if (!user.is_admin) return responseError("ADMIN_REQUIRED", "관리자만 권한을 변경할 수 있습니다.", 403, cors);
    const body = await readJson(request);
    const kind = text(body?.kind, 32).toLowerCase() || "teacher";
    if (kind === "student") return this.setStudentAdministrator(user, body, cors);
    if (kind !== "teacher") return responseError("INVALID_ADMIN_TARGET", "관리자 지정 대상을 확인해 주세요.", 400, cors);
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

  async setStudentAdministrator(user, body, cors) {
    const studentId = text(body?.studentId, 128);
    const classId = text(body?.classId, 128);
    const isAdmin = body?.isAdmin === true;
    if (!studentId || !classId) return responseError("INVALID_STUDENT", "관리자 권한을 줄 학생을 선택해 주세요.", 400, cors);
    const student = this.one(`SELECT * FROM StudentCodes
      WHERE school_id = ? AND class_id = ? AND student_id = ? AND revoked_at_utc IS NULL`, user.school_id, classId, studentId);
    if (!student) return responseError("STUDENT_NOT_FOUND", "등록된 학생을 찾지 못했습니다.", 404, cors);
    const now = isoNow();
    this.exec(`INSERT INTO StudentAdminGrants
      (school_id, class_id, student_id, student_display_name, granted_by_teacher_id, created_at_utc, active)
      VALUES (?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(school_id, class_id, student_id) DO UPDATE SET
        student_display_name = excluded.student_display_name,
        granted_by_teacher_id = excluded.granted_by_teacher_id,
        created_at_utc = excluded.created_at_utc,
        active = excluded.active`,
      user.school_id, classId, studentId, student.student_display_name, user.id, now, isAdmin ? 1 : 0);
    this.audit({ schoolId: user.school_id, classId, teacherId: user.id, studentId, action: "STUDENT_ADMIN_ACCESS", result: isAdmin ? "GRANTED" : "REVOKED", reason: student.student_display_name });
    return responseJson({ kind: "student", studentId, classId, isAdmin, accountFound: true, permissionScope: "student-account-only" }, 200, cors);
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
      const activity = normalizeActivity(incoming.payload.activity);
      const screenFrame = normalizeScreenFrame(incoming.payload.screenFrame);
      if (incoming.payload.screenSharingEnabled === true && screenFrame) {
        this.screenFrames.set(device.device_id, { screenFrame, receivedAt: Date.now() });
      } else if (incoming.payload.screenSharingEnabled !== true) {
        this.screenFrames.delete(device.device_id);
      }
      this.exec(`UPDATE Devices SET last_heartbeat_utc = ?, agent_version = ?, activity_json = ?, battery_percent = ?, network_status = ?, policy_applied = ?, active_session_id = ? WHERE device_id = ?`,
        now,
        text(incoming.payload.agentVersion, 128) || device.agent_version,
        activity ? JSON.stringify(activity) : null,
        numberInRange(incoming.payload.batteryPercent, 0, 100),
        text(incoming.payload.networkStatus, 64) || null,
        incoming.payload.policyApplied === true ? 1 : 0,
        active?.session_id || null,
        device.device_id);
      if (incoming.payload.sessionId !== (active?.session_id || "00000000-0000-0000-0000-000000000000")) this.sendSessionAccepted(socket, device.device_id, active?.session_id || "00000000-0000-0000-0000-000000000000");
      return;
    }
    if (incoming.type === "DEVICE_EXIT_PIN_VERIFICATION_REQUEST") {
      const payload = incoming.payload;
      const requestId = validId(payload?.requestId) ? payload.requestId : null;
      const pin = normalizeStudentExitPin(payload?.pin);
      if (!requestId || !pin) {
        socket.send(envelope("DEVICE_EXIT_PIN_VERIFICATION_RESPONSE", {
          requestId: requestId || "00000000-0000-0000-0000-000000000000",
          approved: false,
          code: "EXIT_PIN_INVALID",
          message: "종료 비밀번호는 6~64자로 입력해 주세요."
        }));
        return;
      }

      const rateKey = `student-exit-pin:${device.device_id}`;
      if (!this.consumeRateLimit(rateKey, 5, 5 * 60_000)) {
        this.audit({ schoolId: device.school_id, classId: device.class_id, studentId: device.student_id, deviceId: device.device_id, action: "STUDENT_APP_EXIT_PIN", result: "RATE_LIMITED" });
        socket.send(envelope("DEVICE_EXIT_PIN_VERIFICATION_RESPONSE", {
          requestId,
          approved: false,
          code: "EXIT_PIN_RATE_LIMITED",
          message: "종료 비밀번호 확인 시도가 많습니다. 잠시 후 다시 시도해 주세요."
        }));
        return;
      }

      const configured = this.one("SELECT * FROM StudentExitPins WHERE school_id = ?", device.school_id);
      if (!configured) {
        socket.send(envelope("DEVICE_EXIT_PIN_VERIFICATION_RESPONSE", {
          requestId,
          approved: false,
          code: "EXIT_PIN_NOT_CONFIGURED",
          message: "관리자가 학생 앱 종료 비밀번호를 아직 설정하지 않았습니다."
        }));
        return;
      }

      const approved = await verifyPassword(pin, configured.pin_salt, configured.pin_hash, configured.pin_iterations);
      if (approved) this.exec("DELETE FROM RateLimits WHERE rate_key = ?", rateKey);
      this.audit({
        schoolId: device.school_id,
        classId: device.class_id,
        studentId: device.student_id,
        deviceId: device.device_id,
        action: "STUDENT_APP_EXIT_PIN",
        result: approved ? "APPROVED" : "REJECTED"
      });
      socket.send(envelope("DEVICE_EXIT_PIN_VERIFICATION_RESPONSE", {
        requestId,
        approved,
        code: approved ? "EXIT_PIN_APPROVED" : "EXIT_PIN_REJECTED",
        message: approved ? "종료 비밀번호를 확인했습니다." : "종료 비밀번호가 올바르지 않습니다."
      }));
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

  deduplicateActiveDevices() {
    const groups = this.all(`SELECT class_id, student_id
      FROM Devices
      WHERE revoked_at_utc IS NULL
      GROUP BY class_id, student_id
      HAVING COUNT(*) > 1`);
    for (const group of groups) {
      const devices = this.all(`SELECT device_id
        FROM Devices
        WHERE class_id = ? AND student_id = ? AND revoked_at_utc IS NULL
        ORDER BY CASE WHEN last_heartbeat_utc IS NULL THEN 1 ELSE 0 END,
          last_heartbeat_utc DESC, issued_at_utc DESC, device_id ASC`, group.class_id, group.student_id);
      const now = isoNow();
      for (const duplicate of devices.slice(1)) {
        this.exec("UPDATE Devices SET revoked_at_utc = ?, active_session_id = NULL, policy_applied = 0 WHERE device_id = ?", now, duplicate.device_id);
        this.closeDeviceSockets(duplicate.device_id, 1008, "Duplicate student device removed");
      }
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
    if (row) return row;
    const guest = this.one("SELECT token_hash, school_id FROM GuestSessions WHERE token_hash = ? AND revoked_at_utc IS NULL AND expires_at_utc > ?", await sha256Text(token), now);
    if (!guest || !this.one("SELECT id FROM Schools WHERE id = ?", guest.school_id)) return null;
    return {
      id: `guest:${guest.token_hash}`,
      school_id: guest.school_id,
      login_name: "guest",
      display_name: "게스트",
      subject: "",
      firebase_email: "",
      password_hash: null,
      is_admin: 0,
      profile_completed: 1,
      school_selected: 1,
      legal_accepted_at_utc: isoNow(),
      is_guest: 1
    };
  }

  async createSessionPayload(user) {
    const token = await randomToken();
    const expiresAt = new Date(Date.now() + SESSION_LIFETIME_MS).toISOString();
    this.exec("INSERT INTO TeacherSessions (token_hash, teacher_id, expires_at_utc, created_at_utc) VALUES (?, ?, ?, ?)", await sha256Text(token), user.id, expiresAt, isoNow());
    const session = this.serializeTeacherSession(user);
    return { accessToken: token, expiresAtUtc: expiresAt, teacherId: user.id, displayName: user.display_name, loginName: user.login_name, email: user.firebase_email || "", classes: session.classes, isAdmin: Boolean(user.is_admin), isGuest: false, profileCompleted: Boolean(user.profile_completed), schoolSelected: session.schoolSelected, school: session.school, subject: user.subject || "", hasPassword: Boolean(user.password_hash), legalAccepted: Boolean(user.legal_accepted_at_utc) };
  }

  serializeGuestSession(token, school, expiresAtUtc) {
    return {
      accessToken: token,
      expiresAtUtc,
      teacherId: null,
      displayName: "게스트",
      loginName: "guest",
      email: "",
      classes: this.classesForTeacher({ school_id: school.id, school_selected: 1, is_admin: 0, id: "guest" }),
      isAdmin: false,
      isGuest: true,
      profileCompleted: true,
      schoolSelected: true,
      school: { id: school.id, name: school.name, address: school.address || "", schoolType: school.school_type || "" },
      subject: "",
      hasPassword: false,
      legalAccepted: true
    };
  }

  serializeTeacherSession(user) {
    const school = this.one("SELECT * FROM Schools WHERE id = ?", user.school_id);
    return {
      teacherId: user.id,
      displayName: user.display_name,
      loginName: user.login_name,
      email: user.firebase_email || "",
      classes: this.classesForTeacher(user),
      isAdmin: Boolean(user.is_admin),
      isGuest: Boolean(user.is_guest),
      profileCompleted: Boolean(user.profile_completed),
      schoolSelected: Boolean(user.school_selected && school),
      school: school ? { id: school.id, name: school.name, address: school.address || "", schoolType: school.school_type || "" } : null,
      subject: user.subject || "",
      hasPassword: Boolean(user.password_hash),
      legalAccepted: Boolean(user.legal_accepted_at_utc)
    };
  }

  classesForTeacher(user) {
    const rows = this.all("SELECT * FROM Classes WHERE school_id = ? ORDER BY COALESCE(grade, 999), COALESCE(class_number, 999), name COLLATE NOCASE", user.school_id);
    return rows.map(serializeClass);
  }

  canAccessClass(user, classId) {
    const classItem = this.one("SELECT * FROM Classes WHERE id = ?", classId);
    if (!classItem || classItem.school_id !== user.school_id) return false;
    if (user.is_admin) return true;
    // Teachers can switch between classes registered in their selected school.
    // The school boundary is still enforced server-side for every read/write.
    return Boolean(this.one("SELECT class_id FROM ClassTeachers WHERE class_id = ? AND teacher_id = ?", classId, user.id))
      || Boolean(user.school_selected);
  }

  createStarterClass(teacherId, schoolId, className, subject, grade = null, classNumber = null) {
    const now = isoNow();
    const classId = crypto.randomUUID();
    this.exec(`INSERT INTO Classes (id, school_id, name, default_subject, grade, class_number, owner_teacher_id, created_at_utc, updated_at_utc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`, classId, schoolId, className, subject, grade, classNumber, teacherId, now, now);
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

  ensureColumn(tableName, columnName, definition) {
    const columns = this.all(`PRAGMA table_info(${tableName})`);
    if (!columns.some((column) => column.name === columnName)) {
      this.exec(`ALTER TABLE ${tableName} ADD COLUMN ${columnName} ${definition}`);
    }
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
function normalizeSchoolId(value) {
  const normalized = text(value, 120);
  return /^(?:neis:[A-Za-z0-9_-]+:[A-Za-z0-9_-]+|[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})$/i.test(normalized)
    ? normalized
    : "";
}
function normalizeGuestPassword(value) {
  if (typeof value !== "string") return "";
  const normalized = value.trim();
  return normalized.length >= 6
    && normalized.length <= 64
    && !/[\u0000-\u001F\u007F]/.test(normalized)
    ? normalized
    : "";
}
function normalizeJoinCode(value) { return text(value, 32).toUpperCase().replace(/[^A-Z0-9]/g, ""); }
function normalizeStudentExitPin(value) {
  if (typeof value !== "string") return "";
  const normalized = value.trim();
  return normalized.length >= 6
    && normalized.length <= 64
    && !/[\u0000-\u001F\u007F]/.test(normalized)
    ? normalized
    : "";
}
function validId(value) { return typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value); }
function numberInRange(value, min, max) { const number = Number(value); return Number.isInteger(number) && number >= min && number <= max ? number : null; }
function placeholders(count) { return Array.from({ length: count }, () => "?").join(", "); }
function isSafeHttpsUrl(value) { try { return new URL(value).protocol === "https:"; } catch (_) { return false; } }

function serializeClass(row) {
  return {
    id: row.id,
    schoolId: row.school_id,
    name: row.name,
    grade: row.grade ?? null,
    classNumber: row.class_number ?? null,
    defaultSubject: row.default_subject
  };
}

function parseClassLabel(value) {
  const match = String(value || "").match(/(\d{1,2})\s*학년\s*(\d{1,2})\s*반/);
  return match ? { grade: Number(match[1]), classNumber: Number(match[2]) } : { grade: null, classNumber: null };
}

function classifyActivity(activity) {
  if (!activity) return { level: "unknown", label: "확인 필요", reason: "활동 정보 없음" };
  const app = `${activity.ApplicationDisplayName || activity.applicationDisplayName || ""} ${activity.ProcessName || activity.processName || ""}`.toLowerCase();
  const domain = String(activity.BrowserDomain || activity.browserDomain || "").toLowerCase();
  if (domain.includes("youtube.com") || domain.includes("youtu.be")) return { level: "excluded", label: "웹 도메인 제외", reason: "YouTube는 위험 신호에서 제외" };
  const gamingTerms = ["roblox", "minecraft", "fortnite", "steam", "epicgames", "leagueoflegends", "valorant", "game"];
  if (gamingTerms.some((term) => app.includes(term) || domain.includes(term))) {
    return { level: "warning", label: "확인 필요", reason: "게임 관련 앱 또는 도메인으로 분류됨" };
  }
  return { level: "ok", label: "정상", reason: "허용된 상태 신호" };
}

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
    activityRisk: classifyActivity(activity),
    grade: device.grade ?? null,
    classNumber: device.class_number ?? null,
    studentNumber: device.student_number ?? null,
    batteryPercent: device.battery_percent ?? null,
    networkStatus: device.network_status || null,
    policyApplied: Boolean(device.policy_applied),
    screenSharingAvailable: true,
    statusSharingMode: "visible-status"
  };
}

function normalizeActivity(value) {
  if (!value || typeof value !== "object") return null;
  const applicationDisplayName = text(value.applicationDisplayName || value.ApplicationDisplayName, 128);
  const processName = text(value.processName || value.ProcessName, 128);
  if (!applicationDisplayName || !processName) return null;
  const browserDomain = normalizeBrowserDomain(value.browserDomain || value.BrowserDomain);
  const windowTitle = text(value.windowTitle || value.WindowTitle, 256) || null;
  const observedAt = text(value.observedAtUtc || value.ObservedAtUtc, 64);
  const parsedObservedAt = observedAt && !Number.isNaN(Date.parse(observedAt))
    ? new Date(observedAt).toISOString()
    : isoNow();
  return {
    applicationDisplayName,
    processName,
    browserDomain,
    windowTitle,
    observedAtUtc: parsedObservedAt
  };
}

function normalizeScreenFrame(value) {
  if (!value || typeof value !== "object" || value.mimeType !== "image/jpeg") return null;
  const base64Data = typeof value.base64Data === "string" ? value.base64Data : "";
  const width = numberInRange(value.width, 1, 640);
  const height = numberInRange(value.height, 1, 480);
  if (!width || !height || !base64Data || base64Data.length > 49_152 || !/^[A-Za-z0-9+/]+={0,2}$/.test(base64Data)) return null;
  try {
    if (atob(base64Data).length > 36 * 1024) return null;
  } catch (_) { return null; }
  const capturedAt = Date.parse(value.capturedAtUtc);
  return {
    mimeType: "image/jpeg",
    base64Data,
    width,
    height,
    capturedAtUtc: Number.isFinite(capturedAt) ? new Date(capturedAt).toISOString() : isoNow()
  };
}

function normalizeBrowserDomain(value) {
  const candidate = text(value, 253).trim().toLowerCase();
  if (!candidate || candidate.includes("/") || candidate.includes("?") || candidate.includes("#") || candidate.includes("@")) return null;
  return UriHostName(candidate) ? candidate : null;
}

function UriHostName(value) {
  const hostnamePattern = /^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?))*$/;
  return hostnamePattern.test(value) && value.includes(".");
}

function envelope(type, payload) {
  return JSON.stringify({ version: 1, messageId: crypto.randomUUID(), type, sentAtUtc: isoNow(), payload });
}

function randomCode() {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (byte) => CODE_ALPHABET[byte % CODE_ALPHABET.length]).join("");
}

function randomVerificationCode() {
  const bytes = new Uint32Array(1);
  crypto.getRandomValues(bytes);
  return String(bytes[0] % 1_000_000).padStart(6, "0");
}

function maskEmail(value) {
  const [local, domain] = String(value || "").split("@", 2);
  if (!local || !domain) return "이메일";
  const visible = local.length <= 2 ? local.slice(0, 1) : local.slice(0, 2);
  return `${visible}${"*".repeat(Math.max(1, local.length - visible.length))}@${domain}`;
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
