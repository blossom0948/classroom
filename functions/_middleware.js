const PROXY_PATHS = ["/auth/", "/api/", "/health", "/ws/student"];

function shouldProxy(pathname) {
  return PROXY_PATHS.some((prefix) => pathname === prefix.replace(/\/$/, "") || pathname.startsWith(prefix));
}

export async function onRequest(context) {
  const incomingUrl = new URL(context.request.url);
  if (!shouldProxy(incomingUrl.pathname)) {
    return context.next();
  }

  const configuredOrigin = String(context.env.CLASSROOM_BACKEND_ORIGIN || "").trim();
  let backendOrigin;
  try {
    backendOrigin = new URL(configuredOrigin);
  } catch {
    return Response.json(
      { code: "BACKEND_NOT_CONFIGURED", message: "Classroom backend origin is not configured." },
      { status: 503 }
    );
  }

  if (backendOrigin.protocol !== "https:" || backendOrigin.host === incomingUrl.host) {
    return Response.json(
      { code: "BACKEND_CONFIGURATION_INVALID", message: "Classroom backend origin must be a separate HTTPS origin." },
      { status: 503 }
    );
  }

  backendOrigin.pathname = `${backendOrigin.pathname.replace(/\/$/, "")}${incomingUrl.pathname}`;
  backendOrigin.search = incomingUrl.search;
  const headers = new Headers(context.request.headers);
  headers.set("X-Forwarded-Proto", "https");
  headers.set("X-Forwarded-Host", incomingUrl.host);
  headers.set("X-Classroom-Proxy", "cloudflare-pages");

  try {
    const upstream = await fetch(new Request(backendOrigin, {
      method: context.request.method,
      headers,
      body: context.request.body,
      redirect: "manual"
    }));
    // Re-wrap the response so the Pages origin deliberately passes the
    // backend's HttpOnly session cookie through to the browser.  `Headers`
    // preserves Set-Cookie while also preventing authentication responses
    // from being cached by an intermediary.
    const responseHeaders = new Headers(upstream.headers);
    responseHeaders.set("Cache-Control", "no-store");
    return new Response(upstream.body, {
      status: upstream.status,
      statusText: upstream.statusText,
      headers: responseHeaders
    });
  } catch {
    return Response.json(
      { code: "BACKEND_UNAVAILABLE", message: "Classroom backend is temporarily unavailable." },
      { status: 502 }
    );
  }
}
