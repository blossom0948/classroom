// Bump this whenever the app shell changes so an already-installed school
// browser drops the previous Firebase configuration and JavaScript bundle.
const CACHE_NAME = "classroom-console-v24";
const APP_SHELL = ["/", "/index.html", "/styles.css?v=0.5.32", "/app.js?v=0.5.32", "/config.js", "/firebase-auth.js", "/manifest.webmanifest", "/version.json", "/icons/classroom.svg"];
// URL.pathname never contains the query string. Keeping query-bearing entries
// in this set made old app.js/styles.css responses cache-first indefinitely.
const NETWORK_FIRST_PATHS = new Set(["/", "/index.html", "/styles.css", "/app.js", "/config.js", "/firebase-auth.js", "/manifest.webmanifest", "/version.json", "/icons/classroom.svg"]);

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE_NAME).then((cache) => cache.addAll(APP_SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key)))).then(() => self.clients.claim()));
});

self.addEventListener("message", (event) => {
  if (event.data?.type === "SKIP_WAITING") self.skipWaiting();
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== "GET" || url.origin !== self.location.origin || url.pathname.startsWith("/api/") || url.pathname.startsWith("/auth/") || url.pathname.startsWith("/health")) return;
  if (NETWORK_FIRST_PATHS.has(url.pathname)) {
    event.respondWith((async () => {
      const cached = await caches.match(request);
      try {
        const response = await fetch(request, { cache: "no-store" });
        if (response.ok) {
          const cache = await caches.open(CACHE_NAME);
          await cache.put(request, response.clone());
        }
        return response;
      } catch (_) {
        return cached || caches.match("/index.html");
      }
    })());
    return;
  }
  event.respondWith(caches.match(request).then((cached) => {
    const network = fetch(request).then((response) => {
      if (response.ok) caches.open(CACHE_NAME).then((cache) => cache.put(request, response.clone()));
      return response;
    }).catch(() => cached || caches.match("/index.html"));
    return cached || network;
  }));
});
