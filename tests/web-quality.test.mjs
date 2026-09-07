import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const [html, script, styles, config, updater, helper, desktopProgram, desktopForm, watchdog, desktopOptions, desktopPipeClient, agentWorker, desktopBridge, diagnostics, setupProgram, setupForm, elevatedInstaller, installScript, buildPagesScript, cloudflareWorker] = await Promise.all([
  readFile(new URL("../src/Classroom.Server/wwwroot/index.html", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Server/wwwroot/app.js", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Server/wwwroot/styles.css", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Server/wwwroot/config.js", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Service/StudentUpdateWorker.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Service/StudentUpdateHelper.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Desktop/Program.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Desktop/Ui/StudentDesktopForm.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Desktop/StudentDesktopWatchdog.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Desktop/Configuration/StudentDesktopOptions.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Desktop/Networking/DesktopPipeClient.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Service/StudentAgentWorker.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Service/Desktop/DesktopStatusBridge.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Desktop/StudentDesktopDiagnostics.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Setup/Program.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Setup/StudentSetupForm.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Setup/ElevatedStudentInstaller.cs", import.meta.url), "utf8"),
  readFile(new URL("../scripts/install/Install-ClassroomStudent.ps1", import.meta.url), "utf8"),
  readFile(new URL("../scripts/deploy/build-pages.mjs", import.meta.url), "utf8"),
  readFile(new URL("../cloudflare/worker.js", import.meta.url), "utf8")
]);

