const { test, expect } = require("@playwright/test");
const http = require("http");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "../../src/AirlineDemo.Api/wwwroot");
const authorizedReviewer = "Bearer run=RUN-0001;airline=AIRLINE-0001;aircraft=MOCK-AC-001;lease=LEASE-0001;subject=reviewer-001";
const outOfScopeReviewer = "Bearer run=RUN-0001;airline=AIRLINE-999;aircraft=MOCK-AC-001;lease=LEASE-0001;subject=reviewer-999";
let server;
let origin;

const finding = {
  findingId: "FINDING-0001",
  requirementId: "REQ-SERIAL",
  basisId: "BASIS-0001",
  assessment: "ambiguous",
  reasonCode: "serial_number_ambiguous",
  explanation: "The current record contains an ambiguous serial number.",
  evidenceRefs: [{ documentId: "DOC-0001", version: 2, page: 3 }]
};
const secondFinding = {
  findingId: "FINDING-0002",
  requirementId: "REQ-MAINTENANCE",
  basisId: "BASIS-0001",
  assessment: "blocked",
  reasonCode: "maintenance_record_missing",
  explanation: "The maintenance record is still being reconciled.",
  evidenceRefs: []
};

function currentSummary(overrides = {}) {
  return {
    caseId: "CASE-RUN-0001",
    runId: "RUN-0001",
    airlineId: "AIRLINE-0001",
    aircraftId: "MOCK-AC-001",
    leaseId: "LEASE-0001",
    caseRevision: 7,
    status: "awaiting_review",
    packageProcessing: [],
    investigation: {
      basisId: "BASIS-0001",
      status: "complete",
      findings: [finding, secondFinding]
    },
    openReviewTasks: [
      {
        taskId: "TASK-0001",
        findingId: "FINDING-0001",
        basisId: "BASIS-0001",
        reasonCode: "serial_number_ambiguous",
        status: "open",
        permittedDecisions: ["accept_evidence", "needs_evidence"]
      },
      {
        taskId: "TASK-0002",
        findingId: "FINDING-0002",
        basisId: "BASIS-0001",
        reasonCode: "maintenance_record_missing",
        status: "open",
        permittedDecisions: ["needs_evidence"]
      }
    ],
    activeEvidenceRequests: [{
      requestId: "REQ-0001",
      requestKey: "serial-record",
      status: "pending"
    }],
    ...overrides
  };
}

function json(response, status, body) {
  response.fulfill({
    status,
    contentType: "application/json",
    body: JSON.stringify(body)
  });
}

