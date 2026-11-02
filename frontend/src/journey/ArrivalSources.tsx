import { useEffect, useRef, useState } from 'react';
import type { Session } from './api';
import {
  ARRIVAL_SOURCE_IDS, ArrivalApiError, completeArrival, listSourceRecords, loadArrivalMetadata,
  retrieveArrivalSource, startArrival, type ArrivalRetrievalReceipt, type ArrivalSourceId,
  type ArrivalSourceSummary, type ArrivalStatus, type SourceRecord, type SourceSummary,
} from './arrivalApi';

type Props = {
  caseId: string;
  session: Session;
  onChanged: () => Promise<void> | void;
  onSwitchPersona: (userId: string) => void;
};

type Metadata = { status: ArrivalStatus; summary: SourceSummary };
type Feedback = { message: string; alert?: boolean };

const PERSONA_FOR_SOURCE: Record<ArrivalSourceId, { id: string; label: string }> = {
  technical: { id: 'reviewer', label: 'Technical reviewer' },
  maintenance: { id: 'reviewer', label: 'Technical reviewer' },
  finance: { id: 'fin-1', label: 'Finance reviewer' },
  records: { id: 'reviewer', label: 'Technical reviewer' },
  contracts: { id: 'legal-1', label: 'Legal reviewer' },
};

const RETRIEVAL_OUTCOME: Record<ArrivalRetrievalReceipt['outcome'], string> = {
  pinned: 'Records retrieved and pinned.',
  empty: 'No records were available from this source.',
  stale: 'The source records are stale; nothing was pinned.',
  conflicting: 'The source records conflict; nothing was pinned.',
};

function readError(error: unknown): string {
  if (!(error instanceof ArrivalApiError)) return 'The source request failed. Refresh metadata before trying again.';
  switch (error.code) {
    case 'forbidden': return 'This source is not permitted for the current persona.';
    case 'source_unavailable': return 'The source is unavailable.';
    case 'source_read_timeout': return 'The source request timed out.';
    case 'source_read_limit': return 'The source read limit was reached.';
    case 'source_record_limit': return 'The source returned more records than the permitted limit.';
    case 'audit_write_failed': return 'The source read could not be recorded; no records were returned.';
    case 'stale_revision': return 'The case changed before this request. Metadata was refreshed; submit again only if still needed.';
    case 'unknown_source': return 'The server did not recognize this source.';
    default: return error.code ? `The source request failed: ${error.code}.` : `The source request failed (${error.status}).`;
  }
}

function SourceRecordView({ sourceId, record }: { sourceId: ArrivalSourceId; record: SourceRecord }) {
  const fields = Object.entries(record.payload?.fields ?? {});
  return (
    <article className="av-source-record">
      <h4>{record.kind}: {record.recordId} v{record.recordVersion}</h4>
      <p className="av-footnote">As of {record.asOf}{record.stale ? ' · stale' : ''}</p>
      {fields.length > 0 && (
        <dl>
          {fields.map(([name, value]) => (
            <div key={name}>
              <dt>{name}</dt>
              <dd>{sourceId === 'maintenance' ? <><strong>Untrusted source data</strong><span>{value}</span></> : value}</dd>
            </div>
          ))}
        </dl>
      )}
    </article>
  );
}

function SourcePanel({ source, status, feedback, records, busy, onRetrieve, onViewRecords, onSwitchPersona }:
  { source: ArrivalSourceSummary; status: ArrivalStatus; feedback?: Feedback; records?: SourceRecord[]; busy: boolean;
    onRetrieve: (sourceId: ArrivalSourceId) => void; onViewRecords: (sourceId: ArrivalSourceId) => void;
    onSwitchPersona: (userId: string) => void }) {
  const permitted = source.recordCount !== undefined;
  const persona = PERSONA_FOR_SOURCE[source.sourceId];
  return (
    <article className="av-source-card" aria-label={`${source.sourceId} source`}>
      <header>
        <h3>{source.sourceId}</h3>
        <span className="av-source-capability">{source.capabilityLabel}</span>
      </header>
      <dl className="av-source-facts">
        <div><dt>Mode</dt><dd>{source.mode}</dd></div>
        <div><dt>Coverage</dt><dd>{source.coverage ? 'Covered' : 'Not covered'}</dd></div>
      </dl>
      {!permitted ? (
        <div className="av-source-permission">
          <strong>Not permitted</strong>
          {status.state !== 'ready' && status.state !== 'not_started' && (
            <p>Switch to {persona.label} to request this source.</p>
          )}
          {status.state !== 'ready' && status.state !== 'not_started' && (
            <button type="button" className="av-button av-button-secondary av-button-small" onClick={() => onSwitchPersona(persona.id)}>
              Continue as {persona.label}
            </button>
          )}
        </div>
      ) : (
        <>
          <p className="av-source-count">{source.recordCount} records in this source</p>
          <div className="av-source-actions">
            <button type="button" className="av-button av-button-secondary av-button-small"
              disabled={busy || status.state !== 'collecting'} onClick={() => onRetrieve(source.sourceId)}>
              Retrieve and pin records
            </button>
            <button type="button" className="av-button av-button-ghost av-button-small"
              disabled={busy || status.state === 'not_started'} onClick={() => onViewRecords(source.sourceId)}>
              View records
            </button>
          </div>
          {feedback && <p className={`av-source-feedback${feedback.alert ? ' av-source-feedback-alert' : ''}`} role={feedback.alert ? 'alert' : 'status'}>{feedback.message}</p>}
          {records && (records.length === 0
            ? <p className="av-source-feedback" role="status">No records were returned.</p>
            : <div className="av-source-records">{records.map((record) =>
              <SourceRecordView key={`${record.recordId}:${record.recordVersion}`} sourceId={source.sourceId} record={record} />)}</div>)}
        </>
      )}
    </article>
  );
}

