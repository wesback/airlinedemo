const { test, expect } = require("@playwright/test");

const inboxItem = {
  itemId: "INBOX-1",
  requestId: "REQ-1",
  requestKey: "component-history",
  context: { runId: "RUN-41", caseId: "CASE-41", airlineId: "AIRLINE-1", aircraftId: "AC-1", leaseId: "LEASE-1" },
  recipientRef: "mock-partner-inbox",
  templateVersion: "evidence-request-v1",
  message: "Please provide the approved component history record.",
  deliveredAt: "2026-09-12T10:00:00Z"
};

const undeliveredInboxItem = {
  ...inboxItem,
  itemId: "INBOX-PENDING",
  requestId: "REQ-PENDING",
  deliveredAt: null,
  message: "This pending request must not be shown."
};

const nonAuthoritativeInboxItem = {
  ...inboxItem,
  itemId: "INBOX-UNSCOPED",
  requestId: "REQ-UNSCOPED",
  recipientRef: "real-partner-email",
  message: "This non-authoritative request must not be shown."
};

const responsePackage = {
  schemaVersion: "1.0",
  packageId: "PKG-RESPONSE-1",
  runId: "RUN-41",
  caseId: "CASE-41",
  airlineId: "AIRLINE-1",
  aircraftId: "AC-1",
  leaseId: "LEASE-1",
  submittedAt: "2026-09-12T10:00:00Z",
  scenarioEffectiveAt: "2026-09-12T09:00:00Z",
  manifest: []
};

async function configure(page, responses = {}) {
  await page.addInitScript(({ responsePackage }) => {
    window.airlineDemoConfig = {
      apiBaseUrl: "/api",
      authToken: "run=RUN-41;airline=AIRLINE-1;aircraft=AC-1;lease=LEASE-1",
      runId: "RUN-41",
      caseId: "CASE-41",
      responsePackage,
      fixtureLoad: { event: { eventId: "EVT-1", type: "package.submitted" }, package: responsePackage }
    };
  }, { responsePackage });
  await page.route("**/api/partner-responses", route => {
    if (responses.partner) return responses.partner(route);
    return route.fulfill({ status: 202, json: { operationId: "OP-1", caseId: "CASE-41", receiptId: "RECEIPT-RESPONSE-1" } });
  });
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Mock partner inbox" })).toBeVisible();
}

test("renders only authoritative delivered requests and exposes a bounded mock response", async ({ page }) => {
  await configure(page);
  const inboxResponse = await page.request.get("/api/mock-inbox", {
    headers: {
      Authorization: "Bearer run=RUN-41;airline=AIRLINE-1;aircraft=AC-1;lease=LEASE-1"
    }
  });
  expect(inboxResponse.ok()).toBe(true);
  expect((await inboxResponse.json()).map(item => item.requestId)).toEqual(["REQ-1"]);
  await expect(page.getByText("Synthetic partner portal")).toBeVisible();
  await expect(page.getByText("REQ-1")).toBeVisible();
  await expect(page.getByText("PKG-RESPONSE-1")).toBeVisible();
  await expect(page.getByText("Approved rendered wording")).toBeVisible();
  await expect(page.getByText(inboxItem.message)).toBeVisible();
  await expect(page.getByText("Delivered to mock inbox")).toBeVisible();
  await expect(page.getByText("REQ-PENDING")).toHaveCount(0);
  await expect(page.getByText("REQ-UNSCOPED")).toHaveCount(0);
  await expect(page.getByText(undeliveredInboxItem.message)).toHaveCount(0);
  await expect(page.getByText(nonAuthoritativeInboxItem.message)).toHaveCount(0);
  await expect(page.locator("a[href^='mailto:'], a[href^='tel:'], a[href^='http']")).toHaveCount(0);
  await expect(page.getByText("real-partner-email")).toHaveCount(0);
  await expect(page.locator("input, select, textarea")).toHaveCount(0);
  await expect(page.getByRole("button", { name: /submit mock response/i })).toHaveCount(1);
});