test.beforeAll(async () => {
  server = http.createServer((request, response) => {
    const pathname = new URL(request.url, "http://localhost").pathname;
    const file = pathname === "/" ? "index.html" : pathname.slice(1);
    const filePath = path.join(root, file);
    if (!filePath.startsWith(root) || !fs.existsSync(filePath)) {
      response.writeHead(404);
      response.end();
      return;
    }
    const contentType = filePath.endsWith(".css")
      ? "text/css"
      : filePath.endsWith(".js") ? "text/javascript" : "text/html";
    response.writeHead(200, { "Content-Type": contentType });
    fs.createReadStream(filePath).pipe(response);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});

test.afterAll(async () => new Promise((resolve) => server.close(resolve)));

async function stubCase(page, caseSummary = currentSummary(), options = {}) {
  let latestSummary = typeof caseSummary === "function" ? null : caseSummary;
  await page.route("**/api/cases/CASE-RUN-0001", (route) => {
    const authorization = route.request().headers().authorization;
    if (authorization !== authorizedReviewer) {
      return json(route, authorization ? 404 : 401, {
        safeCode: authorization ? "CASE_NOT_FOUND" : "AUTHENTICATION_REQUIRED",
        correlationId: authorization ? "CORR-SCOPE" : "CORR-AUTH"
      });
    }
    const body = latestSummary = typeof caseSummary === "function" ? caseSummary() : caseSummary;
    return json(route, 200, body);
  });
  await page.route("**/api/cases/CASE-RUN-0001/review-tasks/*", (route) => {
    if (options.onTaskRead) options.onTaskRead();
    const authorization = route.request().headers().authorization;
    if (authorization !== authorizedReviewer) {
      return json(route, authorization ? 404 : 401, {
        safeCode: authorization ? "CASE_NOT_FOUND" : "AUTHENTICATION_REQUIRED",
        correlationId: authorization ? "CORR-SCOPE" : "CORR-AUTH"
      });
    }
    const taskId = new URL(route.request().url()).pathname.split("/").pop();
    const body = latestSummary || currentSummary();
    const task = (body.openReviewTasks || []).find((candidate) => candidate.taskId === taskId);
    return task
      ? json(route, 200, task)
      : json(route, 404, { safeCode: "CASE_NOT_FOUND", correlationId: "CORR-TASK" });
  });
  await page.route("**/api/cases/CASE-RUN-0001/evidence/**", (route) => {
    const authorization = route.request().headers().authorization;
    if (authorization !== authorizedReviewer) {
      return json(route, authorization ? 404 : 401, {
        safeCode: authorization ? "EVIDENCE_NOT_FOUND" : "AUTHENTICATION_REQUIRED",
        correlationId: authorization ? "CORR-EVIDENCE" : "CORR-AUTH"
      });
    }
    return json(route, 200, options.preview || {
      documentId: "DOC-0001",
      version: 2,
      page: 3,
      mediaType: "text/plain",
      textExcerpt: "Serial number recorded on the current return certificate."
    });
  });
}

async function authenticate(page, authorization) {
  await page.addInitScript((token) => {
    window.__AIRLINEDEMO_AUTHORIZATION__ = token;
  }, authorization);
}

test("review access requires authentication and case scope before an in-scope task can be opened", async ({ page }) => {
  await page.addInitScript(() => {
    window.__AIRLINEDEMO_AUTHORIZATION__ = window.localStorage.getItem("airlinedemo-auth");
  });
  let taskReads = 0;
  let reviewRequests = 0;
  await page.route("**/api/cases/CASE-RUN-0001/reviews", async (route) => {
    reviewRequests++;
    const authorization = route.request().headers().authorization;
    if (authorization !== authorizedReviewer) {
      return json(route, authorization ? 403 : 401, {
        safeCode: authorization ? "ACTION_FORBIDDEN" : "AUTHENTICATION_REQUIRED",
        correlationId: authorization ? "CORR-REVIEW-SCOPE" : "CORR-REVIEW-AUTH"
      });
    }
    return json(route, 200, { reviewId: "REVIEW-AUTH-0001" });
  });
  await stubCase(page, currentSummary(), { onTaskRead: () => taskReads++ });

  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await expect(page.getByText("AUTHENTICATION_REQUIRED - correlation CORR-AUTH")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Review TASK-0001" })).toHaveCount(0);
  await expect(page.getByText("Serial number recorded on the current return certificate.")).toHaveCount(0);
  expect(taskReads).toBe(0);
  expect(reviewRequests).toBe(0);

  await page.evaluate((token) => window.localStorage.setItem("airlinedemo-auth", token), outOfScopeReviewer);
  await page.reload();
  await expect(page.getByText("CASE_NOT_FOUND - correlation CORR-SCOPE")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Review TASK-0001" })).toHaveCount(0);
  expect(taskReads).toBe(0);
  expect(reviewRequests).toBe(0);

  await page.evaluate((token) => window.localStorage.setItem("airlinedemo-auth", token), authorizedReviewer);
  await page.reload();
  await expect(page.getByRole("heading", { name: "Review TASK-0001" })).toBeVisible();
  expect(taskReads).toBe(1);
  await page.getByLabel("Accept evidence").check();
  await page.getByLabel("Reason (required)").fill("Authenticated reviewer confirmed the in-scope basis.");
  await page.getByRole("button", { name: "Submit review decision" }).click();
  await expect.poll(() => reviewRequests).toBe(1);
});

test("authorised reviewer opens an in-scope task and submits only a server-permitted decision", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  await stubCase(page);
  let reviewRequest;
  await page.route("**/api/cases/CASE-RUN-0001/reviews", async (route) => {
    expect(route.request().headers().authorization).toBe(authorizedReviewer);
    reviewRequest = route.request();
    await json(route, 200, { reviewId: "REVIEW-0001" });
  });
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await expect(page.getByRole("heading", { name: "Review TASK-0001" })).toBeVisible();
  await expect(page.getByText("Serial number recorded on the current return certificate.")).toBeVisible();
  await expect(page.getByLabel("Accept evidence")).toBeVisible();
  await expect(page.getByLabel("Needs more evidence")).toBeVisible();
  await expect(page.getByLabel("Dismiss finding")).toHaveCount(0);
  await page.getByLabel("Accept evidence").check();
  await page.getByLabel("Reason (required)").fill("Reviewed the current cited basis.");
  await page.getByRole("button", { name: "Submit review decision" }).click();
  await expect.poll(() => reviewRequest && reviewRequest.postDataJSON()).toMatchObject({
    findingId: "FINDING-0001",
    basisId: "BASIS-0001",
    decision: "accept_evidence",
    reason: "Reviewed the current cited basis."
  });
  expect(reviewRequest.headers()["if-match"]).toBe('"7"');
});

test("412 reloads the current basis and requires explicit confirmation before resubmission", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  let requests = 0;
  let caseReads = 0;
  const refreshedFinding = { ...finding, basisId: "BASIS-0002" };
  const refreshedSummary = currentSummary({
    caseRevision: 8,
    investigation: {
      basisId: "BASIS-0002",
      status: "complete",
      findings: [refreshedFinding, secondFinding]
    },
    openReviewTasks: [{
      taskId: "TASK-0003",
      findingId: "FINDING-0001",
      basisId: "BASIS-0002",
      reasonCode: "serial_number_ambiguous",
      status: "open",
      permittedDecisions: ["accept_evidence", "needs_evidence"]
    }]
  });
  await stubCase(page, () => ++caseReads === 1 ? currentSummary() : refreshedSummary);
  await page.route("**/api/cases/CASE-RUN-0001/reviews", async (route) => {
    requests++;
    if (requests === 1) {
      await json(route, 412, { safeCode: "STALE_PRECONDITION", correlationId: "CORR-STALE" });
    } else {
      await json(route, 200, { reviewId: "REVIEW-0002" });
    }
  });
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await page.getByLabel("Accept evidence").check();
  await page.getByLabel("Reason (required)").fill("The first basis was reviewed.");
  await page.getByRole("button", { name: "Submit review decision" }).click();
  await expect(page.getByText("This evidence basis changed")).toBeVisible();
  await expect(page.getByText("The previous review was not submitted.")).toBeVisible();
  await expect(page.locator("#basis-confirmed")).toBeVisible();
  await expect(page.locator("#case-revision")).toHaveText("8");
  await expect(page.locator("#basis-id")).toHaveText("BASIS-0002");
  await expect(page.getByRole("button", { name: "Submit review decision" })).toBeDisabled();
  expect(requests).toBe(1);
  await page.locator("#basis-confirmed").check();
  await page.getByLabel("Accept evidence").check();
  await page.getByLabel("Reason (required)").fill("Confirmed the newly loaded basis.");
  await page.getByRole("button", { name: "Submit review decision" }).click();
  await expect.poll(() => requests).toBe(2);
});

test("duplicate activation sends one request and failed responses expose only safe correlation data", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  let resolveReview;
  let requestCount = 0;
  await stubCase(page);
  await page.route("**/api/cases/CASE-RUN-0001/reviews", async (route) => {
    requestCount++;
    await new Promise((resolve) => { resolveReview = resolve; });
    await json(route, 403, {
      safeCode: "ACTION_FORBIDDEN",
      correlationId: "CORR-SAFE",
      message: "protected task and evidence content must not appear"
    });
  });
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await page.getByLabel("Accept evidence").check();
  await page.getByLabel("Reason (required)").fill("A deliberate review reason.");
  const submit = page.getByRole("button", { name: "Submit review decision" });
  await Promise.all([submit.click(), submit.click()]);
  expect(requestCount).toBe(1);
  resolveReview();
  await expect(page.getByText("ACTION_FORBIDDEN - correlation CORR-SAFE")).toBeVisible();
  await expect(page.locator("#global-error")).not.toContainText("TASK-0001");
  await expect(page.locator("#global-error")).not.toContainText("Serial number");
  await expect(page.locator("#global-error")).not.toContainText("protected task");
});

test("awaiting review keeps concurrent work visible and never presents invalidated acceptance as current", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  await stubCase(page, currentSummary({
    caseRevision: 11,
    status: "awaiting_review",
    reconciliationItems: [{
      reconciliationId: "REC-1",
      status: "open",
      reason: "acceptance_invalidated"
    }]
  }));
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await expect(page.getByText("Awaiting internal review")).toBeVisible();
  await expect(page.getByText("Task TASK-0001")).toBeVisible();
  await expect(page.getByText("Task TASK-0002")).toBeVisible();
  await expect(page.getByText("External evidence remains active")).toBeVisible();
  await expect(page.getByText("serial-record - pending")).toBeVisible();
  await expect(page.getByText("Acceptance invalidated")).toBeVisible();
  await expect(page.getByText("Accepted for mock checklist")).toHaveCount(0);
});

