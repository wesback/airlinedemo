export type Session = { id: string; displayName: string; role: string; caseScopes: string[]; csrfToken: string };

export class ApiError extends Error {
  constructor(public status: number, message = `Request failed (${status})`) {
    super(message);
  }
}

export async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) throw new ApiError(response.status);
  return response.json() as Promise<T>;
}

export async function postJson<T>(url: string, csrfToken: string, body: unknown): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    let message = '';
    try {
      const payload = await response.json() as { error?: unknown };
      if (typeof payload?.error === 'string') message = payload.error;
    } catch {
      // The body is optional on failures.
    }
    throw new ApiError(response.status, message || `Request failed (${response.status})`);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export type BasisVersion = { version: number; createdAt?: string };
export type AuditEvent = { sequence: number; revision: number; actor: string; action: string; detail: string; at: string };
export type CaseRecord = {
  caseId: string;
  owner?: string;
  revision: number;
  basis: BasisVersion[];
  audit: AuditEvent[];
};

export type JourneyWitness = { witnessId: string; documentId: string; documentVersion: number; page: number };
export type JourneyEvent = {
  eventId: string;
  sequence: number;
  eventType: string;
  actorRole: string;
  summary: string;
  capabilityLabel: string;
  witnesses: JourneyWitness[];
};
export type CaseHistory = { events: JourneyEvent[] };

export type ReserveLine = { contractLine: string; units: number; unitRate: number; amount: number; reportedAmount: number | null };
export type ReserveDraft = {
  draftId: string;
  period: string;
  status: number | string;
  usageReportId: string | null;
  rateReportId: string | null;
  lines: ReserveLine[];
  payableTotal: number | null;
  blockers: { code: string; detail: string }[];
  discrepancies: { code?: string; detail?: string; contractLine?: string }[];
  calculationBasis: string;
};

export type NoticeDraft = { id: string; obligationId: string; status: number | string; body?: string; basisVersion?: number };
export type ObligationRecord = { state: number | string; reasons: string[]; drafts: NoticeDraft[]; newDraft: NoticeDraft | null };

export type Readiness = {
  monthsUntilReturn: number;
  gapsFound: number;
  requestsSent: number;
  heldForReview: number;
  notProcessed: number;
  sentence: string;
};

export type TechnicalReviews = {
  basisVersion: number;
  decisions: { decision: string; current: boolean; reason?: string; actor?: string; at?: string }[];
};

export const caseUrl = (caseId: string, suffix = '') => `/api/cases/${encodeURIComponent(caseId)}${suffix}`;
