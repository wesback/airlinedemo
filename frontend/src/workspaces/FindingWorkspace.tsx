import { useMemo, useState } from 'react';
import { tokens } from '@fluentui/react-components';
import type { WorklistItem } from './TechnicalWorkspace';

export type DecisionAction = { id: string; label: string };

export type FindingEvidence = {
  id?: string;
  label?: string;
  text?: string;
  status?: string;
};

export type FindingWorkspaceProps = {
  worklist?: WorklistItem[];
  caseId?: string | null;
  itemId?: string | null;
  findings?: {
    title?: string;
    summary?: string;
    status?: string;
    evidence?: FindingEvidence[];
  };
  allowedDecisions?: DecisionAction[];
  decisionReason?: string;
  onDecisionReasonChange?: (reason: string) => void;
  onNavigate?: (direction: 'previous' | 'next') => void;
  onDecisionSubmit?: (decisionId: string, reason: string) => void | Promise<void>;
};

function findSelectedIndex(worklist: WorklistItem[], caseId?: string | null, itemId?: string | null): number {
  if (!worklist.length) return -1;
  if (caseId && itemId) {
    const index = worklist.findIndex((item) => item.caseId === caseId && item.itemId === itemId);
    if (index >= 0) return index;
  }
  if (caseId) {
    const index = worklist.findIndex((item) => item.caseId === caseId);
    if (index >= 0) return index;
  }
  return 0;
}

export default function FindingWorkspace({
  worklist = [],
  caseId,
  itemId,
  findings,
  allowedDecisions = [],
  decisionReason,
  onDecisionReasonChange,
  onNavigate,
  onDecisionSubmit,
}: FindingWorkspaceProps) {
  const selectedIndex = useMemo(() => findSelectedIndex(worklist, caseId, itemId), [caseId, itemId, worklist]);
  const selectedItem = worklist[selectedIndex] ?? null;
  const [toast, setToast] = useState<string | null>(null);

  const canGoPrevious = selectedIndex > 0;
  const canGoNext = selectedIndex >= 0 && selectedIndex < worklist.length - 1;

  async function handleDecision(decisionId: string) {
    if (!onDecisionSubmit) return;
    await onDecisionSubmit(decisionId, decisionReason ?? '');
    setToast('Decision recorded');
  }

  const evidence = findings?.evidence ?? [
    { id: 'evidence-1', label: 'Request status', text: findings?.status ?? selectedItem?.state ?? 'Open', status: 'open' },
  ];

  return (
    <section aria-labelledby="finding-workspace-heading" style={{ display: 'grid', gridTemplateColumns: 'minmax(260px, 1fr) minmax(260px, 1.3fr)', gap: tokens.spacingHorizontalM }}>
      <h2 id="finding-workspace-heading" style={{ gridColumn: '1 / -1' }}>Finding workspace</h2>
      <div>
        <h3>Evidence</h3>
        <ul>
          {evidence.map((entry, index) => (
            <li key={`${entry.id ?? 'evidence'}-${index}`}>
              <strong>{entry.label ?? `Evidence ${index + 1}`}</strong>
              <div>{entry.text ?? 'No evidence recorded'}</div>
              {entry.status ? <small>{entry.status}</small> : null}
            </li>
          ))}
        </ul>
        <div>
          <h3>Request status</h3>
          <p>{findings?.status ?? selectedItem?.state ?? 'Awaiting review'}</p>
        </div>
      </div>
      <div>
        <div style={{ display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'center' }}>
          <button type="button" disabled={!canGoPrevious} onClick={() => { if (canGoPrevious && onNavigate) onNavigate('previous'); }}>
            Previous
          </button>
          <button type="button" disabled={!canGoNext} onClick={() => { if (canGoNext && onNavigate) onNavigate('next'); }}>
            Next
          </button>
        </div>
        <h3>{findings?.title ?? selectedItem?.itemId ?? 'Finding'}</h3>
        <p>{findings?.summary ?? `Case ${selectedItem?.caseId ?? caseId ?? 'unknown'}: ${selectedItem?.itemId ?? 'Finding'}`}</p>
        {allowedDecisions.length > 0 && (
          <div aria-label="Decision panel">
            {typeof decisionReason === 'string' && onDecisionReasonChange && (
              <>
                <label htmlFor="finding-decision-reason">Reason for decision</label>
                <textarea id="finding-decision-reason" value={decisionReason} onChange={(event) => onDecisionReasonChange(event.target.value)} />
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
