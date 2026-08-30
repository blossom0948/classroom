import { copyFile, cp, mkdir, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, "..", "..");
const sourceRoot = join(repositoryRoot, "src", "Classroom.Server", "wwwroot");
const outputRoot = join(repositoryRoot, "dist");

await rm(outputRoot, { recursive: true, force: true });
await mkdir(outputRoot, { recursive: true });

for (const fileName of ["index.html", "styles.css", "app.js", "config.js", "firebase-auth.js", "manifest.webmanifest", "sw.js"]) {
  await copyFile(join(sourceRoot, fileName), join(outputRoot, fileName));
}
await cp(join(sourceRoot, "icons"), join(outputRoot, "icons"), { recursive: true });

await writeFile(join(outputRoot, "_headers"), `/*
  Cache-Control: no-cache
  Content-Security-Policy: default-src 'self'; script-src 'self' https://www.gstatic.com; style-src 'self'; img-src 'self' data: https://*.googleusercontent.com; connect-src 'self' https://identitytoolkit.googleapis.com https://securetoken.googleapis.com https://firebaseinstallations.googleapis.com https://www.googleapis.com https://*.firebaseapp.com https://accounts.google.com; frame-src https://*.firebaseapp.com https://accounts.google.com; worker-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'
  Referrer-Policy: no-referrer
  X-Content-Type-Options: nosniff
  X-Frame-Options: DENY
  Permissions-Policy: camera=(), microphone=(), geolocation=()
`);
await writeFile(join(outputRoot, "_routes.json"), `${JSON.stringify({
  version: 1,
  include: ["/auth/*", "/api/*", "/health*", "/ws/student*"],
  exclude: []
}, null, 2)}\n`);

console.log(`Classroom Pages output created at ${outputRoot}`);
