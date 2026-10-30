import type { ReactNode } from 'react';
import { ActorGlyph, CheckIcon, DocumentIcon } from './Art';
import type { AiBrief, AiCheck, AiFinding } from './aiBriefs';
import type { JourneyEvent } from './api';
import { documentTitle } from './StepPanels';

const severityLabel = { blocker: 'Blocks redelivery', attention: 'Needs attention' } as const;
const checkLabel = { pass: 'Verified', flag: 'Flagged', unread: 'Could not read' } as const;

function CheckMark({ result }: { result: AiCheck['result'] }) {
  if (result === 'pass') return <span className="av-check-mark av-check-pass"><CheckIcon /></span>;
  return (
    <span className={`av-check-mark av-check-${result}`}>
      <svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">
        {result === 'flag'
          ? <path d="M8 3v6M8 12v.5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
          : <path d="M4 8h8" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />}
      </svg>
    </span>
  );
}

export function FindingCard({ finding }: { finding: AiFinding }) {
  return (
    <li className={`av-finding av-finding-${finding.severity}`}>
      <div className="av-finding-head">
        <span className={`av-severity av-severity-${finding.severity}`}>{severityLabel[finding.severity]}</span>
        <strong>{finding.title}</strong>
      </div>
      <p>{finding.detail}</p>
      <div className="av-citations" aria-label="Sources">
        {finding.sources.map((source) => (
          <span key={source.document} className="av-citation">
            <DocumentIcon />{documentTitle(source.document)}{source.note && <em>· {source.note}</em>}
          </span>
        ))}
      </div>
    </li>
  );
}

function Stat({ value, label }: { value: number; label: string }) {
  return <div className="av-ai-stat"><dt>{label}</dt><dd>{value}</dd></div>;
}

/** The full briefing of the records agent: what it checked, what it found and what it deliberately left to people. */
export function AiBriefView({ brief, trace }: { brief: AiBrief; trace: JourneyEvent[] }) {
  const sources = new Set(brief.findings.flatMap((finding) => finding.sources.map((source) => source.document)));
  return (
    <div className="av-ai-brief">
      <div className="av-ai-headline">
        <ActorGlyph actor="ai" size={18} />
        <div>
          <span className="av-ai-kicker">AI insight</span>
          <p>{brief.headline}</p>
        </div>
      </div>
      <dl className="av-ai-stats">
        <Stat value={brief.documentsRead} label="documents read" />
        <Stat value={brief.checks.length} label="checks run" />
        <Stat value={brief.findings.length} label={brief.findings.length === 1 ? 'finding raised' : 'findings raised'} />
        <Stat value={sources.size} label="sources cited" />
      </dl>

      {brief.checks.length > 0 && (
        <Section title="What the agent checked">
          <ul className="av-checks">
            {brief.checks.map((check) => (
              <li key={check.label}>
                <CheckMark result={check.result} />
                <span><strong>{check.label}</strong><span className={`av-check-result av-check-text-${check.result}`}>{checkLabel[check.result]}</span><span>{check.detail}</span></span>
              </li>
            ))}
          </ul>
        </Section>
      )}

      {brief.findings.length > 0 && (
        <Section title="Findings for the technical reviewer">
          <ul className="av-findings">{brief.findings.map((finding) => <FindingCard key={finding.id} finding={finding} />)}</ul>
        </Section>
      )}

      <Section title="What the agent did not do">
        <ul className="av-guardrails">{brief.guardrails.map((item) => <li key={item}>{item}</li>)}</ul>
      </Section>

      {trace.length > 0 && (
        <details className="av-trace-details">
          <summary>Agent trace, {trace.length} recorded step{trace.length === 1 ? '' : 's'}</summary>
          <ol className="av-ai-trace">
            {trace.map((event) => (
              <li key={event.eventId}>
                <span className="av-ai-trace-marker"><ActorGlyph actor="ai" size={14} /></span>
                <div>
                  <p>{event.summary}</p>
                  <div className="av-citations" aria-label="Sources">
                    {event.witnesses.map((witness) => (
                      <span key={witness.witnessId} className="av-citation"><DocumentIcon />{documentTitle(witness.documentId)}, p. {witness.page}</span>
                    ))}
                  </div>
                </div>
              </li>
            ))}
          </ol>
        </details>
      )}
    </div>
  );
}

/** Explains to the technical reviewer why a person has to decide and what each choice does. */
export function WhyReview({ brief }: { brief: AiBrief }) {
  return (
    <section className="av-why" aria-labelledby="why-heading">
      <h3 id="why-heading"><ActorGlyph actor="human" size={16} />Why you are reviewing</h3>
      <p>{brief.whyHuman}</p>
      {brief.findings.length > 0 && (
        <>
          <h4>You are confirming</h4>
          <ul className="av-findings av-findings-compact">{brief.findings.map((finding) => <FindingCard key={finding.id} finding={finding} />)}</ul>
        </>
      )}
      <div className="av-why-choices">
        <div className="av-why-accept"><strong>If you accept</strong><p>{brief.acceptMeans}</p></div>
        <div className="av-why-reject"><strong>If you reject</strong><p>{brief.rejectMeans}</p></div>
      </div>
    </section>
  );
}

/** Compact AI summary shown beside every gate of the journey. */
export function AiInsightCard({ brief, onOpen }: { brief: AiBrief; onOpen: () => void }) {
  const blockers = brief.findings.filter((finding) => finding.severity === 'blocker').length;
  return (
    <section className="av-ai-card" aria-labelledby="ai-card-heading">
      <h2 id="ai-card-heading"><ActorGlyph actor="ai" size={14} />AI insight</h2>
      <p>{brief.headline}</p>
      <p className="av-ai-card-meta">
        {brief.documentsRead} documents read, {brief.findings.length} finding{brief.findings.length === 1 ? '' : 's'}{blockers ? `, ${blockers} blocking` : ''}
      </p>
      <button type="button" className="av-link-button" onClick={onOpen}>See what the AI found</button>
    </section>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return <section className="av-ai-section"><h3 className="av-panel-sub">{title}</h3>{children}</section>;
}
