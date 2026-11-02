export type CopilotCitation = { sourceId: string; recordId: string; recordVersion: number };
export type CopilotScreening = { state: 'clear' | 'detected' | 'unavailable' | string; service?: string; flaggedFields: string[] };
export type CopilotRecord = CopilotCitation & {
  asOf?: string; caseId?: string; kind?: string; stale?: boolean; payload?: unknown; synthetic?: boolean; screening?: CopilotScreening;
};
export type CopilotConfirmation = {
  contractVersion: number; proposalId: string; caseId: string; aircraftId: string; action: string; expectedRevision: number;
  note: string; createdAt: string; expiresAt: string; state: string; simulated: boolean; warning: string;
  controls?: { decision: string; advance: string };
};
export type CopilotAnswer = {
  contractVersion: number; answer: string; citations: CopilotCitation[]; records: CopilotRecord[]; confirmation?: CopilotConfirmation;
};
export type CopilotReceipt = {
  contractVersion: number; proposalId: string; caseId: string; decision: string; revision: number; auditSequence: number; simulated: boolean;
};
export type CopilotActivityItem = {
  invocationId: string; operation: string; at: string; outcome: string; httpStatus: number; resultCode: string; source: 'from copilot';
  user: string | null; role: string; provenance: string; records: CopilotCitation[];
  screening: { state: string; flaggedFields: string[] } | null; proposalId: string | null;
  gate: { name: string; result: string; blockers: string[] } | null;
};
export type CopilotActivity = { contractVersion: number; caseId: string; items: CopilotActivityItem[] };
export type CopilotQuestionBody = { question: string; selectedCaseId?: string; releaseNote?: string };
export type CopilotOutcome<T> = { ok: true; value: T } | { ok: false; status: number; code: string; blockers: string[] };

export const copilotCaseUrl = (caseId: string, suffix: string) => `/api/v1/cases/${encodeURIComponent(caseId)}/copilot${suffix}`;

/** POSTs a JSON body with the session anti-forgery token and reads either the value or the closed error code. */
export async function postCopilot<T>(url: string, csrfToken: string, body: unknown): Promise<CopilotOutcome<T>> {
  let response: Response;
  try {
    response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
      body: JSON.stringify(body),
    });
  } catch {
    return { ok: false, status: 0, code: 'network_error', blockers: [] };
  }
  let payload: unknown = null;
  try { payload = await response.json(); } catch { payload = null; }
  if (response.ok && payload && typeof payload === 'object') return { ok: true, value: payload as T };
  const error = (payload as { error?: { code?: unknown; blockers?: unknown } } | null)?.error;
  const code = typeof error?.code === 'string' ? error.code : response.ok ? 'invalid_response' : `http_${response.status}`;
  const blockers = Array.isArray(error?.blockers) ? error.blockers.filter((item): item is string => typeof item === 'string') : [];
  return { ok: false, status: response.status, code, blockers };
}

/**
 * Reads the copilot activity of a case. A 403 or 404 means the case or role is outside the copilot scope, so there is
 * no copilot activity to show; any other failure is reported as unavailable.
 */
export async function getCopilotActivity(caseId: string): Promise<CopilotActivityItem[] | 'unavailable'> {
  try {
    const response = await fetch(copilotCaseUrl(caseId, '/activity'));
    if (response.status === 403 || response.status === 404) return [];
    if (!response.ok) return 'unavailable';
    const body = await response.json() as Partial<CopilotActivity>;
    return Array.isArray(body.items) ? body.items : 'unavailable';
  } catch {
    return 'unavailable';
  }
}

const messages: Record<string, string> = {
  invalid_question: 'Enter a question of 1 to 2000 characters.',
  invalid_request: 'The request was not accepted. Check the question and try again.',
  unauthenticated: 'Your session has ended. Sign in again.',
  forbidden: 'Your role is not permitted to do this.',
  anti_forgery_failed: 'The request could not be verified. Sign in again.',
  origin_forbidden: 'The request came from an origin that is not allowed.',
  host_forbidden: 'The request came from a host that is not allowed.',
  case_not_found: 'This case is not available to you.',
  case_selection_required: 'Open a case first. Without a selected case the copilot can only list your cases.',
  selected_case_mismatch: 'The copilot tried to use a case other than the one you selected. Nothing was read.',
  release_note_required: 'A release note is required to propose a release.',
  invalid_release_note: 'The release note must be 1 to 2000 characters.',
  copilot_tool_limit: 'The question needed too many lookups. Ask a narrower question.',
  copilot_model_limit: 'The question needed too many model calls. Ask a narrower question.',
  copilot_retry_limit: 'The model is busy. Try again in a moment.',
  copilot_input_token_limit: 'The question and its context are too large. Ask a narrower question.',
  copilot_output_token_limit: 'The answer would be too long. Ask a narrower question.',
  copilot_timeout: 'The copilot took too long to answer. Try again.',
  copilot_point_limit: 'This case has too many open points to summarise.',
  copilot_context_invalid: 'The case context could not be prepared. Try again.',
  copilot_input_not_approved: 'The copilot is not approved for use in this environment.',
  model_manifest_refused: 'The model configuration is not approved. The copilot is unavailable.',
  model_unavailable: 'The governed model is unavailable. Try again later.',
  model_response_invalid: 'The model returned an answer that could not be accepted. Nothing is shown.',
  citation_invalid: 'The model cited a record it did not read. Nothing is shown.',
  audit_unavailable: 'The audit trail is unavailable, so nothing was done. Try again later.',
  confirmation_not_found: 'This confirmation is not available.',
  confirmation_expired: 'This confirmation has expired. Ask again to create a new proposal.',
  confirmation_state_conflict: 'This confirmation was already decided differently.',
  confirmation_required: 'Approve the proposal before releasing.',
  confirmation_declined: 'This proposal was declined.',
  confirmation_cancelled: 'This proposal was cancelled.',
  confirmation_mismatch: 'The release does not match the approved proposal.',
  replay_mismatch: 'This proposal was already used for a different release.',
  stale_revision: 'The case changed since the proposal was made. Ask again to create a new proposal.',
  already_released: 'This aircraft is already released.',
  release_blocked: 'Release is blocked until every gate is passed.',
  release_unavailable: 'The release could not be recorded. Try again later.',
  unsupported_media_type: 'The request was not sent as JSON.',
  network_error: 'The server could not be reached. Try again.',
};

export const copilotMessage = (code: string) => messages[code] ?? `The request failed (${code}). Try again.`;

const blockerWords: Record<string, string> = {
  technical_review_not_accepted: 'The technical review is not accepted.',
  reserve_not_approved: 'The maintenance reserve is not approved.',
  obligations_not_signed_off: 'The return obligations are not signed off.',
};

export const blockerMessage = (code: string) => blockerWords[code] ?? `Gate not passed: ${code}.`;
