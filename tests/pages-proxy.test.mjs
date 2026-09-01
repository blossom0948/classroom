import assert from "node:assert/strict";
import { onRequest } from "../functions/_middleware.js";

const staticResponse = await onRequest({
  request: new Request("https://classroom-2en.pages.dev/"),
  env: {},
  next: () => new Response("STATIC", { status: 200 })
});
assert.equal(await staticResponse.text(), "STATIC");

const missingBackend = await onRequest({
  request: new Request("https://classroom-2en.pages.dev/health"),
  env: {},
  next: () => new Response("unexpected")
});
assert.equal(missingBackend.status, 503);
assert.equal((await missingBackend.json()).code, "BACKEND_NOT_CONFIGURED");

const originalFetch = globalThis.fetch;
globalThis.fetch = async (request) => Response.json({
  url: request.url,
  forwardedProto: request.headers.get("X-Forwarded-Proto"),
  forwardedHost: request.headers.get("X-Forwarded-Host"),
  classroomProxy: request.headers.get("X-Classroom-Proxy")
});
try {
  const proxied = await onRequest({
    request: new Request("https://classroom-2en.pages.dev/api/classes?active=true", {
      headers: { Authorization: "Bearer test-token" }
    }),
    env: { CLASSROOM_BACKEND_ORIGIN: "https://classroom-origin.example" },
    next: () => new Response("unexpected")
  });
  const payload = await proxied.json();
  assert.equal(payload.url, "https://classroom-origin.example/api/classes?active=true");
  assert.equal(payload.forwardedProto, "https");
  assert.equal(payload.forwardedHost, "classroom-2en.pages.dev");
  assert.equal(payload.classroomProxy, "cloudflare-pages");
  assert.equal(proxied.headers.get("Cache-Control"), "no-store");
} finally {
  globalThis.fetch = originalFetch;
}

globalThis.fetch = async () => new Response("signed in", {
  status: 200,
  headers: [
    ["Set-Cookie", "__Host-classroom-session=test; Path=/; Secure; HttpOnly"],
    ["Set-Cookie", "classroom-preference=light; Path=/; Secure"]
  ]
});
try {
  const login = await onRequest({
    request: new Request("https://classroom-2en.pages.dev/auth/login", { method: "POST" }),
    env: { CLASSROOM_BACKEND_ORIGIN: "https://classroom-origin.example" },
    next: () => new Response("unexpected")
  });
  const cookies = typeof login.headers.getSetCookie === "function"
    ? login.headers.getSetCookie()
    : [login.headers.get("Set-Cookie")];
  assert.ok(cookies.includes("__Host-classroom-session=test; Path=/; Secure; HttpOnly"));
  assert.ok(cookies.includes("classroom-preference=light; Path=/; Secure"));
} finally {
  globalThis.fetch = originalFetch;
}

console.log("PASS Cloudflare Pages static fallback and backend proxy routing");
