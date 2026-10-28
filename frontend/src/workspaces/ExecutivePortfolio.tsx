export type PortfolioActivity = {
  sequence: number;
  actor: string;
  label: string | null;
  outcome: string;
};

export type ExecutivePortfolioRow = {
  caseId: string;
  openItemsByRole: Record<string, number>;
  nextDecisionOwner: string | null;
  oldestOpenAgeSeconds: number | null;
  latestActivity: PortfolioActivity | null;
};

export type ExecutivePortfolioProps = {
  rows?: ExecutivePortfolioRow[];
  onOpenCase?: (caseId: string) => void;
};

function sumOpenItems(openItemsByRole: Record<string, number>): number {
  return Object.values(openItemsByRole ?? {}).reduce((total, value) => total + Number(value || 0), 0);
}

function formatAge(ageSeconds: number | null): string {
  if (ageSeconds === null || !Number.isFinite(ageSeconds) || ageSeconds <= 0) return '—';
  const days = Math.floor(ageSeconds / 86400);
  const hours = Math.floor((ageSeconds % 86400) / 3600);
  return `${days}d ${hours}h`;
}

function latestActivityText(activity: PortfolioActivity | null): string {
  if (!activity) return '—';
  const label = activity.label && activity.label.trim() ? activity.label : 'Unknown';
  return `${activity.actor}: ${label} — ${activity.outcome}`;
}

export default function ExecutivePortfolio({ rows = [], onOpenCase }: ExecutivePortfolioProps) {
  const totalCases = rows.length;
  const activeCases = rows.filter((row) => sumOpenItems(row.openItemsByRole) > 0).length;
  const totalOpenItems = rows.reduce((total, row) => total + sumOpenItems(row.openItemsByRole), 0);
  const oldestOpenAge = rows.reduce<number | null>((max, row) => {
    if (row.oldestOpenAgeSeconds === null) return max;
    if (max === null || row.oldestOpenAgeSeconds > max) return row.oldestOpenAgeSeconds;
    return max;
  }, null);

  return (
    <section aria-labelledby="executive-portfolio-heading" data-testid="executive-portfolio">
      <h2 id="executive-portfolio-heading">Portfolio</h2>
      <div aria-label="Portfolio KPI tiles" style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(180px, 1fr))', gap: '1rem' }}>
        <div aria-label="Cases in scope KPI">
          <div>Cases in scope</div>
          <strong>{totalCases || 0}</strong>
        </div>
        <div aria-label="Cases with open items KPI">
          <div>Cases with open items</div>
          <strong>{activeCases || 0}</strong>
        </div>
        <div aria-label="Total open items KPI">
          <div>Total open items</div>
          <strong>{totalOpenItems || 0}</strong>
        </div>
        <div aria-label="Oldest open age KPI">
          <div>Oldest age</div>
          <strong>{formatAge(oldestOpenAge)}</strong>
        </div>
      </div>
      {totalCases === 0 && <p>No cases in your scope.</p>}
      {totalCases > 0 && (
        <table>
          <thead>
            <tr>
              <th scope="col">Case</th>
              <th scope="col">Open items</th>
              <th scope="col">Next decision owner</th>
              <th scope="col">Oldest age</th>
              <th scope="col">Latest activity</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.caseId} onClick={() => onOpenCase?.(row.caseId)} style={{ cursor: onOpenCase ? 'pointer' : undefined }}>
                <td>{row.caseId}</td>
                <td>{Object.entries(row.openItemsByRole ?? {}).map(([role, count]) => `${role}: ${count}`).join(', ') || '0'}</td>
                <td>{row.nextDecisionOwner ?? '—'}</td>
                <td>{formatAge(row.oldestOpenAgeSeconds)}</td>
                <td>{latestActivityText(row.latestActivity)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
