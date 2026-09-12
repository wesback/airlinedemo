import { createServer } from "node:http";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";
import { tmpdir } from "node:os";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../src/AirlineDemo.Api/wwwroot/", import.meta.url));
const types = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css" };
const backendPort = 4174;
const backendUrl = `http://127.0.0.1:${backendPort}`;
const stateDirectory = await mkdtemp(join(tmpdir(), "airlinedemo-browser-"));

await writeFile(
  join(stateDirectory, "workflow-state.json"),
  JSON.stringify({
    evidenceRequests: {
      "REQ-1": {
        requestId: "REQ-1",
        requestKey: "component-history",
        findingId: "FIND-1",
        basisId: "BASIS-1",
        requirementId: "REQ-COMPONENT-HISTORY",
        recipientRef: "mock-partner-inbox",
        templateVersion: "evidence-request-v1",
        message: "Please provide the approved component history record.",
        status: "delivered",
        createdAt: "2026-09-12T10:00:00Z"
      },
      "REQ-PENDING": {
        requestId: "REQ-PENDING",
        requestKey: "pending-request",
        findingId: "FIND-PENDING",
        basisId: "BASIS-PENDING",
        requirementId: "REQ-PENDING",
        recipientRef: "mock-partner-inbox",
        templateVersion: "evidence-request-v1",
        message: "This pending request must not be shown.",
        status: "pending",
        createdAt: "2026-09-12T10:01:00Z"
      },
      "REQ-UNSCOPED": {
        requestId: "REQ-UNSCOPED",
        requestKey: "real-recipient",
        findingId: "FIND-UNSCOPED",
        basisId: "BASIS-UNSCOPED",
        requirementId: "REQ-UNSCOPED",
        recipientRef: "real-partner-email",
        templateVersion: "evidence-request-v1",
        message: "This non-authoritative request must not be shown.",
        status: "delivered",
        createdAt: "2026-09-12T10:02:00Z"
      }
    },
    mockInbox: {
      "INBOX-1": {
        itemId: "INBOX-1",
        requestId: "REQ-1",
        requestKey: "component-history",
        context: {
          runId: "RUN-41",
          caseId: "CASE-41",
          airlineId: "AIRLINE-1",
          aircraftId: "AC-1",
          leaseId: "LEASE-1"
        },
        recipientRef: "mock-partner-inbox",
        templateVersion: "evidence-request-v1",
        message: "Please provide the approved component history record.",
        deliveredAt: "2026-09-12T10:00:00Z"
      },
      "INBOX-PENDING": {
        itemId: "INBOX-PENDING",
        requestId: "REQ-PENDING",
        requestKey: "pending-request",
        context: {
          runId: "RUN-41",
          caseId: "CASE-41",
          airlineId: "AIRLINE-1",
          aircraftId: "AC-1",
          leaseId: "LEASE-1"
        },
        recipientRef: "mock-partner-inbox",
        templateVersion: "evidence-request-v1",
        message: "This pending request must not be shown.",
        deliveredAt: "2026-09-12T10:01:00Z"
      },
      "INBOX-UNSCOPED": {
        itemId: "INBOX-UNSCOPED",
        requestId: "REQ-UNSCOPED",
        requestKey: "real-recipient",
        context: {
          runId: "RUN-41",
          caseId: "CASE-41",
          airlineId: "AIRLINE-1",
          aircraftId: "AC-1",
          leaseId: "LEASE-1"
        },
        recipientRef: "real-partner-email",
        templateVersion: "evidence-request-v1",
        message: "This non-authoritative request must not be shown.",
        deliveredAt: "2026-09-12T10:02:00Z"
      }
    }
  })
);

const backend = spawn(
  "dotnet",
  ["run", "--project", "src/AirlineDemo.Api/AirlineDemo.Api.csproj", "--", "--serve-browser"],
  {
    cwd: fileURLToPath(new URL("../../", import.meta.url)),
    env: {
      ...process.env,
      AIRLINEDEMO_STATE_DIRECTORY: stateDirectory,
      AIRLINEDEMO_URLS: backendUrl
    },
    stdio: "ignore"
  }
);

async function waitForBackend() {
  for (let attempt = 0; attempt < 200; attempt += 1) {
    try {
      const response = await fetch(`${backendUrl}/api/mock-inbox`, {
        headers: { Authorization: "Bearer run=RUN-41;airline=AIRLINE-1;aircraft=AC-1;lease=LEASE-1" }
      });
      if (response.status === 200) return;
    } catch {
      // The backend may still be compiling or starting.
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error("The browser API did not start.");
}

await waitForBackend();

async function proxyApi(request, response) {
  const body = request.method === "GET" || request.method === "HEAD"
    ? undefined
    : await new Promise((resolve, reject) => {
        const chunks = [];
        request.on("data", chunk => chunks.push(chunk));
        request.on("end", () => resolve(Buffer.concat(chunks)));
        request.on("error", reject);
      });
  const headers = {};
  for (const name of ["accept", "authorization", "content-type", "if-match"]) {
    if (request.headers[name]) headers[name] = request.headers[name];
  }
  try {
    const backendResponse = await fetch(`${backendUrl}${request.url}`, {
      method: request.method,
      headers,
      body
    });
    response.writeHead(backendResponse.status, {
      "Content-Type": backendResponse.headers.get("content-type") || "application/json"
    });
    response.end(Buffer.from(await backendResponse.arrayBuffer()));
  } catch {
    response.writeHead(502, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ safeCode: "API_UNAVAILABLE", correlationId: "BROWSER-HARNESS" }));
  }
}

const server = createServer(async (request, response) => {
  if (request.url?.startsWith("/api/")) {
    proxyApi(request, response);
    return;
  }
  const requested = request.url === "/" ? "/index.html" : request.url.split("?")[0];
  const file = normalize(join(root, requested));
  if (!file.startsWith(root)) {
    response.writeHead(404);
    response.end();
    return;
  }
  try {
    const content = await readFile(file);
    response.writeHead(200, { "Content-Type": types[extname(file)] || "application/octet-stream" });
    response.end(content);
  } catch {
    response.writeHead(404);
    response.end();
  }
}).listen(4173, "127.0.0.1");

async function shutdown() {
  server.close();
  backend.kill("SIGTERM");
  await rm(stateDirectory, { recursive: true, force: true });
}

process.once("SIGINT", shutdown);
process.once("SIGTERM", shutdown);
process.once("exit", () => backend.kill("SIGTERM"));
