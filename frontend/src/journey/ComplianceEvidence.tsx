import { useEffect, useRef, useState } from 'react';
import { ApiError, getJson } from './api';
import { formatWhen } from './model';

type EvidenceRef = { sourceId: string; recordId: string | null; recordVersion: number | null; access: string };
type Decision = { sequence: number; auditEventId: string; actorId: string; role: string; at: string; decision: string };
type Requirement = {
  requirementId: string; requirementVersion: number | null; responsibleParty: string | null; status: string;
  applicability: EvidenceRef | null; evidence: EvidenceRef[]; decision: Decision | null;
};
type EvidenceView = {
  basisVersion: number | null; currentBasis: boolean; applicabilityAccess: string;
  regimes: { regime: string; requirements: Requirement[] }[]; untagged: Requirement[];
  basisDecision: Decision | null; decisions: Decision[];
  responsibilities: { platform: string[]; customer: string[]; limitation: string };
};
const labels: Record<string, string> = { met_by_evidence: 'Met by evidence', needs_review: 'Needs review', not_assessed: 'Not assessed' };

export default function ComplianceEvidence({ caseId, onClose, sourceCase }: {
  caseId: string; onClose: () => void; sourceCase?: { id: string; open: () => void };
}) {
  const [data, setData] = useState<EvidenceView | null>(null);
  const [error, setError] = useState('');
  const [decisionId, setDecisionId] = useState('');
  const [regime, setRegime] = useState('');
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { heading.current?.focus(); }, []);
  useEffect(() => {
    let active = true;
    setData(null);
    setError('');
    const query = decisionId ? `?decisionId=${encodeURIComponent(decisionId)}` : '';
    void getJson<EvidenceView>(`/api/v1/cases/${encodeURIComponent(caseId)}/compliance-evidence${query}`)
      .then((result) => {
        if (!active) return;
        setData(result);
        setRegime((previous) => result.regimes.some((r) => r.regime === previous) ? previous : result.regimes[0]?.regime ?? '');
      })
      .catch((caught: unknown) => {
        if (active) setError(caught instanceof ApiError && caught.status === 404
          ? 'This evidence view is not permitted or no longer available.'
          : 'The evidence view could not be loaded. No assessment is shown.');
      });
    return () => { active = false; };
  }, [caseId, decisionId]);
  return (
    <>
      <header className="av-panel-head">
        <div><p className="av-panel-kicker">Decision dossier · Read-only</p><h2 id="evidence-heading" ref={heading} tabIndex={-1}>Compliance evidence</h2></div>
        <button type="button" className="av-button av-button-ghost" onClick={onClose}>Back to journey</button>
      </header>
      <div className="av-panel-body av-evidence">
        <p>Trace the requirement, pinned records and recorded review. A basis review is not an assessment of every regime.</p>
        {sourceCase && <aside className="av-notice" aria-label="Separate source-system demonstration">
          <h3>Separate source-system demonstration</h3>
          <p>The specialist source records belong to a different synthetic case. They are not evidence for this aircraft.</p>
          <a href={`/cases/${encodeURIComponent(sourceCase.id)}`} onClick={(event) => { event.preventDefault(); sourceCase.open(); }}>Open source-system case</a>
        </aside>}
        {error && <p className="av-notice av-notice-alert" role="alert">{error}</p>}
        {!data && !error && <p role="status">Loading pinned evidence…</p>}
        {data && (
          <>
            <p className="av-notice">{data.basisVersion === null ? 'No evidence basis is recorded. Needs review.'
              : `Evidence basis v${data.basisVersion}${data.currentBasis ? '' : ' · Superseded basis, retained for this decision'}`}</p>
            {data.decisions.length > 0 && (
              <label className="av-field">Decision basis
                <select value={decisionId || String(data.basisDecision?.sequence ?? '')} onChange={(event) => setDecisionId(event.target.value)}>
                  {data.decisions.map((decision) => <option key={decision.sequence} value={decision.sequence}>Review #{decision.sequence} · {formatWhen(decision.at)}</option>)}
                </select>
              </label>
            )}
            {data.basisDecision && <section aria-label="Recorded basis review"><h3>Recorded basis review</h3><DecisionLink decision={data.basisDecision} /></section>}
            {data.applicabilityAccess !== 'permitted' && <p>Applicability records: not permitted for this role.</p>}
            {data.regimes.length === 0 ? <p>No permitted, tagged regimes are recorded on this basis. Needs review.</p> : (
              <>
                <label className="av-field">Regulatory regime
                  <select value={regime} onChange={(event) => setRegime(event.target.value)}>
                    {data.regimes.map((item) => <option key={item.regime} value={item.regime}>{item.regime}</option>)}
                  </select>
                </label>
                <section aria-label={`${regime} requirements`}>
                  <h3>{regime}</h3>
                  {data.regimes.find((r) => r.regime === regime)?.requirements.map((requirement, index) =>
                    <RequirementView key={`${requirement.requirementId}-${index}`} requirement={requirement} />)}
                </section>
              </>
            )}
            {data.untagged.length > 0 && <section aria-label="Untagged requirements"><h3>Missing applicability tags</h3>
              {data.untagged.map((requirement, index) => <RequirementView key={`${requirement.requirementId}-${index}`} requirement={requirement} />)}
            </section>}
            <section className="av-evidence-responsibility" aria-label="Shared responsibility">
              <h3>Shared responsibility</h3>
              <h4>Platform controls evidenced</h4><ul>{data.responsibilities.platform.map((item) => <li key={item}>{item}</li>)}</ul>
              <h4>Customer decisions</h4><ul>{data.responsibilities.customer.map((item) => <li key={item}>{item}</li>)}</ul>
              <p>{data.responsibilities.limitation}</p>
            </section>
          </>
        )}
      </div>
    </>
  );
}

function RequirementView({ requirement }: { requirement: Requirement }) {
  const missingReference = (reference: EvidenceRef | null) => !reference?.recordId
    || !Number.isInteger(reference.recordVersion) || (reference.recordVersion ?? 0) < 1;
  const incomplete = missingReference(requirement.applicability) || !requirement.responsibleParty
    || requirement.responsibleParty === 'unassigned' || requirement.evidence.length === 0
    || requirement.evidence.some(missingReference);
  const status = incomplete ? 'needs_review' : requirement.status;
  return (
    <article className="av-evidence-requirement" aria-label={requirement.requirementId}>
      <header><h4>{requirement.requirementId}</h4><strong className={status === 'met_by_evidence' ? 'av-pill av-pill-green' : 'av-pill av-pill-alert'}>{labels[status] ?? 'Needs review'}</strong></header>
      <p>Responsible party: {requirement.responsibleParty ?? 'Not recorded'}</p>
      {requirement.applicability && <p>Applicability: <Reference reference={requirement.applicability} /></p>}
      <h5>Pinned evidence records</h5>
      {requirement.evidence.length === 0 ? <p>No evidence reference recorded.</p> : (
        <ul>{requirement.evidence.map((reference, index) => <li key={`${reference.sourceId}-${reference.recordId}-${index}`}><Reference reference={reference} /></li>)}</ul>
      )}
      {requirement.decision ? <DecisionLink decision={requirement.decision} /> : <p>No requirement-specific decision recorded.</p>}
    </article>
  );
}

function Reference({ reference }: { reference: EvidenceRef }) {
  return <span>{reference.sourceId}{reference.recordId !== null && <> · <code>{reference.recordId}</code> · v{reference.recordVersion}</>}
    {reference.access === 'not_permitted' && <strong> · Source access not permitted</strong>}</span>;
}

function DecisionLink({ decision }: { decision: Decision }) {
  return (
    <div className="av-evidence-decision">
      <p>{decision.decision === 'accept' ? 'Accepted findings' : 'Rejected findings'} · {decision.actorId} · {decision.role}</p>
      <time dateTime={decision.at}>{formatWhen(decision.at)}</time>{' · '}
      <a href={`#case-activity-${decision.sequence}`} onClick={() => document.getElementById(`case-activity-${decision.sequence}`)?.focus()}>
        Activity #{decision.sequence}
      </a>
      <p>Audit entry: <code>{decision.auditEventId}</code></p>
    </div>
  );
}
