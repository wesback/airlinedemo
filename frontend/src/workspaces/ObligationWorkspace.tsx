import { useMemo, useState } from 'react';
import { tokens } from '@fluentui/react-components';
import type { WorklistItem } from './TechnicalWorkspace';

// Permanent text of this screen; the server cannot change it.
const SIMULATION_BANNER = 'Simulated — no notice is sent.';

export type DecisionAction = { id: string; label: string };

export type ObligationRow = { label: string; value: string };
export type ClauseReference = { id: string; text: string };

export type ObligationRecord = {
  id?: string;
  title?: string;
  summary?: string;
  status?: string;
  basisVersion?: number | null;
  rows?: ObligationRow[];
  clauseReferences?: ClauseReference[];
  noticeDraft?: string;
  allowedDecisions?: DecisionAction[];
};

export type ObligationWorkspaceProps = {
  worklist?: WorklistItem[];
  caseId?: string | null;
  itemId?: string | null;
  obligation?: ObligationRecord | null;
  decisionReason?: string;
  onDecisionReasonChange?: (reason: string) => void;
  onNavigate?: (direction: 'previous' | 'next') => void;
  onDecisionSubmit?: (decisionId: string, reason: string) => boolean | void | Promise<boolean | void>;
};

function findSelectedIndex(worklist: WorklistItem[], caseId?: string | null, itemId?: string | null): number {
  if (!worklist.length || !caseId) return -1;
  if (itemId) {
    const index = worklist.findIndex((item) => item.caseId === caseId && item.itemId === itemId);
    return index >= 0 ? index : -1;
  }
  return worklist.findIndex((item) => item.caseId === caseId);
}

export default function ObligationWorkspace({
  worklist = [],
  caseId,
  itemId,
  obligation,
  decisionReason,
  onDecisionReasonChange,
  onNavigate,
  onDecisionSubmit,
}: ObligationWorkspaceProps) {
  const selectedIndex = useMemo(() => findSelectedIndex(worklist, caseId, itemId), [caseId, itemId, worklist]);
  const selectedItem = worklist[selectedIndex] ?? null;
  const [toast, setToast] = useState<string | null>(null);

  const canGoPrevious = selectedIndex > 0;
  const canGoNext = selectedIndex >= 0 && selectedIndex < worklist.length - 1;
  const rows = obligation?.rows ?? [];
  const clauseReferences = obligation?.clauseReferences ?? [];
  const allowedDecisions = obligation?.allowedDecisions ?? [];
  const title = obligation?.title ?? selectedItem?.itemId ?? 'Obligation';

  async function handleDecision(decisionId: string): Promise<void> {
    if (!onDecisionSubmit) return;
    const result = await onDecisionSubmit(decisionId, decisionReason ?? '');
    if (result === true) setToast('Decision recorded');
  }

  return (
    <section aria-labelledby="obligation-workspace-heading" style={{ display: 'grid', gridTemplateColumns: 'minmax(260px, 1fr) minmax(260px, 1.3fr)', gap: tokens.spacingHorizontalM }}>
      <h2 id="obligation-workspace-heading" style={{ gridColumn: '1 / -1' }}>Obligation workspace</h2>
      <div>
        <h3>{title}</h3>
        <p>{obligation?.summary ?? `Case ${selectedItem?.caseId ?? caseId ?? 'unknown'}: ${selectedItem?.itemId ?? 'Obligation'}`}</p>
        {rows.length > 0 && (
          <table>
            <thead>
              <tr>
                <th scope="col">Field</th>
                <th scope="col">Value</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.label}>
                  <th scope="row">{row.label}</th>
                  <td>{row.value}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {clauseReferences.length > 0 && (
          <div>
            <h3>Clause references</h3>
            <ul>
              {clauseReferences.map((clause) => (
                <li key={clause.id}>{clause.text}</li>
              ))}
            </ul>
          </div>
        )}
      </div>
      <div>
        <div style={{ display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'center' }}>
          {canGoPrevious && (
            <button type="button" onClick={() => onNavigate?.('previous')}>Previous</button>
          )}
          {canGoNext && (
            <button type="button" onClick={() => onNavigate?.('next')}>Next</button>
          )}
        </div>
        <p>{obligation?.status ?? selectedItem?.state ?? 'Awaiting legal review'}</p>
        <p>{SIMULATION_BANNER}</p>
        <div>
          <h3>Notice draft</h3>
          <p>{obligation?.noticeDraft ?? 'No notice draft recorded.'}</p>
        </div>
        {allowedDecisions.length > 0 && (
          <div aria-label="Decision panel">
            {typeof decisionReason === 'string' && onDecisionReasonChange && (
              <>
                <label htmlFor="obligation-decision-reason">Reason for decision</label>
                <textarea id="obligation-decision-reason" value={decisionReason} onChange={(event) => onDecisionReasonChange(event.target.value)} />
              </>
            )}
            <div role="group" aria-label="Allowed decisions">
              {allowedDecisions.map((decision) => (
                <button key={decision.id} type="button" onClick={() => { void handleDecision(decision.id); }}>
                  {decision.label}
                </button>
              ))}
            </div>
          </div>
        )}
      </div>
      {toast && <div role="status" aria-live="polite">{toast}</div>}
    </section>
  );
}
