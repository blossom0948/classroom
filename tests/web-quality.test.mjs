import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const [html, script, styles, config, updater, helper] = await Promise.all([
  readFile(new URL("../src/Classroom.Server/wwwroot/index.html", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Server/wwwroot/app.js", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Server/wwwroot/styles.css", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Server/wwwroot/config.js", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Service/StudentUpdateWorker.cs", import.meta.url), "utf8"),
  readFile(new URL("../src/Classroom.Student.Service/StudentUpdateHelper.cs", import.meta.url), "utf8")
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
assert.match(config, /apiOrigin:\s*window\.location\.origin/, "The public console must use its Pages origin proxy for secure sessions.");
assert.match(config, /cookieSession:\s*!isLocalClassroom/, "Production must opt into the secure cookie session while local development retains its fallback.");
assert.match(script, /async function consumePendingFirebaseRedirect\(\)/, "Google redirect credentials must be consumed before session restoration.");
assert.match(script, /if \(await consumePendingFirebaseRedirect\(\)\) return;/, "Google redirect completion must not be sent back to landing by an early session check.");
assert.doesNotMatch(script, /\bconfirm\(/, "Native browser confirmation prompts should not be used in the console.");
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
assert.doesNotMatch(html, /class="settings-card legal-card"/, "Terms and privacy must live in the compact footer, not a standalone settings card.");
assert.match(html, /class="console-footer"/, "The compact terms/privacy footer must remain visible.");
assert.match(updater, /UPDATE_APPLYING/, "Student updates must report the immediate apply state.");
assert.doesNotMatch(updater, /MoveFileEx|DelayUntilReboot|next-windows-start|Windows를 다시 시작하면/, "Student updates must not wait for a Windows reboot.");
assert.match(helper, /CreateProcessAsUser/, "The update helper must be able to restart the student UI in the interactive session.");

console.log("PASS Classroom web quality guards and responsive UI contracts");
