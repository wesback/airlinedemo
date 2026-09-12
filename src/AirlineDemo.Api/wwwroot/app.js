(function () {
  "use strict";

  const config = window.airlineDemoConfig || {};
  const apiBaseUrl = String(config.apiBaseUrl || "/api").replace(/\/+$/, "");
  const configuredAuthorization = window.__AIRLINEDEMO_AUTHORIZATION__;
  const caseId = new URLSearchParams(window.location.search).get("caseId") ||
    (typeof config.caseId === "string" && config.caseId) || "CASE-RUN-0001";
  const runId = typeof config.runId === "string" ? config.runId : "";
  const configuredCaseId = typeof config.caseId === "string" ? config.caseId : "";
  const runIdElement = document.getElementById("run-id");
  const caseIdElement = document.getElementById("case-id");
  const inboxElement = document.getElementById("inbox-list");
  const loadButton = document.getElementById("load-fixture");
  const resetButton = document.getElementById("reset-run");
  const loadResult = document.getElementById("load-result");
  const resetResult = document.getElementById("reset-result");

  runIdElement.textContent = runId || "Not configured";
  caseIdElement.textContent = configuredCaseId || "Not configured";

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
    return { response, body };
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

  function setResult(element, state, value) {
    element.className = "operation-result " + state;
    element.textContent = value;
  }

  function setGlobalStatus(value) {
    document.getElementById("global-status").textContent = value;
  }

  function createElement(tag, className, value) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (typeof value === "string") element.textContent = value;
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
      inboxElement.appendChild(createElement(
        "div",
        "empty-state",
        "No delivered requests are available in this authenticated scope."
      ));
      return;
    }

    deliveredItems.forEach((item) => {
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
      if (item.templateVersion) {
        metadata.appendChild(createElement("span", "", "Approved wording " + item.templateVersion));
      }
      if (item.deliveredAt) {
        metadata.appendChild(createElement("span", "", "Delivered " + item.deliveredAt));
      }
      content.appendChild(metadata);
      content.appendChild(createElement("div", "message-label", "Approved rendered wording"));
      content.appendChild(createElement("p", "message-copy", item.message));

      const actions = createElement("div", "inbox-actions");
      const status = createElement("div", "delivery-status", "Delivered to mock inbox");
      status.dataset.responseState = "ready";
      actions.appendChild(status);
      const button = createElement("button", "button button-primary", "Submit mock response");
      button.type = "button";
      button.addEventListener("click", () => submitResponse(item, button, status));
      actions.appendChild(button);
      card.append(content, actions);
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
        body: JSON.stringify({ runId, confirmation: "RESET" })
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

  let summary = null;
  let selectedTask = null;
  let selectedFinding = null;
  let pending = false;
  let staleNotice = false;
  let confirmedNewBasis = false;

  const $ = (id) => document.getElementById(id);
  const text = (value) => value == null ? "" : String(value);
  const decisionLabel = {
    accept_evidence: "Accept evidence",
    dismiss_finding: "Dismiss finding",
    needs_evidence: "Needs more evidence"
  };

  class ApiError extends Error {
    constructor(status, safeCode, correlationId) {
      super(safeCode);
      this.status = status;
      this.safeCode = safeCode || "REQUEST_FAILED";
      this.correlationId = correlationId || "unavailable";
    }
  }

  async function api(path, options) {
    const headers = new Headers((options && options.headers) || {});
    if (configuredAuthorization) headers.set("Authorization", configuredAuthorization);
    if (options && options.body) headers.set("Content-Type", "application/json");
    const response = await fetch(path, Object.assign({}, options || {}, { headers }));
    if (!response.ok) {
      let safe = {};
      try {
        safe = await response.json();
      } catch (_) {
        // Use the safe fallback below when the response is not JSON.
      }
      throw new ApiError(response.status, safe.safeCode, safe.correlationId);
    }
    return response.status === 204 ? null : response.json();
  }

  function showSafeError(error) {
    const box = $("global-error");
    box.hidden = false;
    box.replaceChildren();
    const title = document.createElement("strong");
    title.textContent = "The review could not be completed";
    const detail = document.createElement("span");
    detail.textContent = `${error.safeCode} - correlation ${error.correlationId}`;
    box.append(title, detail);
  }

  function clearSafeError() {
    $("global-error").hidden = true;
    $("global-error").replaceChildren();
  }

  function showStaleNotice() {
    const box = $("stale-notice");
    box.hidden = false;
    box.replaceChildren();
    const title = document.createElement("strong");
    title.textContent = "This evidence basis changed";
    const detail = document.createElement("span");
    detail.textContent = "The previous review was not submitted. Check the new current basis and explicitly confirm before sending another decision.";
    box.append(title, detail);
  }

  function statusLabel(status) {
    return {
      awaiting_review: "Awaiting internal review",
      awaiting_external: "Awaiting external evidence",
      ready_for_acceptance: "Ready for acceptance",
      accepted: "Accepted for mock checklist",
      blocked: "Processing blocked",
      active: "Processing active",
      queued: "Queued",
      processing: "Processing",
      complete: "Processing complete",
      failed: "Processing failed",
      missing: "Missing evidence",
      ambiguous: "Ambiguous evidence",
      conflicting: "Conflicting evidence",
      satisfied: "Evidence satisfied"
    }[status] || status || "Unknown";
  }

  function valueAt(source, paths) {
    for (const path of paths) {
      const value = path.split(".").reduce((current, key) => (
        current && typeof current === "object" ? current[key] : undefined
      ), source);
      if (value !== undefined && value !== null && String(value).trim()) return value;
    }
    return null;
  }

  function displayValue(value) {
    if (value === null || value === undefined || value === "") return "Not supplied by server";
    if (typeof value === "object") {
      const label = value.displayName || value.name || value.subject || value.id;
      return label ? String(label) : JSON.stringify(value);
    }
    return String(value);
  }

  function setAuthoritativeText(id, value) {
    $(id).textContent = displayValue(value);
  }

  function renderPackageProcessing() {
    const list = $("package-processing");
    list.replaceChildren();
    const packages = Array.isArray(summary.packageProcessing) ? summary.packageProcessing : [];
    if (!packages.length) {
      list.append(createElement(
        "p",
        "muted",
        "No package processing state supplied by server."
      ));
      return;
    }
    packages.forEach((packageState) => {
      const item = createElement("article", "processing-item");
      item.dataset.processingState = text(packageState.status);
      if (packageState.status === "failed") item.classList.add("processing-failed");
      const heading = createElement("strong", "", text(packageState.packageId));
      const state = createElement(
        "span",
        "processing-state",
        statusLabel(packageState.status)
      );
      item.append(heading, state);
      if (packageState.error && packageState.error.safeCode) {
        item.append(createElement(
          "span",
          "processing-error",
          `${packageState.error.safeCode} - correlation ${displayValue(packageState.error.correlationId)}`
        ));
      }
      list.append(item);
    });
  }

  function renderSummary() {
    $("case-revision").textContent = text(summary.caseRevision);
    setAuthoritativeText("case-identity", summary.caseId);
    $("aircraft-id").textContent = text(summary.aircraftId);
    $("scope-label").textContent = `${text(summary.airlineId)} / ${text(summary.leaseId)}`;
    setAuthoritativeText("planned-return", valueAt(summary, [
      "plannedReturnDate",
      "plannedReturn",
      "returnDate",
      "case.plannedReturnDate",
      "context.plannedReturnDate"
    ]));
    $("case-status").textContent = statusLabel(summary.status);
    setAuthoritativeText("unresolved-count", valueAt(summary, [
      "unresolvedItemCount",
      "unresolvedItemsCount",
      "coordination.unresolvedItemCount",
      "coordination.unresolvedItemsCount"
    ]));
    const owner = valueAt(summary, [
      "owner",
      "ownerId",
      "coordination.owner",
      "coordination.ownerId",
      "coordination.assignedTo"
    ]);
    const action = valueAt(summary, [
      "action",
      "nextAction",
      "coordination.action",
      "coordination.nextAction"
    ]);
    setAuthoritativeText("owner-action", owner || action
      ? `${displayValue(owner)} / ${displayValue(action)}`
      : null);
    $("basis-id").textContent = text(
      selectedTask?.basisId ||
      valueAt(summary, [
        "currentBasisId",
        "evidenceBasisId",
        "investigation.basisId",
        "evidenceBasis.basisId"
      ]) ||
      "Not supplied by server"
    );
    renderPackageProcessing();

    const tasks = (summary.openReviewTasks || []).filter((task) => task.status === "open");
    $("task-count").textContent = String(tasks.length);
    const list = $("task-list");
    list.replaceChildren();
    if (!tasks.length) {
      const empty = document.createElement("p");
      empty.className = "muted";
      empty.textContent = "No open internal review tasks.";
      list.append(empty);
    } else {
      tasks.forEach((task) => {
        const button = document.createElement("button");
        button.className = "task-button";
        button.type = "button";
        button.setAttribute("aria-current", selectedTask && selectedTask.taskId === task.taskId ? "true" : "false");
        const title = document.createElement("strong");
        title.textContent = `Task ${task.taskId}`;
        const detail = document.createElement("span");
        detail.textContent = `${task.reasonCode} - basis ${task.basisId}`;
        button.append(title, detail);
        button.addEventListener("click", () => openTask(task));
        list.append(button);
      });
    }

    const external = $("external-work");
    external.replaceChildren();
    const requests = summary.activeEvidenceRequests || [];
    if (requests.length) {
      const heading = document.createElement("h3");
      heading.textContent = "External evidence remains active";
      external.append(heading);
      requests.forEach((request) => {
        const item = document.createElement("div");
        item.className = "external-item";
        const title = document.createElement("strong");
        title.textContent = "Evidence request";
        const detail = document.createElement("span");
        detail.textContent = `${request.requestKey} - ${request.status}`;
        item.append(title, detail);
        external.append(item);
      });
    }
    const reconciliations = summary.reconciliationItems || [];
    if (reconciliations.length) {
      const item = document.createElement("div");
      item.className = "external-item";
      const title = document.createElement("strong");
      title.textContent = "Acceptance invalidated";
      const detail = document.createElement("span");
      detail.textContent = "The current basis requires a new review decision.";
      item.append(title, detail);
      external.append(item);
    }
  }

  function findingForTask(task) {
    return summary && summary.investigation && summary.investigation.findings
      ? summary.investigation.findings.find((finding) => finding.findingId === task.findingId)
      : null;
  }

  async function openTask(task) {
    selectedTask = null;
    selectedFinding = null;
    confirmedNewBasis = false;
    renderSummary();
    renderTask();
    let authorizedTask;
    try {
      authorizedTask = await api(
        `/api/cases/${encodeURIComponent(caseId)}/review-tasks/${encodeURIComponent(task.taskId)}`);
    } catch (error) {
      showSafeError(error);
      return;
    }
    if (!authorizedTask || authorizedTask.taskId !== task.taskId || authorizedTask.status !== "open") {
      showSafeError(new ApiError(403, "ACTION_FORBIDDEN", "unavailable"));
      return;
    }

    selectedTask = authorizedTask;
    selectedFinding = findingForTask(authorizedTask);
    confirmedNewBasis = false;
    renderSummary();
    renderTask();
    const references = selectedFinding && Array.isArray(selectedFinding.evidenceRefs)
      ? selectedFinding.evidenceRefs
      : [];
    if (!references.length) return;
    const previews = await Promise.all(references.map(async (reference) => {
      try {
        return await api(
          `/api/cases/${encodeURIComponent(caseId)}/evidence/${encodeURIComponent(reference.documentId)}?version=${reference.version}&page=${reference.page}`
        );
      } catch (error) {
        showSafeError(error);
        return null;
      }
    }));
    if (selectedTask && selectedTask.taskId === authorizedTask.taskId) {
      renderPreviews(references, previews);
    }
  }

  function renderPreviews(references, previews) {
    const existing = $("evidence-preview");
    if (!existing) return;
    existing.replaceChildren();
    references.forEach((reference, index) => {
      const preview = previews[index];
      const citation = document.createElement("article");
      citation.className = "evidence-citation";
      citation.dataset.citation = `${reference.documentId}:v${reference.version}:p${reference.page}`;
      const header = document.createElement("header");
      const title = document.createElement("strong");
      title.textContent = `${reference.documentId} - version ${reference.version}`;
      const page = document.createElement("span");
      page.textContent = `Page ${reference.page}`;
      header.append(title, page);
      citation.append(header);
      if (preview && typeof preview.textExcerpt === "string") {
        const quote = document.createElement("blockquote");
        quote.textContent = preview.textExcerpt;
        citation.append(quote);
      } else {
        citation.append(createElement(
          "p",
          "muted",
          "Scoped evidence preview unavailable."
        ));
      }
      existing.append(citation);
    });
  }

  function policyRouteFor(finding, task) {
    return valueAt(finding, [
      "policyRoute",
      "policyOutcome",
      "policyDecision.outcome",
      "policy.outcome"
    ]) || valueAt(task, [
      "policyRoute",
      "policyOutcome",
      "policyDecision.outcome"
    ]) || valueAt(summary, [
      `policyRoutes.${finding && finding.findingId}`,
      `policyDecisions.${finding && finding.findingId}.outcome`
    ]);
  }

  function renderFindingDetails(finding, task, content) {
    if (!finding) return;
    const details = document.createElement("dl");
    details.className = "finding-details";
    [
      ["Requirement", finding.requirementId],
      ["Assessment", statusLabel(finding.assessment)],
      ["Explanation", finding.explanation],
      ["Reason code", finding.reasonCode],
      ["Policy route", policyRouteFor(finding, task)]
    ].forEach(([label, value]) => {
      const term = createElement("dt", "", label);
      const description = createElement("dd", "", displayValue(value));
      details.append(term, description);
    });
    content.append(details);
  }

  function renderTask() {
    const heading = $("review-heading");
    const content = $("review-content");
    content.replaceChildren();
    $("task-state").textContent = selectedTask ? "Open task" : "Waiting";
    if (!selectedTask) {
      heading.textContent = "Select a task to review";
      const message = document.createElement("p");
      message.className = "muted";
      message.textContent = "Only evidence and decisions returned by the server appear here.";
      content.append(message);
      return;
    }
    heading.textContent = `Review ${selectedTask.taskId}`;
    const finding = selectedFinding;
    const meta = document.createElement("p");
    meta.className = "finding-meta";
    meta.textContent = finding
      ? `${finding.requirementId} - ${finding.assessment} - ${finding.reasonCode}`
      : `Finding ${selectedTask.findingId} - ${selectedTask.reasonCode}`;
    content.append(meta);
    renderFindingDetails(finding, selectedTask, content);
    const evidence = document.createElement("section");
    evidence.id = "evidence-preview";
    evidence.className = "evidence-preview";
    const references = finding && Array.isArray(finding.evidenceRefs)
      ? finding.evidenceRefs
      : [];
    if (references.length) {
      const loading = document.createElement("p");
      loading.className = "muted";
      loading.textContent = "Loading scoped evidence previews...";
      evidence.append(loading);
    } else {
      evidence.append(createElement(
        "p",
        "muted",
        "No document citations supplied by server."
      ));
    }
    content.append(evidence);

    const form = document.createElement("form");
    form.className = "decision-form";
    const legend = document.createElement("h3");
    legend.textContent = "Server-permitted decision";
    form.append(legend);
    const options = document.createElement("div");
    options.className = "decision-options";
    (Array.isArray(selectedTask.permittedDecisions) ? selectedTask.permittedDecisions : [])
      .forEach((decision, index) => {
        const wrapper = document.createElement("div");
        wrapper.className = "decision-option";
        const input = document.createElement("input");
        input.type = "radio";
        input.name = "decision";
        input.id = `decision-${index}`;
        input.value = decision;
        input.required = true;
        const label = document.createElement("label");
        label.htmlFor = input.id;
        label.textContent = decisionLabel[decision] || decision;
        wrapper.append(input, label);
        options.append(wrapper);
      });
    form.append(options);
    const reasonLabel = document.createElement("label");
    reasonLabel.className = "reason-label";
    reasonLabel.htmlFor = "review-reason";
    reasonLabel.textContent = "Reason (required)";
    form.append(reasonLabel);
    const reason = document.createElement("textarea");
    reason.id = "review-reason";
    reason.name = "reason";
    reason.required = true;
    reason.maxLength = 4000;
    reason.placeholder = "Explain how the current cited evidence supports this decision.";
    form.append(reason);
    const footer = document.createElement("div");
    footer.className = "form-footer";
    const hint = document.createElement("span");
    hint.className = "muted";
    hint.textContent = "Submitting sends the current case revision.";
    const submit = document.createElement("button");
    submit.className = "submit-button";
    submit.type = "submit";
    submit.textContent = "Submit review decision";
    footer.append(hint, submit);
    form.append(footer);
    if (staleNotice) {
      const confirmation = document.createElement("label");
      confirmation.className = "confirmation";
      const checkbox = document.createElement("input");
      checkbox.type = "checkbox";
      checkbox.id = "basis-confirmed";
      checkbox.checked = confirmedNewBasis;
      checkbox.addEventListener("change", () => {
        confirmedNewBasis = checkbox.checked;
        submit.disabled = !confirmedNewBasis;
      });
      const copy = document.createElement("span");
      copy.textContent = "I have reviewed the newly loaded current basis and want to send a new decision.";
      confirmation.append(checkbox, copy);
      form.append(confirmation);
      submit.disabled = !confirmedNewBasis;
    }
    form.addEventListener("submit", (event) => submitReview(event, form));
    content.append(form);
  }

  async function submitReview(event, form) {
    event.preventDefault();
    if (pending || (staleNotice && !confirmedNewBasis)) return;
    const decision = form.elements.decision && form.elements.decision.value;
    const reason = form.elements.reason.value.trim();
    if (!decision || !reason || !selectedTask) return;
    pending = true;
    const submit = form.querySelector("button[type=submit]");
    submit.disabled = true;
    clearSafeError();
    try {
      await api(`/api/cases/${encodeURIComponent(caseId)}/reviews`, {
        method: "POST",
        headers: { "If-Match": `"${summary.caseRevision}"` },
        body: JSON.stringify({
          findingId: selectedTask.findingId,
          basisId: selectedTask.basisId,
          decision,
          reason
        })
      });
      staleNotice = false;
      confirmedNewBasis = false;
      await loadCase();
    } catch (error) {
      if (error.status === 412) {
        staleNotice = true;
        confirmedNewBasis = false;
        await loadCase();
        showStaleNotice();
      } else {
        showSafeError(error);
        submit.disabled = false;
      }
    } finally {
      pending = false;
    }
  }

  async function loadCase() {
    clearSafeError();
    try {
      summary = await api(`/api/cases/${encodeURIComponent(caseId)}`);
      const tasks = (summary.openReviewTasks || []).filter((task) => task.status === "open");
      const taskToOpen = selectedTask
        ? tasks.find((task) => task.findingId === selectedTask.findingId) || tasks[0] || null
        : tasks[0] || null;
      selectedTask = null;
      selectedFinding = null;
      renderSummary();
      renderTask();
      if (taskToOpen) await openTask(taskToOpen);
    } catch (error) {
      showSafeError(error);
      $("case-status").textContent = "Unavailable";
    }
  }

  loadInbox();
  window.addEventListener("DOMContentLoaded", loadCase);
}());
