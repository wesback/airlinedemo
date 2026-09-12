(function () {
  "use strict";

  const caseId = new URLSearchParams(window.location.search).get("caseId") || "CASE-RUN-0001";
  const configuredAuthorization = window.__AIRLINEDEMO_AUTHORIZATION__;
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
      try { safe = await response.json(); } catch (_) { /* use the safe fallback below */ }
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
      active: "Processing active"
    }[status] || status || "Unknown";
  }

  function renderSummary() {
    $("case-revision").textContent = text(summary.caseRevision);
    $("aircraft-id").textContent = text(summary.aircraftId);
    $("scope-label").textContent = `${text(summary.airlineId)} / ${text(summary.leaseId)}`;
    $("case-status").textContent = statusLabel(summary.status);
    $("basis-id").textContent = selectedTask ? text(selectedTask.basisId) : "Current server basis";

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
    if (!authorizedTask ||
        authorizedTask.taskId !== task.taskId ||
        authorizedTask.status !== "open") {
      showSafeError(new ApiError(403, "ACTION_FORBIDDEN", "unavailable"));
      return;
    }

    selectedTask = authorizedTask;
    selectedFinding = findingForTask(authorizedTask);
    confirmedNewBasis = false;
    renderSummary();
    renderTask();
    const reference = selectedFinding && selectedFinding.evidenceRefs && selectedFinding.evidenceRefs[0];
    if (!reference) return;
    try {
      const preview = await api(`/api/cases/${encodeURIComponent(caseId)}/evidence/${encodeURIComponent(reference.documentId)}?version=${reference.version}&page=${reference.page}`);
      if (selectedTask && selectedTask.taskId === authorizedTask.taskId) renderPreview(preview);
    } catch (error) {
      showSafeError(error);
    }
  }

  function renderPreview(preview) {
    const existing = $("evidence-preview");
    if (!existing) return;
    existing.replaceChildren();
    const header = document.createElement("header");
    const title = document.createElement("strong");
    title.textContent = `${preview.documentId} - version ${preview.version}`;
    const page = document.createElement("span");
    page.textContent = `Page ${preview.page}`;
    header.append(title, page);
    const quote = document.createElement("blockquote");
    quote.textContent = preview.textExcerpt;
    existing.append(header, quote);
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
    if (finding) {
      const explanation = document.createElement("p");
      explanation.className = "muted";
      explanation.textContent = finding.explanation;
      content.append(explanation);
    }
    const evidence = document.createElement("section");
    evidence.id = "evidence-preview";
    evidence.className = "evidence-preview";
    const loading = document.createElement("p");
    loading.className = "muted";
    loading.textContent = "Loading scoped evidence preview...";
    evidence.append(loading);
    content.append(evidence);

    const form = document.createElement("form");
    form.className = "decision-form";
    const legend = document.createElement("h3");
    legend.textContent = "Server-permitted decision";
    form.append(legend);
    const options = document.createElement("div");
    options.className = "decision-options";
    (Array.isArray(selectedTask.permittedDecisions) ? selectedTask.permittedDecisions : []).forEach((decision, index) => {
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
    form.append(reasonLabel, reason);
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

  window.addEventListener("DOMContentLoaded", loadCase);
})();