test("submits the displayed request and package IDs, and keeps failures safe", async ({ page }) => {
  const received = [];
  let submissions = 0;
  await configure(page, {
    partner: async route => {
      received.push(JSON.parse(route.request().postData()));
      submissions += 1;
      if (submissions === 1) {
        await route.fulfill({ status: 202, json: { operationId: "OP-1", caseId: "CASE-41", receiptId: "RECEIPT-RESPONSE-1" } });
        return;
      }
      if (submissions === 2) {
        await route.fulfill({ status: 403, json: { safeCode: "ACTION_FORBIDDEN", correlationId: "CORR-SAFE" } });
        return;
      }
      await route.abort("failed");
    }
  });
  await page.getByRole("button", { name: /submit mock response/i }).click();
  await expect(page.getByText(/response accepted by server/i)).toBeVisible();
  await page.reload();
  await expect(page.getByText("REQ-1")).toBeVisible();
  await page.getByRole("button", { name: /submit mock response/i }).click();
  await expect(page.getByText(/ACTION_FORBIDDEN \(correlation CORR-SAFE\)/)).toBeVisible();
  await expect(page.getByText(/response accepted/i)).toHaveCount(0);
  await page.getByRole("button", { name: /submit mock response/i }).click();
  await expect(page.getByText("The server rejected the operation. Contact the demo operator.")).toBeVisible();
  await expect(page.getByText(/response accepted/i)).toHaveCount(0);
  expect(received).toHaveLength(3);
  for (const request of received) {
    expect(request.event.payload.requestId).toBe("REQ-1");
    expect(request.event.payload.packageId).toBe("PKG-RESPONSE-1");
  }
});

test("requires confirmation before loading or resetting and shows server reset receipt", async ({ page }) => {
  let calls = [];
  await configure(page);
  await page.route("**/api/packages", route => {
    calls.push("load");
    return route.fulfill({ status: 202, json: { receiptId: "RECEIPT-LOAD-1" } });
  });
  await page.route("**/api/demo/runs/RUN-41", route => {
    calls.push("reset");
    return route.fulfill({ status: 200, json: { runId: "RUN-41", receiptId: "RECEIPT-RESET-SERVER", affectedRecordCount: 4 } });
  });
  await page.on("dialog", dialog => dialog.dismiss());
  await page.getByRole("button", { name: "Load fixture" }).click();
  await page.getByRole("button", { name: "Reset run" }).click();
  expect(calls).toEqual([]);
  await page.removeAllListeners("dialog");
  await page.on("dialog", dialog => dialog.accept());
  await page.getByRole("button", { name: "Load fixture" }).click();
  await expect(page.getByText(/RECEIPT-LOAD-1/)).toBeVisible();
  await page.getByRole("button", { name: "Reset run" }).click();
  await expect(page.getByText(/RECEIPT-RESET-SERVER/)).toBeVisible();
  expect(calls).toEqual(["load", "reset"]);
});

test("does not expose arbitrary fixture paths, evaluator answers, role switching, or local reset success", async ({ page }) => {
  await configure(page);
  await expect(page.getByText("RUN-41")).toBeVisible();
  await expect(page.locator("input, select, textarea")).toHaveCount(0);
  await expect(page.locator("input[type=file]")).toHaveCount(0);
  await expect(page.getByRole("button", { name: /role|evaluator|recipient/i })).toHaveCount(0);
  const interactiveControls = await page.locator(
    "a, button, input, select, textarea, [role], [contenteditable='true']"
  ).evaluateAll(elements => elements.map(element => ({
    text: element.textContent || "",
    ariaLabel: element.getAttribute("aria-label") || "",
    href: element.getAttribute("href") || ""
  })));
  expect(interactiveControls.some(control =>
    /evaluator|answer key|recipient|role switch/i.test(
      `${control.text} ${control.ariaLabel} ${control.href}`
    )
  )).toBe(false);
  await expect(page.getByText(/reset confirmed by server/i)).toHaveCount(0);
});