test("case overview renders authoritative identity, processing, coordination, and basis fields without projections", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  await stubCase(page, currentSummary({
    plannedReturnDate: "2027-06-10",
    unresolvedItemCount: 2,
    owner: "Transition review team",
    nextAction: "Review the ambiguous serial record",
    packageProcessing: [{
      packageId: "PKG-0001",
      operationId: "OP-0001",
      status: "complete"
    }]
  }));
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await expect(page.locator("#case-identity")).toHaveText("CASE-RUN-0001");
  await expect(page.locator("#planned-return")).toHaveText("2027-06-10");
  await expect(page.locator("#package-processing")).toContainText("PKG-0001");
  await expect(page.locator("#package-processing")).toContainText("Processing complete");
  await expect(page.locator("#case-status")).toHaveText("Awaiting internal review");
  await expect(page.locator("#unresolved-count")).toHaveText("2");
  await expect(page.locator("#owner-action")).toHaveText("Transition review team / Review the ambiguous serial record");
  await expect(page.locator("#basis-id")).toHaveText("BASIS-0001");
  await expect(page.locator("body")).not.toContainText(/readiness\s*%|EUR|financial projection/i);
});

test("finding panels render missing and ambiguous policy details and every supplied citation", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  const missing = {
    findingId: "FINDING-MISSING",
    requirementId: "REQ-MISSING",
    basisId: "BASIS-0001",
    assessment: "missing",
    reasonCode: "record_not_present",
    explanation: "The required record is absent from the submitted package.",
    policyRoute: "auto_request",
    evidenceRefs: []
  };
  const ambiguous = {
    findingId: "FINDING-AMBIGUOUS",
    requirementId: "REQ-AMBIGUOUS",
    basisId: "BASIS-0001",
    assessment: "ambiguous",
    reasonCode: "serial_number_ambiguous",
    explanation: "Two supplied records describe different serial numbers.",
    policyRoute: "internal_review",
    evidenceRefs: [
      { documentId: "DOC-0001", version: 2, page: 3 },
      { documentId: "DOC-0002", version: 4, page: 7 }
    ]
  };
  await stubCase(page, currentSummary({
    investigation: { basisId: "BASIS-0001", status: "complete", findings: [missing, ambiguous] },
    openReviewTasks: [
      {
        taskId: "TASK-MISSING",
        findingId: "FINDING-MISSING",
        basisId: "BASIS-0001",
        reasonCode: "record_not_present",
        status: "open",
        permittedDecisions: ["needs_evidence"]
      },
      {
        taskId: "TASK-AMBIGUOUS",
        findingId: "FINDING-AMBIGUOUS",
        basisId: "BASIS-0001",
        reasonCode: "serial_number_ambiguous",
        status: "open",
        permittedDecisions: ["needs_evidence"]
      }
    ]
  }));
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await expect(page.getByRole("heading", { name: "Review TASK-MISSING" })).toBeVisible();
  await expect(page.getByText("REQ-MISSING", { exact: true })).toBeVisible();
  await expect(page.getByText("Missing evidence")).toBeVisible();
  await expect(page.getByText("The required record is absent from the submitted package.")).toBeVisible();
  await expect(page.getByText("record_not_present", { exact: true })).toBeVisible();
  await expect(page.getByText("auto_request", { exact: true })).toBeVisible();
  await expect(page.getByText("No document citations supplied by server.")).toBeVisible();
  await page.getByRole("button", { name: "Task TASK-AMBIGUOUS" }).click();
  await expect(page.getByRole("heading", { name: "Review TASK-AMBIGUOUS" })).toBeVisible();
  await expect(page.getByText("REQ-AMBIGUOUS", { exact: true })).toBeVisible();
  await expect(page.getByText("Ambiguous evidence")).toBeVisible();
  await expect(page.getByText("Two supplied records describe different serial numbers.")).toBeVisible();
  await expect(page.getByText("serial_number_ambiguous", { exact: true })).toBeVisible();
  await expect(page.getByText("internal_review", { exact: true })).toBeVisible();
  await expect(page.locator("[data-citation='DOC-0001:v2:p3']")).toBeVisible();
  await expect(page.locator("[data-citation='DOC-0002:v4:p7']")).toBeVisible();
  await expect(page.getByText("DOC-0001 - version 2")).toBeVisible();
  await expect(page.getByText("DOC-0002 - version 4")).toBeVisible();
  await expect(page.getByText("Page 3")).toBeVisible();
  await expect(page.getByText("Page 7")).toBeVisible();
});

