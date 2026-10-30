import { useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, caseUrl, postJson, type CaseHistory, type CaseRecord, type ObligationRecord, type Readiness, type ReserveDraft, type Session, type TechnicalReviews } from './api';
import { actorName, aircraftFor, formatWhen, PERSONAS, STEPS, type CaseProgress, type StepDefinition } from './model';
import { ActorGlyph, CheckIcon, DocumentIcon } from './Art';
import { aiBriefFor } from './aiBriefs';
import { AiBriefView, WhyReview } from './AiInsight';

export type JourneyData = {
  record: CaseRecord;
  progress: CaseProgress;
  history: CaseHistory | null;
  reviews: TechnicalReviews | null;
  reserve: ReserveDraft | null;
  obligations: ObligationRecord | null;
  readiness: Readiness | null;
};

export type PanelProps = {
  step: StepDefinition;
  data: JourneyData;
  session: Session;
  onChanged: () => Promise<void> | void;
  onSwitchPersona: (userId: string) => void;
};

export function StepPanel(props: PanelProps) {
  switch (props.step.id) {
    case 'arrival': return <ArrivalPanel {...props} />;
    case 'intake': return <IntakePanel {...props} />;
    case 'investigation': return <InvestigationPanel {...props} />;
    case 'technical': return <TechnicalPanel {...props} />;
    case 'finance': return <FinancePanel {...props} />;
    case 'legal': return <LegalPanel {...props} />;
    default: return <ReleasePanel {...props} />;
  }
}

function ArrivalPanel({ data }: PanelProps) {
  const aircraft = aircraftFor(data.record.caseId);
  return (
    <>
      <Outcome tone="done">Joined the return pool. Return conditions and the redelivery target are on file.</Outcome>
      <dl className="av-facts">
        <Fact label="Aircraft" value={`${aircraft.manufacturer} ${aircraft.model}`} />
        <Fact label="Registration" value={aircraft.registration} />
        <Fact label="Serial number" value={aircraft.msn} />
        <Fact label="Engines" value={aircraft.engines} />
        <Fact label="Seats" value={aircraft.seats ? String(aircraft.seats) : 'Not recorded'} />
        <Fact label="Lease return" value={aircraft.returnDate} />
        <Fact label="Outgoing lessee" value={aircraft.outgoingLessee} />
        <Fact label="Next lessee" value={aircraft.nextLessee} />
      </dl>
    </>
  );
}

function documentsOf(history: CaseHistory | null) {
  const seen = new Map<string, { documentId: string; version: number; pages: Set<number> }>();
  for (const event of history?.events ?? []) {
    for (const witness of event.witnesses) {
      const entry = seen.get(witness.documentId) ?? { documentId: witness.documentId, version: witness.documentVersion, pages: new Set<number>() };
      entry.pages.add(witness.page);
      seen.set(witness.documentId, entry);
    }
  }
  return [...seen.values()];
}

export function documentTitle(documentId: string) {
  const name = documentId.split('/').pop() ?? documentId;
  const base = name.replace(/\.[a-z0-9]+$/i, '').replace(/[-_]+/g, ' ');
  return base.charAt(0).toUpperCase() + base.slice(1);
}

