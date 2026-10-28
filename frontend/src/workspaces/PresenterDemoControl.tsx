export type PresenterCase = {
  id: string;
  name?: string;
  canReset?: boolean;
  resetLabel?: string;
};

export type PresenterPersona = {
  id: string;
  label: string;
  role: string;
};

export const PRESENTER_PERSONAS: PresenterPersona[] = [
  { id: 'presenter', label: 'Demo presenter', role: 'presenter' },
  { id: 'reviewer', label: 'Technical reviewer', role: 'technical-reviewer' },
  { id: 'fin-1', label: 'Finance reviewer', role: 'finance-reviewer' },
  { id: 'legal-1', label: 'Legal reviewer', role: 'legal-reviewer' },
  { id: 'exec-1', label: 'Executive observer', role: 'executive' },
];

const PRESENTER_GUIDED_MAIN_VIEWS = {
  technical: '/cases/:caseId/findings',
  finance: '/cases/:caseId/reserve',
  legal: '/cases/:caseId/obligations',
  executive: '/portfolio',
  presenter: '/demo',
} as const;

export function getPresenterGuidedChecklist(caseId?: string): Array<{ label: string; route: string; description: string }> {
  const selectedCaseRoute = caseId ? `/cases/${encodeURIComponent(caseId)}/overview` : '/cases';
  return [
    { label: 'My work', route: '/work', description: 'Review the technical findings queue.' },
    { label: 'Portfolio', route: PRESENTER_GUIDED_MAIN_VIEWS.executive, description: 'Review the executive portfolio overview.' },
    { label: 'Demo control', route: PRESENTER_GUIDED_MAIN_VIEWS.presenter, description: 'Stay on the presenter control centre.' },
    { label: 'Case', route: selectedCaseRoute, description: 'Open the selected case overview.' },
    { label: 'Hangar', route: '/hangar', description: 'Review the aircraft view.' },
  ];
}

export type PresenterDemoControlProps = {
  cases?: PresenterCase[];
  caseId?: string;
  onResetCase?: (caseId: string) => void;
  onViewAs?: (userId: string) => void;
};

export default function PresenterDemoControl({
  cases = [],
  caseId,
  onResetCase,
  onViewAs,
}: PresenterDemoControlProps) {
  const resolvedCases = cases.length
    ? cases
    : [{ id: caseId ?? 'demo-case', name: caseId ?? 'Selected case', canReset: false }];
  const effectiveCaseId = caseId ?? resolvedCases[0]?.id;

  return (
    <section aria-labelledby="presenter-demo-control-heading">
      <h2 id="presenter-demo-control-heading">Demo control</h2>
      <div aria-label="Presenter persona switches">
        <p>View as</p>
        <div>
          {PRESENTER_PERSONAS.map((persona) => (
            <button key={persona.id} type="button" onClick={() => onViewAs?.(persona.id)}>
              View as {persona.label}
            </button>
          ))}
        </div>
      </div>

      <table>
        <thead>
          <tr>
            <th>Case</th>
            <th>Reset</th>
            <th>View as</th>
          </tr>
        </thead>
        <tbody>
          {resolvedCases.map((entry) => (
            <tr key={entry.id}>
              <td>{entry.name ?? entry.id}</td>
              <td>
                {entry.canReset ? (
                  <button type="button" onClick={() => onResetCase?.(entry.id)}>
                    {entry.resetLabel ?? 'Reset case'}
                  </button>
                ) : (
                  <span aria-label={`${entry.id} reset not eligible`}>Not eligible</span>
                )}
              </td>
              <td>
                <div>
                  {PRESENTER_PERSONAS.map((persona) => (
                    <button key={`${entry.id}:${persona.id}`} type="button" onClick={() => onViewAs?.(persona.id)}>
                      View as {persona.label}
                    </button>
                  ))}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <div aria-label="Presenter guided path checklist">
        <h3>Guided path</h3>
        <ul>
          {getPresenterGuidedChecklist(effectiveCaseId).map((item) => (
            <li key={item.label}>
              <a href={item.route}>{item.label}</a>
              <span>{item.description}</span>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
