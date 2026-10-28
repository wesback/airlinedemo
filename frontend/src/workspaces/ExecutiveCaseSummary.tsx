export type ExecutiveCaseSummaryProps = {
  caseId: string;
  summary?: string;
  rows?: Array<{
    role: string;
    count: number;
    nextDecisionOwner?: string | null;
  }>;
  latestActivity?: { actor: string; label: string | null; outcome: string } | null;
  showDecisionControls?: boolean;
};

export default function ExecutiveCaseSummary({
  caseId,
  summary = 'Executive read-only view',
  rows = [],
  latestActivity = null,
  showDecisionControls = false,
}: ExecutiveCaseSummaryProps) {
  const hasDecisionControls = Boolean(showDecisionControls);

  return (
    <section aria-labelledby="executive-case-summary-heading" data-testid="executive-case-summary">
      <h2 id="executive-case-summary-heading">Case {caseId}</h2>
      <p>{summary}</p>
      <table>
        <thead>
          <tr>
            <th scope="col">Role</th>
            <th scope="col">Open tasks</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.role}>
              <td>{row.role}</td>
              <td>{row.count}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {latestActivity && (
        <p>
          Latest activity: {latestActivity.actor}: {latestActivity.label || 'Unknown'} — {latestActivity.outcome}
        </p>
      )}
      {!hasDecisionControls && <p aria-live="polite">Read-only summary</p>}
    </section>
  );
}
