import assert from "node:assert/strict";
import worker from "../cloudflare/worker.js";

const originalFetch = globalThis.fetch;
globalThis.fetch = async (input, init) => {
  const request = input instanceof Request ? input : new Request(input, init);
  assert.equal(
    request.url,
    "https://github.com/blossom0948/classroom/releases/latest/download/Classroom-Student-x64.zip"
  );
  assert.equal(request.method, "GET");
  assert.equal(request.headers.get("Range"), "bytes=0-2");
  assert.equal(request.headers.get("User-Agent"), "Blossom-Classroom-Download-Proxy/1.0");
  return new Response(new Uint8Array([1, 2, 3]), {
    status: 206,
    headers: {
      "Content-Type": "application/zip",
      "Content-Length": "3",
      "Content-Range": "bytes 0-2/99",
      "Accept-Ranges": "bytes",
      ETag: "student-package"
    }
  });
};

try {
  const download = await worker.fetch(
    new Request("https://classroom-api.blossom0948.cloud/downloads/student-package", {
      headers: { Range: "bytes=0-2" }
    }),
    {}
  );
  assert.equal(download.status, 206);
  assert.equal(download.headers.get("Content-Disposition"), "attachment; filename=\"Classroom-Student-x64.zip\"");
  assert.equal(download.headers.get("Content-Range"), "bytes 0-2/99");
  assert.equal(download.headers.get("Accept-Ranges"), "bytes");
  assert.deepEqual([...new Uint8Array(await download.arrayBuffer())], [1, 2, 3]);

  const rejected = await worker.fetch(
    new Request("https://classroom-api.blossom0948.cloud/downloads/student-package", { method: "POST" }),
    {}
  );
  assert.equal(rejected.status, 405);
  assert.equal(rejected.headers.get("Allow"), "GET, HEAD");
} finally {
  globalThis.fetch = originalFetch;
}

console.log("PASS Classroom Worker student download proxy");
