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
  "detail-status-view-button",
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
assert.match(html, /id="close-console-button"/, "The installed shell keeps a compatibility close-action hook.");
assert.match(script, /\$\("close-console-button"\)\.hidden = true;/, "The redundant in-page close icon must not crowd responsive console headers.");
assert.match(html, /id="console-close-dialog"/, "Closing the account console needs a branded confirmation dialog.");
assert.match(html, /id="confirm-dialog"/, "Destructive classroom actions need branded confirmations.");
assert.match(html, /id="class-select-menu"/, "The primary class picker needs a branded listbox.");
assert.match(script, /localStorage.*TEACHER_TOKEN_KEY|TEACHER_TOKEN_KEY[\s\S]*localStorage/, "Account tokens must survive closing the console.");
assert.match(script, /cookieSessionEnabled/, "The console must retain the optional cookie-session compatibility path.");
assert.match(script, /credentials: cookieSessionEnabled \? "include"/, "Cookie sessions must send same-origin credentials when enabled.");
assert.match(config, /apiOrigin:[\s\S]*classroom-api\.blossom0948\.cloud/, "The public console must use the verified API origin for login compatibility.");
assert.match(config, /cookieSession:\s*false/, "The public console must keep the verified bearer session path enabled.");
assert.doesNotMatch(script, /\bconfirm\(/, "Native browser confirmation prompts should not be used in the console.");
assert.match(styles, /@media \(max-width: 820px\)/, "The mobile shell must keep a compact breakpoint.");
assert.match(styles, /\.command-dialog\s*\{[\s\S]*max-height:/, "Dialogs must stay inside the viewport.");
assert.match(styles, /\.class-select-menu\s*\{/, "The class picker menu must use the console visual system.");
assert.match(styles, /#settings-section > \.password-card\s*\{[\s\S]*grid-column: 1;[\s\S]*grid-row: 3;/, "Desktop settings must keep password controls in the compact left column.");
assert.match(styles, /\.teacher-greeting\s*\{[\s\S]*text-wrap: balance;[\s\S]*word-break: keep-all;/, "Long greetings must wrap at readable word boundaries on phones.");
assert.match(styles, /writing-mode: horizontal-tb/, "Console labels must never fall into vertical writing mode.");
assert.match(styles, /#landing-view \*,[\s\S]*#login-view \*,[\s\S]*#app-view \*/, "Every console surface must explicitly retain horizontal text flow.");
assert.match(styles, /\.class-select-menu \{[\s\S]*z-index: 300;/, "The class picker must stay above the dashboard cards.");
assert.match(styles, /@media \(min-width: 821px\) and \(max-width: 1100px\)[\s\S]*\.teacher-heading[\s\S]*grid-template-columns: minmax\(0, 1fr\);/, "Narrow desktop headers must stack context instead of crushing the greeting.");
assert.match(styles, /@media \(max-width: 390px\)[\s\S]*\.brand > span:last-child \{ display: inline; \}/, "Compact phones must keep the Classroom wordmark visible without a vertical fallback.");
assert.match(updater, /UPDATE_APPLYING/, "Student updates must report the immediate apply state.");
assert.doesNotMatch(updater, /MoveFileEx|DelayUntilReboot|next-windows-start|Windows를 다시 시작하면/, "Student updates must not wait for a Windows reboot.");
assert.match(helper, /CreateProcessAsUser/, "The update helper must be able to restart the student UI in the interactive session.");

console.log("PASS Classroom web quality guards and responsive UI contracts");
