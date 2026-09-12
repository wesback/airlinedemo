(function () {
  "use strict";

  const config = window.airlineDemoConfig || {};
  const apiBaseUrl = String(config.apiBaseUrl || "/api").replace(/\/+$/, "");
  const runIdElement = document.getElementById("run-id");
  const caseIdElement = document.getElementById("case-id");
  const inboxElement = document.getElementById("inbox-list");
  const loadButton = document.getElementById("load-fixture");
  const resetButton = document.getElementById("reset-run");
  const loadResult = document.getElementById("load-result");
  const resetResult = document.getElementById("reset-result");

  const runId = typeof config.runId === "string" ? config.runId : "";
  const caseId = typeof config.caseId === "string" ? config.caseId : "";
  runIdElement.textContent = runId || "Not configured";
  caseIdElement.textContent = caseId || "Not configured";

  function authHeaders(extra) {
    const headers = Object.assign({ Accept: "application/json" }, extra || {});
    if (typeof config.authToken === "string" && config.authToken.trim()) {
      headers.Authorization = "Bearer " + config.authToken;
    }
    return headers;
  }

  function endpoint(path) {
    return apiBaseUrl + "/" + String(path).replace(/^\/+/, "");
  }

  async function readResponse(response) {
    let body = null;
    try {
      body = await response.json();
    } catch (_) {
      body = null;
    }
    return { response: response, body: body };
  }

  function safeErrorMessage(result) {
    const body = result && result.body;
    if (!body || typeof body.safeCode !== "string" || typeof body.correlationId !== "string") {
      return "The server rejected the operation. Contact the demo operator.";
    }
    const detail = typeof body.message === "string" && body.message.trim()
      ? ": " + body.message.trim()
      : "";
    return body.safeCode + detail + " (correlation " + body.correlationId + ")";
  }

  function setResult(element, state, text) {
    element.className = "operation-result " + state;
    element.textContent = text;
  }

  function setGlobalStatus(text) {
    document.getElementById("global-status").textContent = text;
  }

  function createElement(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (typeof text === "string") element.textContent = text;
    return element;
  }

  function getResponsePackage() {
    const candidate = config.responsePackage && config.responsePackage.package
      ? config.responsePackage.package
      : config.responsePackage;
    return candidate && typeof candidate === "object" ? candidate : null;
  }

  function createResponseBody(item) {
    const packageData = getResponsePackage();
    if (!packageData || typeof packageData.packageId !== "string") return null;
    const context = item.context || {};
    const now = new Date().toISOString();
    const sourceEvent = config.responsePackage && config.responsePackage.event
      ? config.responsePackage.event
      : {};
    return {
      event: Object.assign({}, sourceEvent, {
        schemaVersion: sourceEvent.schemaVersion || packageData.schemaVersion || "1.0",
        eventId: "UI-" + Date.now().toString(36),
        type: "partner.response.received",
        runId: packageData.runId || context.runId,
        caseId: packageData.caseId || context.caseId,
        airlineId: packageData.airlineId || context.airlineId,
        aircraftId: packageData.aircraftId || context.aircraftId,
        leaseId: packageData.leaseId || context.leaseId,
        occurredAt: sourceEvent.occurredAt || now,
        scenarioEffectiveAt: sourceEvent.scenarioEffectiveAt || packageData.scenarioEffectiveAt || now,
        correlationId: "UI-" + Date.now().toString(36),
        payload: { packageId: packageData.packageId, requestId: item.requestId }
      }),
      package: packageData
    };
  }

  function isAuthoritativeDeliveredItem(item) {
    return item &&
      typeof item.requestId === "string" &&
      item.requestId.trim() &&
      item.recipientRef === "mock-partner-inbox" &&
      typeof item.templateVersion === "string" &&
      item.templateVersion.trim() &&
      typeof item.message === "string" &&
      item.message.trim() &&
      typeof item.deliveredAt === "string" &&
      item.deliveredAt.trim();
  }

  function renderInbox(items) {
    inboxElement.replaceChildren();
    const deliveredItems = Array.isArray(items)
      ? items.filter(isAuthoritativeDeliveredItem)
      : [];
    if (deliveredItems.length === 0) {
      inboxElement.appendChild(createElement("div", "empty-state", "No delivered requests are available in this authenticated scope."));
      return;
    }

    deliveredItems.forEach(function (item) {
      const card = createElement("article", "inbox-item");
      card.dataset.requestId = item.requestId;
      const content = createElement("div");
      content.appendChild(createElement("h3", "", "Evidence request"));
      const metadata = createElement("div", "request-meta");
      const requestId = createElement("span");
      requestId.appendChild(createElement("strong", "", "Request ID: "));
      requestId.appendChild(document.createTextNode(item.requestId));
      metadata.appendChild(requestId);
      const packageData = getResponsePackage();
      if (packageData && packageData.packageId) {
        const packageId = createElement("span");
        packageId.appendChild(createElement("strong", "", "Response package ID: "));
        packageId.appendChild(document.createTextNode(packageData.packageId));
        metadata.appendChild(packageId);
      }
      if (item.templateVersion) metadata.appendChild(createElement("span", "", "Approved wording " + item.templateVersion));
      if (item.deliveredAt) metadata.appendChild(createElement("span", "", "Delivered " + item.deliveredAt));
      content.appendChild(metadata);
      content.appendChild(createElement("div", "message-label", "Approved rendered wording"));
      content.appendChild(createElement("p", "message-copy", item.message || "No message was supplied by the server."));

      const actions = createElement("div", "inbox-actions");
      const status = createElement("div", "delivery-status", "Delivered to mock inbox");
      status.dataset.responseState = "ready";
      actions.appendChild(status);
      const button = createElement("button", "button button-primary", "Submit mock response");
      button.type = "button";
      button.addEventListener("click", function () {
        submitResponse(item, button, status);
      });
      actions.appendChild(button);
      card.appendChild(content);
      card.appendChild(actions);
      inboxElement.appendChild(card);
    });
  }

  async function loadInbox() {
    try {
      const result = await readResponse(await fetch(endpoint("/mock-inbox"), {
        method: "GET",
        headers: authHeaders()
      }));
      if (!result.response.ok) throw result;
      renderInbox(result.body);
    } catch (error) {
      inboxElement.replaceChildren(createElement("div", "empty-state", safeErrorMessage(error)));
    }
  }

  async function submitResponse(item, button, status) {
    const body = createResponseBody(item);
    if (!body) {
      status.className = "delivery-status";
      status.textContent = "Response package unavailable";
      status.dataset.responseState = "error";
      return;
    }
    button.disabled = true;
    status.className = "delivery-status";
    status.textContent = "Submitting response…";
    status.dataset.responseState = "pending";
    setGlobalStatus("Submitting the scoped mock-partner response.");
    try {
      const result = await readResponse(await fetch(endpoint("/partner-responses"), {
        method: "POST",
        headers: authHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify(body)
      }));
      if (!result.response.ok) throw result;
      status.textContent = "Response accepted by server; reassessment queued";
      status.dataset.responseState = "accepted";
      setGlobalStatus("Mock-partner response accepted by the server.");
    } catch (error) {
      button.disabled = false;
      status.textContent = safeErrorMessage(error);
      status.dataset.responseState = "error";
      setGlobalStatus("Mock-partner response was not accepted.");
    }
  }

  async function loadFixture() {
    const fixtureRequest = config.fixtureLoad || config.fixtureSubmission;
    if (!fixtureRequest || typeof fixtureRequest !== "object") {
      setResult(loadResult, "error", "No approved fixture is configured for this run.");
      return;
    }
    if (!window.confirm("Load the approved fixture for run " + runId + "?")) return;
    loadButton.disabled = true;
    setResult(loadResult, "pending", "Submitting fixture load…");
    try {
      const result = await readResponse(await fetch(
        endpoint(config.fixtureLoadEndpoint || "/packages"),
        {
          method: "POST",
          headers: authHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify(fixtureRequest)
        }
      ));
      if (!result.response.ok) throw result;
      const receiptId = result.body && typeof result.body.receiptId === "string"
        ? result.body.receiptId
        : "not returned";
      setResult(loadResult, "success", "Fixture load accepted · receipt " + receiptId);
    } catch (error) {
      setResult(loadResult, "error", safeErrorMessage(error));
    } finally {
      loadButton.disabled = false;
    }
  }

  async function resetRun() {
    if (!runId) {
      setResult(resetResult, "error", "No server-selected run is available.");
      return;
    }
    if (!window.confirm("Reset demonstration run " + runId + "?")) return;
    resetButton.disabled = true;
    setResult(resetResult, "pending", "Resetting the scoped run…");
    try {
      const result = await readResponse(await fetch(endpoint("/demo/runs/" + encodeURIComponent(runId)), {
        method: "DELETE",
        headers: authHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify({ runId: runId, confirmation: "RESET" })
      }));
      if (!result.response.ok) throw result;
      const receiptId = result.body && typeof result.body.receiptId === "string"
        ? result.body.receiptId
        : "not returned";
      const affected = result.body && Number.isInteger(result.body.affectedRecordCount)
        ? " · " + result.body.affectedRecordCount + " records"
        : "";
      setResult(resetResult, "success", "Reset confirmed by server · receipt " + receiptId + affected);
    } catch (error) {
      setResult(resetResult, "error", safeErrorMessage(error));
    } finally {
      resetButton.disabled = false;
    }
  }

  loadButton.addEventListener("click", loadFixture);
  resetButton.addEventListener("click", resetRun);
  loadInbox();
}());
