const PROXY_PATHS = ["/auth/", "/api/", "/health", "/ws/student"];

function shouldProxy(pathname) {
  return PROXY_PATHS.some((prefix) => pathname === prefix.replace(/\/$/, "") || pathname.startsWith(prefix));
}

function readSetCookies(headers) {
  if (typeof headers.getSetCookie === "function") return headers.getSetCookie();
  if (typeof headers.getAll === "function") return headers.getAll("Set-Cookie");
  const value = headers.get("Set-Cookie");
  return value ? [value] : [];
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
    // Preserve Set-Cookie explicitly. Cloudflare's runtime keeps it as a
    // special multi-value header, so a generic Headers copy is not enough
    // when this optional same-site cookie transport is enabled.
    const response = new Response(upstream.body, upstream);
    const cookies = readSetCookies(upstream.headers);
    if (cookies.length) {
      response.headers.delete("Set-Cookie");
      for (const cookie of cookies) response.headers.append("Set-Cookie", cookie);
    }
    response.headers.set("Cache-Control", "no-store");
    return response;
  } catch {
    return Response.json(
      { code: "BACKEND_UNAVAILABLE", message: "Classroom backend is temporarily unavailable." },
      { status: 502 }
    );
  }
}
