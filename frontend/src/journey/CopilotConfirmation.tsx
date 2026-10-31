import { useId, useState } from 'react';
import type { Session } from './api';
import { blockerMessage, copilotCaseUrl, copilotMessage, postCopilot, type CopilotConfirmation, type CopilotReceipt } from './copilotApi';

type Props = { session: Session; confirmation: CopilotConfirmation; releaseNote: string };
type Failure = { message: string; blockers: string[] };

const actionLabel = (action: string) => action === 'final-release' ? 'Final simulated release' : action;
const ended: Record<string, string> = {
  declined: 'You declined this release. Nothing changed.',
  cancelled: 'You cancelled this release. Nothing changed.',
  expired: 'This confirmation has expired. Ask again to create a new proposal.',
  consumed: 'This proposal was already used for a release.',
};

/**
 * Trusted confirmation card for a copilot release proposal (FR-009). Only an explicit control posts a decision, and
 * the release retries the frozen tuple (expected revision, release note, proposal) without another model call.
 */
export default function CopilotConfirmationCard({ session, confirmation, releaseNote }: Props) {
  const [state, setState] = useState(confirmation.state);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [receipt, setReceipt] = useState<CopilotReceipt | null>(null);
  const heading = useId();
  const proposalUrl = copilotCaseUrl(confirmation.caseId, `/proposals/${encodeURIComponent(confirmation.proposalId)}/decision`);

  async function decide(decision: 'approve' | 'decline' | 'cancel') {
    setBusy(true);
    setFailure(null);
    const outcome = await postCopilot<{ state: string }>(proposalUrl, session.csrfToken, { decision });
    setBusy(false);
    if (outcome.ok) setState(outcome.value.state);
    else fail(outcome.code, outcome.blockers);
  }

  async function releaseNow() {
    setBusy(true);
    setFailure(null);
    const outcome = await postCopilot<CopilotReceipt>(copilotCaseUrl(confirmation.caseId, '/advance'), session.csrfToken, {
      expectedRevision: confirmation.expectedRevision, releaseNote, proposalId: confirmation.proposalId,
    });
    setBusy(false);
    if (outcome.ok && outcome.value.decision === 'released') { setReceipt(outcome.value); setState('consumed'); }
    else if (outcome.ok) fail('invalid_response', []);
    else fail(outcome.code, outcome.blockers);
  }

  function fail(code: string, blockers: string[]) {
    if (code === 'confirmation_expired') setState('expired');
    setFailure({ message: copilotMessage(code), blockers });
  }

  const status = busy ? 'Sending your decision…' : receipt ? 'Release recorded.' : state === 'approved' ? 'Proposal approved. Nothing is released yet.' : '';

  return (
    <section className="av-copilot-confirmation" aria-labelledby={heading}>
      <h3 id={heading}>Confirm release proposal</h3>
      <p className="av-notice av-copilot-notice">{confirmation.warning}</p>
      <dl className="av-copilot-tuple">
        <dt>Aircraft</dt><dd>{confirmation.aircraftId}</dd>
        <dt>Action</dt><dd>{actionLabel(confirmation.action)}</dd>
        <dt>Expected revision</dt><dd>{confirmation.expectedRevision}</dd>
        <dt>Release note</dt><dd className="av-copilot-text">{confirmation.note}</dd>
        <dt>Expires</dt><dd><time dateTime={confirmation.expiresAt}>{confirmation.expiresAt}</time></dd>
      </dl>
      <p className="av-visually-hidden" role="status" aria-live="polite">{status}</p>
      {state === 'pending' && <div className="av-copilot-actions">
        <button type="button" className="av-button av-button-primary" disabled={busy} onClick={() => void decide('approve')}>Approve</button>
        <button type="button" className="av-button av-button-secondary" disabled={busy} onClick={() => void decide('decline')}>Decline</button>
        <button type="button" className="av-button av-button-ghost" disabled={busy} onClick={() => void decide('cancel')}>Cancel</button>
      </div>}
      {state === 'approved' && <div className="av-copilot-actions">
        <p>Approved. The aircraft is released only when you select Release now.</p>
        <button type="button" className="av-button av-button-primary" disabled={busy} onClick={() => void releaseNow()}>Release now</button>
        <button type="button" className="av-button av-button-ghost" disabled={busy} onClick={() => void decide('cancel')}>Cancel</button>
      </div>}
      {!receipt && ended[state] && <p>{ended[state]}</p>}
      {receipt && <p className="av-notice">{`Released (simulated). Revision ${receipt.revision}, audit entry ${receipt.auditSequence}, proposal ${receipt.proposalId}.`}</p>}
      {failure && <div className="av-notice av-notice-alert" role="alert">
        <p>{failure.message}</p>
        {failure.blockers.length > 0 && <ul>{failure.blockers.map((code) => <li key={code}>{blockerMessage(code)}</li>)}</ul>}
      </div>}
    </section>
  );
}