const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map((match) => match[1]));
const dynamicIds = new Set([
  "detail-message-button",
  "detail-revoke-button",
  "detail-screen-button",
  "detail-screen-fullscreen",
  "detail-screen-stage",
  "detail-screen-stop",
  "detail-remote-surface",
  "detail-remote-request",
  "detail-remote-end",
  "detail-remote-toggle",
  "monitor-fullscreen-exit",
  "operations-status",
  "security-setting"
]);
const referencedIds = [...new Set([...script.matchAll(/\$\("([^"]+)"\)/g)].map((match) => match[1]))];
const missing = referencedIds.filter((id) => !ids.has(id) && !dynamicIds.has(id));

assert.deepEqual(missing, [], `Missing static DOM IDs: ${missing.join(", ")}`);
assert.match(script, /function startClassPolling\(\)/, "Class refresh must use a single scheduled polling loop.");
assert.match(script, /function normalizeStudent\(student\)/, "Student payloads must be normalized before rendering.");
assert.match(script, /Array\.isArray\(students\)/, "Malformed student responses must not replace the roster.");
assert.doesNotMatch(script, /\$\("security-setting"\)\.textContent/, "Removed settings UI must stay null-safe.");
assert.match(html, /id="class-sync-status"/, "The console needs a visible sync recovery status.");
assert.match(html, /id="student-sort"/, "The class roster needs a sorting control.");
assert.match(html, /id="focus-display-mode"/, "The focus command needs a visible presentation selector.");
assert.match(html, /id="command-schedule"/, "Command dialogs need a visible delayed-execution selector.");
assert.match(html, /id="command-schedule-minutes"/, "Command dialogs need a custom minute input.");
assert.match(html, /id="command-schedule-at"/, "Command dialogs need a custom date-time input.");
assert.match(script, /focusDisplayMode: state\.focusDisplayMode/, "The console must send the selected focus presentation.");
assert.match(cloudflareWorker, /focusDisplayMode:\s*focusDisplayMode/, "The Worker must forward the selected focus presentation to student devices.");
assert.match(cloudflareWorker, /집중 화면 표시 방식은 집중 모드에서만 사용할 수 있습니다/, "The Worker must reject focus presentation values on unrelated commands.");
assert.match(cloudflareWorker, /MAX_SCHEDULE_DELAY_SECONDS = 7 \* 24 \* 60 \* 60/, "Scheduled commands need a bounded maximum delay.");
assert.match(cloudflareWorker, /c\.scheduled_for_utc IS NULL OR c\.scheduled_for_utc <= \?/, "Queued commands must wait until their scheduled time before delivery.");
assert.match(cloudflareWorker, /requestedScheduleAt/, "The Worker must accept an exact scheduled date and time.");
assert.match(cloudflareWorker, /if \(!scheduledForUtc && kind === "focusMode"\)/, "Scheduled focus commands must not change roster state before delivery.");
assert.match(script, /scheduleDelaySeconds/, "The command dialog must send the selected delayed-execution interval.");
assert.match(cloudflareWorker, /SESSION_LIFETIME_MS = 1000 \* 60 \* 60 \* 24 \* 30/, "Sessions need a long-lived lifetime for classroom consoles.");
assert.match(cloudflareWorker, /renewTeacherSession\(tokenHash, row\.session_expires_at_utc\)/, "Active teacher sessions should renew before expiry.");
assert.match(script, /requestToken === state\.token/, "A stale request must not clear a newer login session.");
assert.match(script, /function isSessionAuthenticationFailure\(error\)/, "Session restoration must distinguish expiry from transient outages.");
assert.match(script, /저장된 로그인 상태는 유지하고 재연결 중입니다/, "Transient startup failures should preserve the stored login.");
assert.match(script, /function activateEasterEgg\(\)/, "The teacher console should include a small hidden delight.");
assert.match(styles, /\.easter-egg-layer/, "The hidden classroom celebration needs a dedicated visual layer.");
assert.match(script, /easter-egg-aurora/, "The hidden celebration should render the full-screen visual layers.");
assert.match(styles, /\.easter-egg-vortex/, "The hidden celebration needs an immersive full-screen vortex.");
assert.match(desktopForm, /label\.Text = string\.Empty;[\s\S]*label\.Visible = false;[\s\S]*if \(!blackScreen\)/, "Black-screen focus mode must hide the overlay label and clear its text.");
assert.match(html, /id="alert-drawer"/, "The classroom needs a compact intervention drawer instead of a permanent signal card.");
assert.match(html, /id="tools-dialog"/, "Teacher-only lesson tools should open on demand.");
assert.match(html, /id="preset-dialog"/, "Frequent classroom commands need reusable presets.");
assert.match(html, /id="groups-dialog"/, "Teachers need a student-group workspace.");
assert.match(html, /id="report-dialog"/, "Class records need a dedicated report dialog.");
assert.match(script, /async function acknowledgeAlert\(/, "The alert drawer must support an explicit acknowledgement action.");
assert.match(script, /async function runPreset\(/, "Saved classroom presets must execute through the existing command queue.");
assert.match(script, /async function loadWorkspaceData\(/, "Workspace metadata must reload when the selected class changes.");
assert.match(script, /async function openReportDialog\(/, "Teachers need a report view backed by audit events.");
assert.match(cloudflareWorker, /ClassGroups/, "Worker must persist student groups per class.");
assert.match(cloudflareWorker, /ClassPresets/, "Worker must persist classroom presets per class.");
assert.match(cloudflareWorker, /clearHelp/, "Teacher help acknowledgement must be delivered to student apps.");
assert.match(desktopForm, /ClassroomCommandKind\.ClearHelp/, "Student apps must clear a help request when a teacher acknowledges it.");
assert.doesNotMatch(html, /id="lesson-flow-card"/, "The removed lesson-flow card should not take dashboard space.");
assert.doesNotMatch(html, /id="signal-center"/, "The removed signal center should not take dashboard space.");
assert.match(html, /data-filter="help"/, "Help requests need a dedicated roster filter.");
assert.match(html, /class="command-group command-group-focus"/, "Focus controls must be visually grouped instead of reading as another identical action.");
assert.doesNotMatch(script, /lessonFlow|renderLessonFlow|renderSignalCenter|data-signal-screen/, "Removed dashboard cards must not leave stale lesson or signal code behind.");
assert.doesNotMatch(html, /id="student-density-button"/, "The unused density control should not occupy the classroom toolbar.");
assert.doesNotMatch(html, /id="history-nav"/, "The unused history tab must not occupy the teacher navigation.");
assert.match(html, /id="close-console-button"/, "The installed shell keeps a compatibility close-action hook.");
assert.match(script, /\$\("close-console-button"\)\.hidden = true;/, "The redundant in-page close icon must not crowd responsive console headers.");
assert.match(html, /id="console-close-dialog"/, "Closing the account console needs a branded confirmation dialog.");
assert.match(html, /id="confirm-dialog"/, "Destructive classroom actions need branded confirmations.");
assert.match(html, /id="class-select-menu"/, "The primary class picker needs a branded listbox.");
assert.match(script, /if \(cookieSessionEnabled\) return null;/, "Secure-cookie mode must not read a teacher bearer token from web storage.");
assert.match(script, /storageRemove\("localStorage", TEACHER_TOKEN_KEY\)/, "Switching to secure cookies must clear old persistent bearer tokens.");
assert.match(script, /credentials: cookieSessionEnabled \? "include"/, "Cookie sessions must send same-origin credentials when enabled.");
assert.match(config, /apiOrigin:\s*isLocalClassroom \? window\.location\.origin : "https:\/\/classroom-api\.blossom0948\.cloud"/, "The public console must use the proven direct API session route.");
assert.match(config, /cookieSession:\s*false/, "The public console must avoid the Pages cookie path until a same-site deployment is available.");
assert.match(script, /async function consumePendingFirebaseRedirect\(\)/, "Google redirect credentials must be consumed before session restoration.");
assert.match(script, /if \(await consumePendingFirebaseRedirect\(\)\) return;/, "Google redirect completion must not be sent back to landing by an early session check.");
assert.doesNotMatch(script, /\bconfirm\(/, "Native browser confirmation prompts should not be used in the console.");
assert.match(html, /id="school-login-panel"/, "Normal teacher sign-in needs a dedicated school-account entry.");
assert.match(html, /id="school-login-button"/, "The normal teacher sign-in must expose one school login action.");
assert.match(html, /id="admin-auth-panel"[^>]*hidden/, "Email, password, and Google alternatives must start behind admin login.");
assert.match(html, /id="admin-login-choice"/, "The normal sign-in needs an explicit administrator entry.");
assert.match(html, /id="landing-student-installer-button"/, "The student installer must be downloadable without teacher authentication.");
assert.match(config, /studentInstallerUrl:\s*"https:\/\/classroom-2en\.pages\.dev\/student"/, "The console must advertise the short student installer URL.");
assert.match(buildPagesScript, /join\(outputRoot, "_redirects"\)/, "The Pages build must publish the short student installer redirect.");
assert.match(buildPagesScript, /\/student\s+https:\/\/classroom-api\.blossom0948\.cloud\/downloads\/student-setup\s+302/, "The short student URL must use the Classroom download proxy.");
assert.match(cloudflareWorker, /"\/downloads\/student-setup"/, "The API Worker must stream the public student setup file.");
assert.match(cloudflareWorker, /"\/downloads\/student-package"/, "The API Worker must stream the public student package.");
assert.match(cloudflareWorker, /return new Response\(request\.method === "HEAD" \? null : upstream\.body/, "Large installer files must stream through the Worker without buffering.");
assert.match(cloudflareWorker, /const MAX_MESSAGE_BYTES = 128 \* 1024;/, "The Worker must accept a base64-encoded 720p heartbeat.");
assert.match(cloudflareWorker, /numberInRange\(value\.width, 1, 1_280\)/, "The Worker must accept the protocol 720p screen width.");
assert.match(cloudflareWorker, /numberInRange\(value\.height, 1, 720\)/, "The Worker must accept the protocol 720p screen height.");
assert.match(cloudflareWorker, /atob\(base64Data\)\.length > 72 \* 1024/, "The Worker frame byte limit must match the desktop protocol.");
assert.match(script, /function showAuth\(mode = "school"\)/, "The default auth route must be the school login.");
assert.match(script, /function setAuthMode\(mode = "login"\)/, "The administrator auth mode must default to login.");
assert.match(html, /id="login-view"[^>]*data-auth-entry="school"[^>]*data-auth-mode="login"/, "The initial auth markup must identify school login and administrator login mode deterministically.");
assert.match(script, /if \(resolvedMode === "school"\) \{[\s\S]*?setAuthEntry\("school"\);[\s\S]*?setAuthMode\("login"\);/, "School entry must reset a prior administrator signup tab back to login.");
assert.match(script, /\$\("landing-start-button"\)\.addEventListener\("click", \(\) => showAuth\("school"\)\)/, "The primary start action must open school login first.");
assert.match(script, /\$\("landing-cta-button"\)\.addEventListener\("click", \(\) => showAuth\("school"\)\)/, "The landing call-to-action must open school login first.");
assert.match(script, /\$\("admin-login-choice"\)\.addEventListener\("click", \(\) => showAuth\("admin"\)\)/, "Administrator login must remain an explicit secondary entry.");
assert.match(html, /class="auth-tab active" data-auth-mode="login"/, "The administrator panel must render the login tab as active initially.");
assert.match(styles, /#admin-auth-panel \.auth-tabs\s*\{[\s\S]*grid-template-columns: repeat\(2, minmax\(0, 1fr\)\)/, "Administrator authentication choices must use a visibly distinct segmented control.");
assert.match(styles, /#app-view \.filters,[\s\S]*#app-view \.admin-grant-tabs\s*\{[\s\S]*background:/, "Classroom filters and administrator target tabs must expose a filled switcher surface.");
assert.match(styles, /#app-view \.settings-card\s*\{[\s\S]*border-top: 4px solid var\(--panel-accent\)/, "Management cards must use a category color rail for faster scanning.");
assert.match(styles, /\.command-group\s*\{[\s\S]*background:/, "Classroom actions must be grouped by intent with their own surface.");
assert.match(html, /class="toolbar-row toolbar-browse-row"/, "The roster filters and search need a dedicated browse row.");
assert.match(html, /class="bulk-actions command-deck"/, "Selected-student commands need a dedicated command deck.");
assert.match(html, /class="workspace-dialog workspace-dialog-tools"/, "Lesson tools need a distinct dialog identity.");
assert.match(html, /class="workspace-dialog workspace-dialog-presets/, "Presets need a distinct dialog identity.");
assert.match(html, /class="workspace-dialog workspace-dialog-groups/, "Groups need a distinct dialog identity.");
assert.match(html, /class="workspace-dialog workspace-dialog-report/, "Reports need a distinct dialog identity.");
assert.match(script, /commandDialog\.dataset\.commandKind = kind/, "Command dialogs must expose their current action kind for visual differentiation.");
assert.match(styles, /\.workspace-dialog-tools\s*\{\s*--dialog-accent:/, "Workspace dialogs must use a distinct accent system.");
assert.match(script, /teacher-account[\s\S]*?수업 운영/, "School guests must be presented as classroom operators rather than read-only viewers.");
assert.doesNotMatch(script, /\$\("bulk-actions"\)\.hidden = isGuest/, "School guests must keep the selected-student command deck visible.");
assert.doesNotMatch(script, /start-session-button"\)\.hidden = Boolean\(state\.teacher\?\.isGuest\)/, "School guests must be able to start a class session.");
assert.match(script, /const revokeAction = state\.teacher\?\.isAdmin/, "Device disconnection must remain an administrator-only action.");
assert.doesNotMatch(cloudflareWorker, /async startSession\(request, classId, cors\)[\s\S]{0,320}user\.is_guest/, "School guests must be able to start a class session through the API.");
assert.doesNotMatch(cloudflareWorker, /async queueCommand\(request, classId, cors\)[\s\S]{0,320}user\.is_guest/, "School guests must be able to send classroom commands through the API.");
assert.match(styles, /0\.6\.2 — dark mode contrast pass/, "The dark mode contrast pass must be present after the final light-surface rules.");
assert.match(styles, /html\[data-theme="dark"\] #app-view \.student-card/, "Student cards need an explicit dark surface and readable text.");
assert.match(styles, /html\[data-theme="dark"\] #detail-pane\.screen-mode/, "Student screen detail needs an explicit dark surface.");
assert.match(styles, /html\[data-theme="dark"\] \.workspace-dialog[\s\S]*background: #151f31/, "Workspace dialogs need an opaque dark surface.");
assert.match(script, /\$\("school-login-button"\)\.addEventListener\("click", openGuestLoginDialog\)/, "School login must open the school guest access flow.");
assert.doesNotMatch(html, /id="login-guest-button"/, "The duplicate school guest button should not be visible beside school login.");
assert.match(html, /id="guest-login-dialog"[^>]*class="command-dialog guest-login-dialog"/, "School login must retain the branded school access dialog.");
assert.match(html, /id="guest-login-submit"[^>]*class="primary"[^>]*>학교 로그인<\/button>/, "The school access dialog must use the same school login action label.");
assert.doesNotMatch(script, /\$\("school-login-button"\)\.disabled = !firebaseReady/, "School login must not depend on Firebase admin authentication readiness.");
assert.match(styles, /@media \(max-width: 820px\)/, "The mobile shell must keep a compact breakpoint.");
assert.match(html, /id="mobile-command-launcher"[^>]*aria-controls="bulk-actions"/, "Phones need one explicit entry point for student commands.");
assert.match(html, /id="mobile-command-close"/, "The mobile command sheet needs an explicit close action.");
assert.match(html, /id="mobile-command-scrim"/, "The mobile command sheet needs a tappable outside-dismiss surface.");
assert.match(script, /function syncMobileCommandDeckPlacement\(\)/, "The mobile command deck needs a stable placement guard.");
assert.match(script, /anchor\.before\(deck\)/, "The mobile command deck must remain in the stable class section on mobile browsers.");
assert.match(script, /mobileCommandMedia\.addEventListener\("change"/, "Crossing the mobile breakpoint must restore the command deck safely.");
assert.match(styles, /0\.6\.7 — mobile-first teacher console/, "The final mobile-first visual layer must remain documented and intentionally ordered.");
assert.match(styles, /#app-view\.mobile-command-open #bulk-actions/, "Mobile student commands must use a deliberate bottom-sheet state.");
assert.match(styles, /#app-view \.toolbar-browse-row \.filters[\s\S]*overflow-x: auto/, "Phone filters must scroll horizontally instead of collapsing into a dense button wall.");
assert.match(styles, /#app-view \.sidebar,[\s\S]*?#app-view \.section-view:not\(\[hidden\]\)[\s\S]*?transform: none !important;/, "Mobile fixed controls must not inherit a transformed shell coordinate system.");
assert.match(styles, /backdrop-filter: none;/, "The sticky mobile app bar must not capture the fixed bottom navigation coordinate system.");
assert.match(styles, /-webkit-backdrop-filter: none;/, "The prefixed mobile backdrop filter must also release the bottom navigation coordinate system.");
assert.match(styles, /\.command-dialog\s*\{[\s\S]*max-height:/, "Dialogs must stay inside the viewport.");
assert.match(styles, /\.class-select-menu\s*\{/, "The class picker menu must use the console visual system.");
assert.match(styles, /#settings-section > \.password-card,[\s\S]*#settings-section > \.update-card \{ grid-column: 1 \/ -1; grid-row: auto; \}/, "The password card must receive a full readable row on desktop.");
assert.match(styles, /\.password-card\.compact-setting\s*\{[\s\S]*grid-template-columns: minmax\(0, 1fr\) auto minmax\(190px, \.75fr\)/, "Password settings must retain a readable grid instead of collapsing text.");
assert.match(styles, /\.teacher-greeting\s*\{[\s\S]*text-wrap: balance;[\s\S]*word-break: keep-all;/, "Long greetings must wrap at readable word boundaries on phones.");
assert.match(styles, /writing-mode: horizontal-tb/, "Console labels must never fall into vertical writing mode.");
assert.match(styles, /#landing-view \*,[\s\S]*#login-view \*,[\s\S]*#app-view \*/, "Every console surface must explicitly retain horizontal text flow.");
assert.match(styles, /\.class-select-menu \{[\s\S]*z-index: 300;/, "The class picker must stay above the dashboard cards.");
assert.match(styles, /@media \(min-width: 821px\) and \(max-width: 1100px\)[\s\S]*\.teacher-heading[\s\S]*grid-template-columns: minmax\(0, 1fr\);/, "Narrow desktop headers must stack context instead of crushing the greeting.");
assert.match(styles, /@media \(max-width: 390px\)[\s\S]*\.brand > span:last-child \{ display: inline; \}/, "Compact phones must keep the Classroom wordmark visible without a vertical fallback.");
assert.match(html, /id="monitor-stage"/, "The home screen needs one in-place monitor stage.");
assert.match(html, /id="monitor-fullscreen-fab"/, "The monitor wall needs the floating fullscreen action.");
assert.doesNotMatch(html, /id="screen-wall-section"/, "The old duplicate screen wall must not take dashboard space.");
assert.match(script, /function renderMonitorGrid\(/, "Student monitors must render through the home grid.");
assert.match(script, /function monitorPageSize\(\)/, "Large classes need predictable monitor-wall pagination.");
assert.match(styles, /\.student-monitor-hinge\s*\{/, "Monitor tiles must expose a compact lower-right student label.");
assert.match(styles, /\.monitor-stage:fullscreen, \.monitor-stage\.fullscreen-mode/, "Monitor fullscreen needs a browser and in-page fallback.");
assert.match(script, /function monitorRefreshIntervalMs\(/, "Screen polling must choose a classroom-safe refresh cadence.");
assert.match(script, /screenShareIntervalMilliseconds: refreshInterval/, "The teacher console must pass the selected screen cadence to student apps.");
assert.match(script, /async function requestRemoteAssist\(/, "Teacher console must expose an explicit remote-assistance request step.");
assert.match(script, /async function toggleRemoteControl\(/, "Teacher console must require a second remote-control start step.");
assert.match(script, /safeRemoteKeyCode\(/, "Remote keyboard input must use an allow-list.");
assert.match(script, /remote-assist\/\$\{control\.remoteAssistSessionId\}\/input/, "Remote input must use the bound session endpoint.");
assert.match(cloudflareWorker, /RemoteAssistSessions/, "Worker must retain bounded remote-assistance session metadata.");
assert.match(cloudflareWorker, /REMOTE_ASSIST_MAX_INPUTS_PER_SECOND = 30/, "Worker must rate-limit remote input.");
assert.match(cloudflareWorker, /kind: "remoteAssistRequest"/, "Worker must deliver consent requests directly to the student socket.");
assert.match(cloudflareWorker, /kind: "remoteAssistEnd"/, "Worker must be able to end a remote session on the student PC.");
assert.match(script, /data-student-message/, "A teacher must be able to message one student without opening screen view.");
assert.match(script, /개인 메시지 보내기/, "A one-student command must be clearly identified as a personal message.");
assert.match(styles, /#class-section > \.class-metrics\s*\{[\s\S]*margin: 0 0 16px !important;[\s\S]*position: static !important;/, "Metric cards must stay in normal flow beneath the session strip.");
assert.match(styles, /#detail-pane\.screen-mode\s*\{[\s\S]*inset: 0 !important;[\s\S]*z-index: 1200 !important;/, "Student-screen detail must cover the console rather than overlap the header.");
assert.match(styles, /aspect-ratio: var\(--screen-aspect-ratio, 16 \/ 9\) !important;/, "The detailed student screen must keep its captured aspect ratio.");
assert.match(styles, /@media \(prefers-contrast: more\)/, "The Liquid Glass layer needs an opaque high-contrast fallback.");
assert.match(styles, /@supports \(\(backdrop-filter: blur\(12px\)\)/, "Glass materials need a feature-query fallback.");
assert.doesNotMatch(html, /class="settings-card legal-card"/, "Terms and privacy must live in the compact footer, not a standalone settings card.");
assert.match(html, /class="console-footer"/, "The compact terms/privacy footer must remain visible.");
assert.match(updater, /UPDATE_APPLYING/, "Student updates must report the immediate apply state.");
assert.match(updater, /Classroom-Student-x64\.zip/, "Student updates must use the student-only package when available.");
assert.match(updater, /classroom-api\.blossom0948\.cloud/, "Student updates must accept the Classroom download proxy.");
assert.match(setupForm, /Classroom-Student-x64\.zip/, "Student setup must prefer the student-only package.");
assert.match(setupForm, /classroom-api\.blossom0948\.cloud\/downloads\/student-package/, "Student setup must prefer the Classroom download proxy.");
assert.match(setupForm, /TimeSpan\.FromMinutes\(20\)/, "Student setup downloads need enough time for managed school networks.");
assert.match(setupForm, /attempt <= 3/, "Student setup downloads must retry transient failures.");
assert.doesNotMatch(updater, /MoveFileEx|DelayUntilReboot|next-windows-start|Windows를 다시 시작하면/, "Student updates must not wait for a Windows reboot.");
assert.match(helper, /CreateProcessAsUser/, "The update helper must be able to restart the student UI in the interactive session.");
assert.match(desktopProgram, /classroom-background/, "The student UI needs an explicit background startup mode.");
assert.match(desktopForm, /CloseReason\.UserClosing[\s\S]*?HideToTray\(\)/, "Closing the student window must hide it instead of ending the background connection.");
assert.match(desktopForm, /private void HideToTray\(\)/, "The student UI needs an explicit tray-hide path.");
assert.doesNotMatch(desktopForm, /CloseReason\.UserClosing[\s\S]{0,300}RequestApprovedExitAsync/, "The window close button must not trigger the administrator exit PIN flow.");
assert.match(desktopForm, /helpButton\.Click \+=/, "Students need a direct help-request action in their status window.");
assert.match(desktopForm, /statusProvider\.SetHelpRequested\(helpRequested\)/, "Student help requests must enter the existing status heartbeat path.");
assert.match(desktopForm, /SetHelpRequestAvailability\(false, clearRequest: true\)/, "Help requests must clear after a class session ends.");
assert.match(watchdog, /Arguments = "--classroom-background"/, "The watchdog must launch the student UI without showing its window.");
assert.match(watchdog, /catch \(Exception exception\)\n\s*\{[\s\S]*?StudentDesktopDiagnostics\.Log/, "The watchdog must survive unexpected process and profile errors.");
assert.match(desktopPipeClient, /catch \(Exception exception\)\n\s*\{[\s\S]*?retrying:/, "Student Desktop IPC must retry after any unexpected service error.");
assert.match(desktopPipeClient, /status collection failed; using a safe fallback/, "Student status collection errors must not terminate the desktop process.");
assert.match(agentWorker, /while \(!stoppingToken\.IsCancellationRequested\)/, "The Windows service must restart its connection loop if it returns unexpectedly.");
assert.match(desktopBridge, /IPC recovered from an unexpected error/, "The service pipe listener must keep accepting desktop reconnects after unexpected errors.");
assert.match(diagnostics, /student-desktop\.log/, "The background student process must leave a diagnosable local log.");
assert.match(desktopOptions, /StudentDesktopConfigurationStore\.TryLoad/, "The tray process must recover enrollment from machine-level configuration.");
assert.match(setupProgram, /TryStartExistingInstallation/, "Rerunning the installer must reuse an existing enrollment.");
assert.match(setupForm, /백그라운드에서 실행 중입니다/, "Successful enrollment must not leave a setup completion window open.");
assert.match(elevatedInstaller, /StudentDesktopConfigurationStore\.Save/, "The elevated installer must persist the tray configuration.");
assert.match(elevatedInstaller, /SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run/, "The elevated installer must register machine startup.");
assert.match(installScript, /desktop-config\.json/, "The manual installer must persist the same startup configuration.");

console.log("PASS Classroom web quality guards and responsive UI contracts");
