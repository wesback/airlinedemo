export type ReserveWorksheetRow = {
  input?: string;
  value?: string | number | null;
  source?: string;
  freshness?: string;
  status?: string;
  label?: string;
  contractLine?: string;
  ContractLine?: string;
};

export type ReserveWorksheetDraft = {
  draftId?: string;
  version?: number;
  revision?: number;
  period?: string;
  status?: string;
  payableTotal?: number | null;
  calculationBasis?: string;
  blockers?: Array<{ code?: string; detail?: string }>;
  discrepancies?: string[];
  lines?: ReserveWorksheetRow[];
  allowedDecisions?: Array<{ id: string; label: string }>;
};

type Props = {
  draft?: ReserveWorksheetDraft | null;
  decisionReason?: string;
  onDecisionReasonChange?: (value: string) => void;
  onDecision?: (decision: string) => void;
};

function normalizeRow(row: ReserveWorksheetRow): ReserveWorksheetRow {
  const input = row.input ?? row.label ?? row.contractLine ?? row.ContractLine ?? 'Unknown input';
  const value = row.value ?? null;
  const source = row.source ?? 'Unknown source';
  const freshness = row.freshness ?? 'Unknown';
  const status = row.status ?? 'Not available';
  return { ...row, input, value, source, freshness, status };
}

function normalizeStatus(status: string | undefined | null): string {
  const normalized = (status ?? '').trim();
  if (!normalized) return 'Not available';
  const lower = normalized.toLowerCase();
  if (lower.includes('blocked')) return 'Blocked';
  if (lower.includes('stale')) return 'Stale';
  return normalized;
}

export default function ReserveWorksheet({ draft, decisionReason = '', onDecisionReasonChange, onDecision }: Props) {
  const rows = Array.isArray(draft?.lines) ? draft.lines.map((row) => normalizeRow(row)) : [];
  const allowedDecisions = Array.isArray(draft?.allowedDecisions) ? draft.allowedDecisions : [];

  return (
    <section aria-labelledby="reserve-heading">
      <h2 id="reserve-heading">Reserve worksheet</h2>
      <p>Draft only — no invoice is issued.</p>
      <div role="status" aria-live="polite">{draft?.status ?? 'Not available'}</div>
      <table>
        <thead>
          <tr>
            <th>Input</th>
            <th>Value</th>
            <th>Source</th>
            <th>Freshness</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 && (
            <tr>
              <td colSpan={5}>No reserve inputs available.</td>
            </tr>
          )}
          {rows.map((row, index) => {
            const statusText = normalizeStatus(row.status);
            const valueText = row.value === null || row.value === undefined || row.value === '' ? 'Not available' : String(row.value);
            return (
              <tr key={`${row.input ?? 'input'}-${index}`}>
                <td>{row.input ?? 'Unknown input'}</td>
                <td>{valueText}</td>
                <td>{row.source ?? 'Unknown source'}</td>
                <td>{row.freshness ?? 'Unknown'}</td>
                <td>{statusText}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {draft?.payableTotal !== undefined && draft.payableTotal !== null && (
        <p>Draft amount: {String(draft.payableTotal)}</p>
      )}
      {Array.isArray(draft?.discrepancies) && draft.discrepancies.length > 0 && (
        <ul>
          {draft.discrepancies.map((item, index) => <li key={`${item}-${index}`}>{item}</li>)}
        </ul>
      )}
      {allowedDecisions.length > 0 && (
        <section aria-label="Reserve decision panel">
          <label htmlFor="reserve-decision-reason">Reason for decision</label>
          <textarea
            id="reserve-decision-reason"
            aria-label="Reason for decision"
            value={decisionReason ?? ''}
            onChange={(event) => onDecisionReasonChange?.(event.currentTarget.value)}
            onInput={(event) => onDecisionReasonChange?.((event.target as HTMLTextAreaElement).value)}
          />
          <div role="group" aria-label="Reserve decisions">
            {allowedDecisions.map((action) => (
              <button key={action.id} type="button" onClick={() => onDecision?.(action.id)}>{action.label}</button>
            ))}
          </div>
        </section>
      )}
    </section>
  );
}
