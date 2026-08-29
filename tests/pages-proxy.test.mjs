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
  forwardedHost: request.headers.get("X-Forwarded-Host")
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
} finally {
  globalThis.fetch = originalFetch;
}

console.log("PASS Cloudflare Pages static fallback and backend proxy routing");
