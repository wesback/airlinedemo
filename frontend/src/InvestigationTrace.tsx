export type InvestigationTraceStep = {
  sequence: number;
  toolName: string;
  documentId: string;
  page: number;
  citationCheckOutcome: string;
  correctionCode: string;
  policyHandover: string;
};

export type InvestigationTraceRecord = { steps: InvestigationTraceStep[] };

export type InvestigationTraceView =
  | { status: 'loading' }
  | { status: 'not-found' }
  | { status: 'error' }
  | { status: 'loaded'; trace: InvestigationTraceRecord };

export default function InvestigationTrace({ view }: { view: InvestigationTraceView }) {
  return (
    <section aria-labelledby="investigation-trace-heading">
      <h3 id="investigation-trace-heading">Investigation trace</h3>
      {view.status === 'loading' && <p role="status">Loading investigation trace…</p>}
      {view.status === 'not-found' && <p>No trace recorded</p>}
      {view.status === 'error' && <p role="alert">Unable to load investigation trace.</p>}
      {view.status === 'loaded' && (
        <ol aria-label="Investigation trace steps">
          {[...view.trace.steps].sort((a, b) => a.sequence - b.sequence).map((step) => (
            <li key={step.sequence}>
              {step.toolName === 'citation-check' ? (
                <>
                  <span>Citation check</span>
                  {' — '}<span>Outcome: {step.citationCheckOutcome}</span>
                </>
              ) : step.toolName === 'claim-validation' ? (
                <>
                  <span>Claim validation</span>
                  {' — '}<span>Correction: {step.correctionCode}</span>
                </>
              ) : step.toolName === 'policy-handover' ? (
                <>
                  <span>Policy handover</span>
                  {' — '}<span>{step.policyHandover === 'deterministic-policy'
                    ? 'Outcome: Deterministic policy took over'
                    : `Outcome: ${step.policyHandover}`}</span>
                </>
              ) : (
                <>
                  <span>Tool call</span>
                  {' — '}<span>{step.toolName}</span>
                  {' — '}<span>Outcome: {step.citationCheckOutcome}</span>
                </>
              )}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
