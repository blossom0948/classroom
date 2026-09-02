import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const [html, script, styles, config, updater, helper, desktopProgram, desktopForm, watchdog, desktopOptions, setupProgram, setupForm, elevatedInstaller, installScript, buildPagesScript, cloudflareWorker] = await Promise.all([
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
assert.match(html, /id="admin-auth-panel" hidden/, "Email, password, and Google alternatives must start behind admin login.");
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
assert.match(script, /\$\("school-login-button"\)\.addEventListener\("click", openGuestLoginDialog\)/, "School login must open the school guest access flow.");
assert.doesNotMatch(html, /id="login-guest-button"/, "The duplicate school guest button should not be visible beside school login.");
assert.match(html, /id="guest-login-dialog"[^>]*class="command-dialog guest-login-dialog"/, "School login must retain the branded school access dialog.");
assert.match(html, /id="guest-login-submit"[^>]*class="primary"[^>]*>학교 로그인<\/button>/, "The school access dialog must use the same school login action label.");
assert.doesNotMatch(script, /\$\("school-login-button"\)\.disabled = !firebaseReady/, "School login must not depend on Firebase admin authentication readiness.");
assert.match(styles, /@media \(max-width: 820px\)/, "The mobile shell must keep a compact breakpoint.");
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
assert.match(watchdog, /Arguments = "--classroom-background"/, "The watchdog must launch the student UI without showing its window.");
assert.match(desktopOptions, /StudentDesktopConfigurationStore\.TryLoad/, "The tray process must recover enrollment from machine-level configuration.");
assert.match(setupProgram, /TryStartExistingInstallation/, "Rerunning the installer must reuse an existing enrollment.");
assert.match(setupForm, /백그라운드에서 실행 중입니다/, "Successful enrollment must not leave a setup completion window open.");
assert.match(elevatedInstaller, /StudentDesktopConfigurationStore\.Save/, "The elevated installer must persist the tray configuration.");
assert.match(elevatedInstaller, /SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run/, "The elevated installer must register machine startup.");
assert.match(installScript, /desktop-config\.json/, "The manual installer must persist the same startup configuration.");

console.log("PASS Classroom web quality guards and responsive UI contracts");
