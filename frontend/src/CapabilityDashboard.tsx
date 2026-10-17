export const KNOWN_LABELS = ['AI agent', 'Deterministic workflow', 'Human decision', 'Design only'] as const;

export type EvidenceLink = { label: string; url: string };
export type DecisionAction = { id: string; label: string };
export type DesignDetails = {
  purpose?: string;
  intendedAutonomy?: string;
  humanAuthority?: string;
  notice?: string;
};
export type Capability = {
  id: string;
  name: string;
  label: unknown;
  designOnly: boolean;
  openItems: number | null;
  nextDecisionOwner: string | null;
  evidence: EvidenceLink[];
  design: DesignDetails | null;
  caseStudyAutonomy?: unknown;
  demoAutonomy?: unknown;
};
export type ActivityItem = {
  sequence: number;
  actor: string;
  label: unknown;
  outcome: string;
};
export type QueueItem = { itemId: string; label: unknown };
export type DashboardData = {
  allowedDecisions: DecisionAction[];
  capabilities: Capability[];
  queue: QueueItem[];
  activity: ActivityItem[];
};

export function safeLabel(label: unknown): string {
  return typeof label === 'string' && (KNOWN_LABELS as readonly string[]).includes(label) ? label : 'Unknown';
}

// Only same-origin relative paths are linkable; anything else (javascript:, data:, //host, /\host) is dropped.
// Browsers strip tabs and newlines from URLs, so those are rejected too (e.g. "/\t/host").
export function safeHref(url: string | null | undefined): string | null {
  if (typeof url !== 'string') return null;
  return /^\/(?![/\\])/.test(url) && !/[\\\t\n\r]/.test(url) ? url : null;
}

function displayValue(value: unknown): string {
  return typeof value === 'string' && value.length > 0 ? value : 'Unknown';
}

type Props = {
  data: DashboardData;
  onDecision?: (capabilityId: string, decisionId: string) => void;
};

export default function CapabilityDashboard({ data, onDecision }: Props) {
  const allowedDecisions = data.allowedDecisions.filter(
    (action) => (action.id === 'accept' || action.id === 'reject') && typeof action.label === 'string',
  );
  const hasTechnicalReview = data.capabilities.some((capability) => capability.id === 'technical-review');
  const decisionControls = (capabilityId: string) => capabilityId === 'technical-review' && allowedDecisions.length > 0 && (
    <div role="group" aria-label="Technical review decisions">
      {allowedDecisions.map((action) => (
        <button key={action.id} type="button" onClick={() => onDecision?.(capabilityId, action.id)}>
          {action.label}
        </button>
      ))}
    </div>
  );

  return (
    <div>
      <section aria-labelledby="capabilities-heading">
        <h2 id="capabilities-heading">Capabilities</h2>
        {data.capabilities.map((capability) => {
          const label = safeLabel(capability.label);
          if (capability.designOnly) {
            return (
              <article key={capability.id} aria-labelledby={`cap-${capability.id}`} data-design-only="true">
                <h3 id={`cap-${capability.id}`}>{capability.name}</h3>
                <p>
                  <span>{label}</span>
                </p>
                <p>Case study: {displayValue(capability.caseStudyAutonomy)}</p>
                <p>Demo: {displayValue(capability.demoAutonomy)}</p>
                <p>Purpose: {displayValue(capability.design?.purpose)}</p>
                <p>Intended autonomy: {displayValue(capability.design?.intendedAutonomy)}</p>
                <p>Human authority: {displayValue(capability.design?.humanAuthority)}</p>
                <p>{capability.design?.notice === 'Not implemented in this demo' ? capability.design.notice : 'Not implemented in this demo'}</p>
              </article>
            );
          }
          return (
            <article key={capability.id} aria-labelledby={`cap-${capability.id}`}>
              <h3 id={`cap-${capability.id}`}>{capability.name}</h3>
              <p><span>{label}</span></p>
              <p>Case study: {displayValue(capability.caseStudyAutonomy)}</p>
              <p>Demo: {displayValue(capability.demoAutonomy)}</p>
              {capability.openItems !== null && <p>Open items: {capability.openItems}</p>}
              {capability.nextDecisionOwner && <p>Next decision owner: {capability.nextDecisionOwner}</p>}
              {capability.id === 'records-investigation' && capability.evidence.map((link) => {
                const href = safeHref(link.url);
                return href ? <p key={link.label}><a href={href}>{link.label}</a></p> : null;
              })}
              {decisionControls(capability.id)}
            </article>
          );
        })}
        {!hasTechnicalReview && allowedDecisions.length > 0 && (
          <article aria-labelledby="technical-review-heading">
            <h3 id="technical-review-heading">Technical review</h3>
            {decisionControls('technical-review')}
          </article>
        )}
      </section>

      <section aria-labelledby="queue-heading">
        <h2 id="queue-heading">Queue</h2>
        <ul>
          {data.queue.map((item) => (
            <li key={item.itemId}>
              <span>{safeLabel(item.label)}</span> {item.itemId}
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="activity-heading">
        <h2 id="activity-heading">Activity</h2>
        <ol>
          {[...data.activity]
            .sort((a, b) => a.sequence - b.sequence)
            .map((event) => (
              <li key={event.sequence}>
                <span>{safeLabel(event.label)}</span> {event.actor}: {event.outcome}
              </li>
            ))}
        </ol>
      </section>
    </div>
  );
}
