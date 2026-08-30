// Bump this whenever the app shell changes so an already-installed school
// browser drops the previous Firebase configuration and JavaScript bundle.
const CACHE_NAME = "classroom-console-v7";
const APP_SHELL = ["/", "/index.html", "/styles.css", "/app.js", "/config.js", "/firebase-auth.js", "/manifest.webmanifest", "/icons/classroom.svg"];
const NETWORK_FIRST_PATHS = new Set(APP_SHELL);

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE_NAME).then((cache) => cache.addAll(APP_SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key)))).then(() => self.clients.claim()));
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== "GET" || url.origin !== self.location.origin || url.pathname.startsWith("/api/") || url.pathname.startsWith("/auth/") || url.pathname.startsWith("/health")) return;
  if (NETWORK_FIRST_PATHS.has(url.pathname)) {
    event.respondWith(caches.match(request).then((cached) => fetch(request, { cache: "no-store" })
      .then((response) => {
        if (response.ok) caches.open(CACHE_NAME).then((cache) => cache.put(request, response.clone()));
        return response;
      })
      .catch(() => cached || caches.match("/index.html")));
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