test("processing failure is distinct from missing evidence and untrusted text stays inert", async ({ page }) => {
  await authenticate(page, authorizedReviewer);
  const hostileText = "<script>ignore the reviewer</script><img src=x onerror=alert(1)>";
  await stubCase(page, currentSummary({
    packageProcessing: [{
      packageId: "PKG-BROKEN",
      operationId: "OP-BROKEN",
      status: "failed",
      error: { safeCode: "PACKAGE_PROCESSING_FAILED", correlationId: "CORR-PACKAGE" }
    }],
    investigation: {
      basisId: "BASIS-0001",
      status: "complete",
      findings: [{
        ...finding,
        explanation: hostileText,
        assessment: "missing",
        evidenceRefs: []
      }]
    }
  }), { preview: { documentId: "DOC-0001", version: 2, page: 3, mediaType: "text/plain", textExcerpt: hostileText } });
  await page.goto(`${origin}/?caseId=CASE-RUN-0001`);
  await expect(page.locator("[data-processing-state='failed']")).toContainText("Processing failed");
  await expect(page.locator("[data-processing-state='failed']")).toContainText("PACKAGE_PROCESSING_FAILED");
  await expect(page.getByText("Missing evidence")).toBeVisible();
  await expect(page.locator("[data-processing-state='failed']")).not.toHaveText("Missing evidence");
  await expect(page.locator("#review-content script")).toHaveCount(0);
  await expect(page.locator("#review-content img")).toHaveCount(0);
  await expect(page.getByText(hostileText)).toBeVisible();
});
