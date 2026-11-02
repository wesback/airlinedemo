import type { CaseRecord } from './api';

export const ARRIVAL_SOURCE_IDS = ['technical', 'maintenance', 'finance', 'records', 'contracts'] as const;
export type ArrivalSourceId = typeof ARRIVAL_SOURCE_IDS[number];

export type ArrivalStatus = {
  contractVersion: 1;
  caseId: string;
  caseRevision: number;
  state: 'not_started' | 'collecting' | 'ready';
  missingSources: ArrivalSourceId[];
  canComplete: boolean;
};

export type ArrivalSourceSummary = {
  sourceId: ArrivalSourceId;
  mode: 'available' | 'unavailable' | 'stale' | 'conflicting';
  capabilityLabel: 'Simulated';
  coverage: boolean;
  recordCount?: number;
  pinnedInBasis?: boolean;
  recordsInUse?: { sourceId: ArrivalSourceId; recordId: string; recordVersion: number }[];
};

export type SourceSummary = {
  contractVersion: 1;
  caseId: string;
  caseRevision: number;
  sources: ArrivalSourceSummary[];
};

export type ArrivalRetrievalReceipt = {
  contractVersion: 1;
  commandId: string;
  caseId: string;
  sourceId: ArrivalSourceId;
  outcome: 'pinned' | 'empty' | 'stale' | 'conflicting';
  priorRevision: number;
  newRevision: number;
  auditEventId: string;
  recordRefs: { sourceId: ArrivalSourceId; recordId: string; recordVersion: number }[];
};

export type SourceRecord = {
  sourceId: ArrivalSourceId;
  recordId: string;
  recordVersion: number;
  asOf: string;
  caseId: string;
  kind: string;
  stale: boolean;
  payload: { fields: Record<string, string> };
  synthetic: boolean;
};

export class ArrivalApiError extends Error {
  constructor(public status: number, public code: string | null) {
    super(code ?? `Request failed (${status})`);
  }
}

async function requestJson<T>(url: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(url, init);
  if (!response.ok) {
    let code: string | null = null;
    try {
      const payload = await response.json() as { error?: { code?: unknown } };
      if (typeof payload?.error?.code === 'string') code = payload.error.code;
    } catch {
      // Some authentication and proxy failures have no JSON body.
    }
    throw new ArrivalApiError(response.status, code);
  }
  return response.json() as Promise<T>;
}

const casePath = (caseId: string) => `/api/v1/cases/${encodeURIComponent(caseId)}`;

export async function getArrivalStatus(caseId: string, signal: AbortSignal): Promise<ArrivalStatus> {
  const status = await requestJson<ArrivalStatus>(`${casePath(caseId)}/arrival`, { signal });
  if (status.contractVersion !== 1 || status.caseId !== caseId
    || !Number.isSafeInteger(status.caseRevision) || !Array.isArray(status.missingSources)
    || !['not_started', 'collecting', 'ready'].includes(status.state)
    || typeof status.canComplete !== 'boolean'
    || status.missingSources.some((source, index) => !ARRIVAL_SOURCE_IDS.includes(source)
      || (index > 0 && ARRIVAL_SOURCE_IDS.indexOf(status.missingSources[index - 1]) >= ARRIVAL_SOURCE_IDS.indexOf(source)))) {
    throw new Error('Malformed arrival status contract');
  }
  return status;
}

export async function getSourceSummary(caseId: string, signal: AbortSignal): Promise<SourceSummary> {
  const summary = await requestJson<SourceSummary>(`${casePath(caseId)}/sources`, { signal });
  if (summary.contractVersion !== 1 || summary.caseId !== caseId
    || !Number.isSafeInteger(summary.caseRevision) || !Array.isArray(summary.sources)
    || summary.sources.length !== ARRIVAL_SOURCE_IDS.length) {
    throw new Error('Malformed source summary contract');
  }

  summary.sources.forEach((source, index) => {
    if (source.sourceId !== ARRIVAL_SOURCE_IDS[index]
      || source.capabilityLabel !== 'Simulated'
      || !['available', 'unavailable', 'stale', 'conflicting'].includes(source.mode)
      || typeof source.coverage !== 'boolean'
      || (source.recordCount !== undefined && (!Number.isSafeInteger(source.recordCount) || source.recordCount < 0))) {
      throw new Error('Malformed source summary contract');
    }
  });
  return summary;
}

export async function getCaseRevision(caseId: string, signal: AbortSignal): Promise<number> {
  const record = await requestJson<CaseRecord>(`/api/cases/${encodeURIComponent(caseId)}`, { signal });
  if (record.caseId !== caseId || !Number.isSafeInteger(record.revision)) throw new Error('Malformed case contract');
  return record.revision;
}

export async function loadArrivalMetadata(caseId: string, signal: AbortSignal) {
  const [caseRevision, status, summary] = await Promise.all([
    getCaseRevision(caseId, signal),
    getArrivalStatus(caseId, signal),
    getSourceSummary(caseId, signal),
  ]);
  if (caseRevision !== status.caseRevision || caseRevision !== summary.caseRevision) {
    throw new Error('Arrival metadata changed while it was loading');
  }
  return { status, summary };
}

function commandBody(expectedRevision: number) {
  return { commandId: `ui-${crypto.randomUUID()}`, expectedRevision };
}

export function startArrival(caseId: string, csrfToken: string, expectedRevision: number, signal: AbortSignal) {
  return requestJson(`${casePath(caseId)}/arrival/start`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
    body: JSON.stringify(commandBody(expectedRevision)),
    signal,
  });
}

export function retrieveArrivalSource(
  caseId: string, sourceId: ArrivalSourceId, csrfToken: string, expectedRevision: number, signal: AbortSignal,
) {
  return requestJson<ArrivalRetrievalReceipt>(`${casePath(caseId)}/arrival/sources/${sourceId}/retrieve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
    body: JSON.stringify(commandBody(expectedRevision)),
    signal,
  });
}

export function completeArrival(caseId: string, csrfToken: string, expectedRevision: number, signal: AbortSignal) {
  return requestJson(`${casePath(caseId)}/arrival/complete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrfToken },
    body: JSON.stringify(commandBody(expectedRevision)),
    signal,
  });
}

export async function listSourceRecords(caseId: string, sourceId: ArrivalSourceId, signal: AbortSignal) {
  const payload = await requestJson<{ caseId: string; sourceId: ArrivalSourceId; records: SourceRecord[] }>(
    `${casePath(caseId)}/sources/${sourceId}/records`, { signal });
  if (payload.caseId !== caseId || payload.sourceId !== sourceId || !Array.isArray(payload.records)) {
    throw new Error('Malformed source-record contract');
  }
  return payload.records;
}