function IntakePanel({ data }: PanelProps) {
  const basis = data.record.basis;
  const documents = documentsOf(data.history);
  if (basis.length === 0) {
    return <Empty title="Waiting for the records package">The outgoing lessee has not uploaded the return records yet. Intake runs automatically as soon as they arrive.</Empty>;
  }
  return (
    <>
      <Outcome tone="done">Records package received and pinned as evidence basis version {basis[basis.length - 1].version}.</Outcome>
      {documents.length > 0 && (
        <>
          <h3 className="av-panel-sub">Documents in the package</h3>
          <ul className="av-docs">
            {documents.map((doc) => (
              <li key={doc.documentId}>
                <DocumentIcon />
                <span><strong>{documentTitle(doc.documentId)}</strong><span>Version {doc.version}, cited on page {[...doc.pages].join(', ')}</span></span>
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  );
}

function InvestigationPanel({ data }: PanelProps) {
  const events = (data.history?.events ?? []).filter((event) => event.capabilityLabel === 'AI agent');
  if (events.length === 0) {
    return (
      <Empty title={data.record.basis.length ? 'The records agent is queued' : 'Starts after intake'}>
        The records agent reads every document, cross-checks identities and life-limited parts, and cites its sources. It never makes a decision on its own.
      </Empty>
    );
  }
  const brief = aiBriefFor(data.record.caseId, data.history);
  return (
    <>
      <Outcome tone="ai">The records agent finished and handed its findings to a technical reviewer.</Outcome>
      {brief && <AiBriefView brief={brief} trace={events} />}
      <p className="av-footnote">Recorded agent run. A live run needs an approved model manifest in this environment.</p>
    </>
  );
}

function TechnicalPanel(props: PanelProps) {
  const { data, session, step } = props;
  const current = data.reviews?.decisions.find((decision) => decision.current);
  const state = data.progress.states.technical;
  const ready = data.progress.states.investigation === 'done';
  const brief = aiBriefFor(data.record.caseId, data.history);
  return (
    <>
      {ready && brief && <WhyReview brief={brief} />}
      {current ? (
        <Decision accepted={current.decision === 'accept'} who={actorName(current.actor ?? 'reviewer')} at={current.at}
          verb={current.decision === 'accept' ? 'Accepted the AI findings' : 'Rejected the AI findings'} reason={current.reason} />
      ) : data.progress.technical && (
        <Decision accepted={data.progress.technical === 'accept'} who="Technical reviewer" at={null}
          verb={data.progress.technical === 'accept' ? 'Accepted the AI findings' : 'Rejected the AI findings'} reason={null} />
      )}
      {!ready && <Empty title="Waiting for the AI findings">The technical review opens once the records agent has raised its findings.</Empty>}
      {ready && state !== 'done' && (
        session.role === step.ownerRole ? (
          <DecisionForm
            onStale={props.onChanged}
            title="Your decision"
            hint="Check the findings against the cited pages, then record your decision. The case moves on only if you accept."
            options={[{ value: 'accept', label: 'Accept findings', tone: 'primary' }, { value: 'reject', label: 'Reject findings', tone: 'danger' }]}
            onSubmit={async (decision, reason) => {
              await postJson(caseUrl(data.record.caseId, '/technical-reviews'), session.csrfToken, {
                expectedRevision: data.record.revision,
                expectedBasisVersion: data.reviews?.basisVersion ?? data.record.basis[data.record.basis.length - 1]?.version ?? 0,
                decision,
                reason,
              });
              await props.onChanged();
            }}
          />
        ) : <HandOff {...props} />
      )}
    </>
  );
}

const money = new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', minimumFractionDigits: 2 });

function FinancePanel(props: PanelProps) {
  const { data, session, step } = props;
  const reserve = data.reserve;
  const state = data.progress.states.finance;
  const ready = data.progress.technical === 'accept';
  return (
    <>
      {data.progress.finance && (
        <Decision accepted={data.progress.finance === 'approve'} who="Finance reviewer" at={null}
          verb={data.progress.finance === 'approve' ? 'Approved the maintenance reserve' : 'Returned the reserve for correction'} reason={null} />
      )}
      {reserve ? (
        <div className="av-reserve">
          <div className="av-reserve-head">
            <span><ActorGlyph actor="rules" size={14} /> Calculated by contract rules for {reserve.period}</span>
            <strong>{reserve.payableTotal !== null ? money.format(reserve.payableTotal) : 'Not calculable'}</strong>
          </div>
          <table className="av-table">
            <thead><tr><th scope="col">Contract line</th><th scope="col">Units</th><th scope="col">Rate</th><th scope="col">Amount</th></tr></thead>
            <tbody>
              {reserve.lines.map((line) => (
                <tr key={line.contractLine}>
                  <th scope="row">{line.contractLine}</th>
                  <td>{line.units}</td>
                  <td>{money.format(line.unitRate)}</td>
                  <td>{money.format(line.amount)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {reserve.blockers.length > 0 && (
            <ul className="av-issues">{reserve.blockers.map((blocker) => <li key={blocker.code}>{blocker.detail}</li>)}</ul>
          )}
          <p className="av-footnote">{reserve.calculationBasis}</p>
        </div>
      ) : (
        <Empty title="No reserve worksheet">The finance view of this aircraft is not available to your persona.</Empty>
      )}
      {!ready && <Empty title="Waiting for technical review">Finance starts once a technical reviewer has accepted the findings.</Empty>}
      {ready && state !== 'done' && reserve && (
        session.role === step.ownerRole ? (
          <DecisionForm
            onStale={props.onChanged}
            title="Your decision"
            hint="Approve the reserve to pass the aircraft to legal, or return it with a reason."
            options={[{ value: 'approve', label: 'Approve reserve', tone: 'primary' }, { value: 'return', label: 'Return for correction', tone: 'danger' }]}
            onSubmit={async (decision, reason) => {
              await postJson(caseUrl(data.record.caseId, '/reserve/decisions'), session.csrfToken, { expectedRevision: data.record.revision, decision, reason });
              await props.onChanged();
            }}
          />
        ) : <HandOff {...props} />
      )}
    </>
  );
}

const REASONS: Record<string, string> = {
  source_basis_missing: 'No evidence basis has been recorded for this aircraft yet.',
  source_basis_invalid: 'The evidence basis is not yet pinned to the contract source records.',
  source_conflict: 'Two sources disagree about this obligation.',
  source_stale: 'The evidence is older than the current records.',
  source_unavailable: 'A source record could not be read.',
  contracts_record_missing: 'The lease contract record is missing.',
  contracts_clause_invalid: 'The return clause in the lease could not be read.',
  notice_window_start_missing_or_invalid: 'The notice window start date is missing.',
  notice_window_end_missing_or_invalid: 'The notice window end date is missing.',
};
const OBLIGATION_STATES = ['Blocked', 'Needs review', 'At risk', 'Evidenced', 'Accepted'];

function LegalPanel(props: PanelProps) {
  const { data, session, step } = props;
  const obligations = data.obligations;
  const ready = data.progress.finance === 'approve' && data.progress.technical === 'accept';
  const draft = obligations?.drafts.find((item) => item.status === 0 || item.status === 'Current');
  const stateLabel = obligations ? (typeof obligations.state === 'number' ? OBLIGATION_STATES[obligations.state] : String(obligations.state)) : null;
  return (
    <>
      {data.progress.legal && (
        <Decision accepted={data.progress.legal === 'signed-off'} who="Legal reviewer" at={null}
          verb={data.progress.legal === 'signed-off' ? 'Signed off the return obligations' : 'Rejected the return obligations'} reason={null} />
      )}
      {obligations ? (
        <div className="av-obligation">
          <div className="av-reserve-head">
            <span><ActorGlyph actor="rules" size={14} /> Obligations evaluated by contract rules</span>
            <span className={`av-pill av-pill-${stateLabel === 'Accepted' || stateLabel === 'Evidenced' ? 'green' : 'alert'}`}>{stateLabel}</span>
          </div>
          {obligations.reasons.length > 0 && (
            <ul className="av-issues">{obligations.reasons.map((reason) => <li key={reason}>{REASONS[reason] ?? reason.replace(/_/g, ' ')}</li>)}</ul>
          )}
          {draft && obligations.reasons.length > 0 && (
            <p className="av-footnote">The rules could not confirm every item, so the decision rests with the legal reviewer: sign off with a reason, or reject.</p>
          )}
          <p className="av-footnote">No notice is ever sent automatically. A legal reviewer signs off a current notice draft.</p>
        </div>
      ) : (
        <Empty title="Obligations not available">The legal view of this aircraft is not available to your persona.</Empty>
      )}
      {!ready && <Empty title="Waiting for finance">Legal review starts once the maintenance reserve is approved.</Empty>}
      {ready && data.progress.states.legal !== 'done' && (
        session.role !== step.ownerRole ? <HandOff {...props} /> : draft ? (
          <DecisionForm
            onStale={props.onChanged}
            title="Your sign-off"
            hint="Sign off the notice draft, or reject it with a reason."
            options={[{ value: 'signed-off', label: 'Sign off', tone: 'primary' }, { value: 'rejected', label: 'Reject', tone: 'danger' }]}
            onSubmit={async (decision, reason) => {
              await postJson(caseUrl(data.record.caseId, `/obligations/${encodeURIComponent(draft.obligationId)}/decisions`), session.csrfToken,
                { expectedRevision: data.record.revision, decision, reason });
              await props.onChanged();
            }}
          />
        ) : (
          <Outcome tone="alert">Sign-off is blocked until the rules produce a current notice draft. Resolve the items above first.</Outcome>
        )
      )}
    </>
  );
}

function ReleasePanel(props: PanelProps) {
  const { data, session, step } = props;
  const gates = STEPS.filter((item) => item.id !== 'release');
  const allDone = gates.every((item) => data.progress.states[item.id] === 'done');
  const aircraft = aircraftFor(data.record.caseId);
  const release = [...data.record.audit].reverse().find((item) => item.action === 'aircraft.released');
  return (
    <>
      {release ? (
        <>
          <Outcome tone="done">{aircraft.registration} is released for redelivery to {aircraft.nextLessee}.</Outcome>
          <Decision accepted who="Transition lead" at={release.at} verb={`Released to ${aircraft.nextLessee}`} reason={noteOf(release.detail)} />
        </>
      ) : allDone
        ? <Outcome tone="done">Every gate is passed. {aircraft.registration} is ready for redelivery to {aircraft.nextLessee}.</Outcome>
        : <Outcome tone="pending">Release opens when every gate below is passed.</Outcome>}
      <ul className="av-checklist">
        {gates.map((item) => {
          const state = data.progress.states[item.id];
          return (
            <li key={item.id} className={`is-${state}`}>
              <span className="av-checklist-box">{state === 'done' ? <CheckIcon /> : null}</span>
              <span>{item.title}</span>
              <span className="av-checklist-state">{state === 'done' ? 'Passed' : state === 'returned' ? 'Returned' : state === 'current' ? 'In progress' : 'Not started'}</span>
            </li>
          );
        })}
      </ul>
      {data.readiness && !release && <p className="av-footnote">{data.readiness.sentence}</p>}
      {allDone && !release && (
        session.role !== step.ownerRole ? <HandOff {...props} /> : (
          <ReleaseForm
            nextLessee={aircraft.nextLessee}
            onStale={props.onChanged}
            onSubmit={async (note) => {
              await postJson(caseUrl(data.record.caseId, '/release'), session.csrfToken, { expectedRevision: data.record.revision, note });
              await props.onChanged();
            }}
          />
        )
      )}
    </>
  );
}

function noteOf(detail: string): string | null {
  try {
    const parsed = JSON.parse(detail) as { note?: unknown };
    return typeof parsed.note === 'string' ? parsed.note : null;
  } catch {
    return null;
  }
}

function ReleaseForm({ nextLessee, onSubmit, onStale }: { nextLessee: string; onSubmit: (note: string) => Promise<void>; onStale: () => Promise<void> | void }) {
  const [note, setNote] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!note.trim()) { setError('Add a short handover note. It is kept in the audit trail.'); return; }
    setBusy(true);
    setError('');
    try {
      await onSubmit(note.trim());
    } catch (caught) {
      if (caught instanceof ApiError && caught.status === 409) await onStale();
      setError(caught instanceof Error ? caught.message : 'The release could not be saved.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="av-decision-form" onSubmit={submit} aria-label="Release the aircraft">
      <div className="av-decision-form-head">
        <ActorGlyph actor="human" size={18} />
        <div><strong>Your release decision</strong><p>Releasing is simulated: it is recorded in the audit trail and nothing is sent to the lessee.</p></div>
      </div>
      <label className="av-field">
        <span>Handover note</span>
        <textarea rows={3} value={note} maxLength={500} onChange={(event) => setNote(event.target.value)} placeholder="Anything the next lessee should know?" />
      </label>
      {error && <p className="av-notice av-notice-alert" role="alert">{error}</p>}
      <button className="av-button av-button-primary" type="submit" disabled={busy}>{busy ? 'Releasing…' : `Release to ${nextLessee}`}</button>
    </form>
  );
}

function HandOff({ step, onSwitchPersona }: PanelProps) {
  const persona = PERSONAS.find((item) => item.role === step.ownerRole);
  return (
    <div className="av-handoff">
      <span className={`av-handoff-icon`}><ActorGlyph actor="human" size={20} /></span>
      <div>
        <strong>Waiting for the {step.owner.toLowerCase()}</strong>
        <p>This is a human-in-the-loop gate. Only the {step.owner.toLowerCase()} can record this decision.</p>
      </div>
      {persona && <button type="button" className="av-button av-button-secondary" onClick={() => onSwitchPersona(persona.id)}>Continue as {persona.label.toLowerCase()}</button>}
    </div>
  );
}

type Option = { value: string; label: string; tone: 'primary' | 'danger' };

function DecisionForm({ title, hint, options, onSubmit, onStale }: { title: string; hint: string; options: Option[]; onSubmit: (decision: string, reason: string) => Promise<void>; onStale: () => Promise<void> | void }) {
  const [decision, setDecision] = useState(options[0].value);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!reason.trim()) { setError('Add a short reason. It is kept in the audit trail.'); return; }
    setBusy(true);
    setError('');
    try {
      await onSubmit(decision, reason.trim());
    } catch (caught) {
      if (caught instanceof ApiError && caught.status === 409) await onStale();
      setError(caught instanceof ApiError && caught.status === 409
        ? 'Someone changed this case a moment ago. The latest version has been loaded; check it and decide again.'
        : caught instanceof Error ? caught.message : 'The decision could not be saved.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="av-decision-form" onSubmit={submit}>
      <div className="av-decision-form-head">
        <ActorGlyph actor="human" size={18} />
        <div><strong>{title}</strong><p>{hint}</p></div>
      </div>
      <fieldset className="av-segmented">
        <legend className="av-visually-hidden">Decision</legend>
        {options.map((option) => (
          <label key={option.value} className={`av-segment av-segment-${option.tone}${decision === option.value ? ' is-selected' : ''}`}>
            <input type="radio" name="decision" value={option.value} checked={decision === option.value} onChange={() => setDecision(option.value)} />
            {option.label}
          </label>
        ))}
      </fieldset>
      <label className="av-field">
        <span>Reason</span>
        <textarea rows={3} value={reason} maxLength={500} onChange={(event) => setReason(event.target.value)} placeholder="What did you check?" />
      </label>
      {error && <p className="av-notice av-notice-alert" role="alert">{error}</p>}
      <button className="av-button av-button-primary" type="submit" disabled={busy}>{busy ? 'Saving…' : 'Record decision'}</button>
    </form>
  );
}

function Decision({ accepted, who, at, verb, reason }: { accepted: boolean; who: string; at: string | null | undefined; verb: string; reason: string | null | undefined }) {
  return (
    <div className={`av-decision ${accepted ? 'is-accepted' : 'is-returned'}`}>
      <span className="av-decision-icon"><ActorGlyph actor="human" size={18} /></span>
      <div>
        <strong>{verb}</strong>
        <p>{who}{at ? `, ${formatWhen(at)}` : ''}</p>
        {reason && <blockquote>{reason}</blockquote>}
      </div>
    </div>
  );
}

function Outcome({ tone, children }: { tone: 'done' | 'ai' | 'pending' | 'alert'; children: ReactNode }) {
  return (
    <p className={`av-outcome av-outcome-${tone}`}>
      {tone === 'done' ? <CheckIcon /> : tone === 'ai' ? <ActorGlyph actor="ai" size={14} /> : null}
      {children}
    </p>
  );
}

function Empty({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="av-empty">
      <strong>{title}</strong>
      <p>{children}</p>
    </div>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return <div><dt>{label}</dt><dd>{value}</dd></div>;
}