export default function ArrivalSources({ caseId, session, onChanged, onSwitchPersona }: Props) {
  const [metadata, setMetadata] = useState<Metadata | null>(null);
  const [metadataUnavailable, setMetadataUnavailable] = useState(false);
  const [busy, setBusy] = useState(false);
  const [feedback, setFeedback] = useState<Partial<Record<ArrivalSourceId, Feedback>>>({});
  const [records, setRecords] = useState<Partial<Record<ArrivalSourceId, SourceRecord[]>>>({});
  const [arrivalMessage, setArrivalMessage] = useState('');
  const controller = useRef<AbortController | null>(null);
  const onChangedRef = useRef(onChanged);
  onChangedRef.current = onChanged;

  async function refreshMetadata(signal: AbortSignal): Promise<boolean> {
    try {
      const next = await loadArrivalMetadata(caseId, signal);
      if (signal.aborted) return false;
      setMetadata(next);
      setMetadataUnavailable(false);
      return true;
    } catch {
      if (signal.aborted) return false;
      setMetadata(null);
      setMetadataUnavailable(true);
      return false;
    }
  }

  async function refreshAfterRequest(signal: AbortSignal) {
    let caseRefreshSucceeded = true;
    try {
      await onChangedRef.current();
    } catch {
      caseRefreshSucceeded = false;
    }
    const metadataRefreshSucceeded = await refreshMetadata(signal);
    if (!caseRefreshSucceeded || !metadataRefreshSucceeded) {
      setMetadata(null);
      setMetadataUnavailable(true);
    }
  }

  useEffect(() => {
    const active = new AbortController();
    controller.current = active;
    setMetadata(null);
    setMetadataUnavailable(false);
    setBusy(false);
    setFeedback({});
    setRecords({});
    setArrivalMessage('');
    void refreshMetadata(active.signal);
    return () => {
      active.abort();
      if (controller.current === active) controller.current = null;
    };
  }, [caseId, session.id]);

  function beginRequest() {
    controller.current?.abort();
    const active = new AbortController();
    controller.current = active;
    setBusy(true);
    return active;
  }

  async function finishRequest(active: AbortController) {
    if (active.signal.aborted) return;
    await refreshAfterRequest(active.signal);
    if (!active.signal.aborted) setBusy(false);
  }

  async function start() {
    if (!metadata || busy) return;
    const active = beginRequest();
    setArrivalMessage('');
    try {
      await startArrival(caseId, session.csrfToken, metadata.status.caseRevision, active.signal);
      if (active.signal.aborted) return;
      setArrivalMessage('Arrival started. Request records from each permitted source.');
    } catch (error) {
      if (active.signal.aborted) return;
      setArrivalMessage(readError(error));
    } finally {
      await finishRequest(active);
    }
  }

  async function complete() {
    if (!metadata || !metadata.status.canComplete || busy) return;
    const active = beginRequest();
    setArrivalMessage('');
    try {
      await completeArrival(caseId, session.csrfToken, metadata.status.caseRevision, active.signal);
      if (active.signal.aborted) return;
      setArrivalMessage('Arrival complete. The case is ready for records intake.');
    } catch (error) {
      if (active.signal.aborted) return;
      setArrivalMessage(readError(error));
    } finally {
      await finishRequest(active);
    }
  }

  async function retrieve(sourceId: ArrivalSourceId) {
    if (!metadata || busy || metadata.status.state !== 'collecting') return;
    const active = beginRequest();
    setFeedback((previous) => ({ ...previous, [sourceId]: undefined }));
    try {
      const receipt = await retrieveArrivalSource(caseId, sourceId, session.csrfToken,
        metadata.status.caseRevision, active.signal);
      if (active.signal.aborted) return;
      setFeedback((previous) => ({ ...previous, [sourceId]: { message: RETRIEVAL_OUTCOME[receipt.outcome] } }));
    } catch (error) {
      if (active.signal.aborted) return;
      setFeedback((previous) => ({ ...previous, [sourceId]: { message: readError(error), alert: true } }));
    } finally {
      await finishRequest(active);
    }
  }

  async function viewRecords(sourceId: ArrivalSourceId) {
    if (!metadata || busy || metadata.status.state === 'not_started') return;
    const active = beginRequest();
    setRecords((previous) => ({ ...previous, [sourceId]: undefined }));
    setFeedback((previous) => ({ ...previous, [sourceId]: undefined }));
    try {
      const result = await listSourceRecords(caseId, sourceId, active.signal);
      if (active.signal.aborted) return;
      setRecords((previous) => ({ ...previous, [sourceId]: result }));
      if (result.length === 0) setFeedback((previous) => ({ ...previous, [sourceId]: { message: 'No records were returned.' } }));
    } catch (error) {
      if (active.signal.aborted) return;
      setFeedback((previous) => ({ ...previous, [sourceId]: { message: readError(error), alert: true } }));
    } finally {
      await finishRequest(active);
    }
  }

  async function retryMetadata() {
    if (busy) return;
    const active = beginRequest();
    await refreshAfterRequest(active.signal);
    if (!active.signal.aborted) setBusy(false);
  }

  const sourcePanels = metadata?.summary.sources ?? [];
  const sourceOrderValid = sourcePanels.length === ARRIVAL_SOURCE_IDS.length
    && sourcePanels.every((source, index) => source.sourceId === ARRIVAL_SOURCE_IDS[index]);
  const status = metadata?.status;

  return (
    <section className="av-arrival-sources" aria-labelledby="arrival-sources-heading">
      <div className="av-arrival-sources-heading">
        <div>
          <h3 id="arrival-sources-heading">Governed source access</h3>
          <p>Request each source through the case-scoped governance layer. Coverage and mode come from the server.</p>
        </div>
        {status?.state === 'not_started' && session.role === 'presenter' && (
          <button type="button" className="av-button av-button-primary" disabled={busy || metadataUnavailable} onClick={() => void start()}>
            Start arrival
          </button>
        )}
        {status?.canComplete && session.role === 'presenter' && (
          <button type="button" className="av-button av-button-primary" disabled={busy || metadataUnavailable} onClick={() => void complete()}>
            Complete arrival
          </button>
        )}
      </div>
      {status && <p className="av-arrival-status" role="status">
        Arrival status: {status.state.replace('_', ' ')}
        {status.missingSources.length > 0 && ` · Missing: ${status.missingSources.join(', ')}`}
      </p>}
      {status?.state === 'ready' && <p className="av-outcome av-outcome-done">Arrival complete. The case is ready for records intake.</p>}
      {arrivalMessage && <p className="av-notice" role="status">{arrivalMessage}</p>}
      {metadataUnavailable && (
        <div className="av-notice av-notice-alert" role="alert">
          Arrival metadata is unavailable. Source actions are disabled until it can be refreshed.
          <button type="button" className="av-button av-button-secondary av-button-small" disabled={busy} onClick={() => void retryMetadata()}>
            Retry metadata
          </button>
        </div>
      )}
      {!metadataUnavailable && status && !sourceOrderValid && (
        <p className="av-notice av-notice-alert" role="alert">The source summary is invalid. No source actions are available.</p>
      )}
      {!metadataUnavailable && sourceOrderValid && status && (
        <div className="av-source-grid">
          {sourcePanels.map((source) => (
            <SourcePanel key={source.sourceId} source={source} status={status} busy={busy}
              feedback={feedback[source.sourceId]} records={records[source.sourceId]}
              onRetrieve={(sourceId) => void retrieve(sourceId)}
              onViewRecords={(sourceId) => void viewRecords(sourceId)}
              onSwitchPersona={onSwitchPersona} />
          ))}
        </div>
      )}
      {busy && <p className="av-visually-hidden" role="status">Refreshing case and source metadata…</p>}
    </section>
  );
}
