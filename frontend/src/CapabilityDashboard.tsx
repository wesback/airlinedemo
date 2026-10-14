export const KNOWN_LABELS = ['AI agent', 'Deterministic workflow', 'Human decision', 'Design only'] as const;

export type EvidenceLink = { label: string; url: string };
export type DecisionAction = { id: string; label: string };
export type DesignDetails = {
  purpose: string;
  intendedAutonomy: string;
  humanAuthority: string;
  notice: string;
};
export type Capability = {
  id: string;
  name: string;
  label: string;
  designOnly: boolean;
  openItems: number | null;
  nextDecisionOwner: string | null;
  evidence: EvidenceLink[];
  actions: DecisionAction[];
  design: DesignDetails | null;
};
export type ActivityItem = {
  sequence: number;
  actor: string;
  label: string;
  outcome: string;
  traceUrl: string | null;
};
export type QueueItem = { id: string; name: string; label: string };
export type DashboardData = { capabilities: Capability[]; queue: QueueItem[]; activity: ActivityItem[] };

export function safeLabel(label: unknown): string {
  return typeof label === 'string' && (KNOWN_LABELS as readonly string[]).includes(label) ? label : 'Unknown';
}

// Only same-origin relative paths are linkable; anything else (javascript:, data:, //host, /\host) is dropped.
// Browsers strip tabs and newlines from URLs, so those are rejected too (e.g. "/\t/host").
export function safeHref(url: string | null | undefined): string | null {
  if (typeof url !== 'string') return null;
  return /^\/(?![/\\])/.test(url) && !/[\\\t\n\r]/.test(url) ? url : null;
}

type Props = {
  data: DashboardData;
  onDecision?: (capabilityId: string, decisionId: string) => void;
};

export default function CapabilityDashboard({ data, onDecision }: Props) {
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
                <p>Purpose: {capability.design?.purpose ?? ''}</p>
                <p>Intended autonomy: {capability.design?.intendedAutonomy ?? ''}</p>
                <p>Human authority: {capability.design?.humanAuthority ?? ''}</p>
                <p>Not implemented in this demo</p>
              </article>
            );
          }
          return (
            <article key={capability.id} aria-labelledby={`cap-${capability.id}`}>
              <h3 id={`cap-${capability.id}`}>{capability.name}</h3>
              <p><span>{label}</span></p>
              {capability.openItems !== null && <p>Open items: {capability.openItems}</p>}
              {capability.nextDecisionOwner && <p>Next decision owner: {capability.nextDecisionOwner}</p>}
              {capability.evidence.map((link) => {
                const href = safeHref(link.url);
                return href ? <p key={link.label}><a href={href}>{link.label}</a></p> : null;
              })}
              {capability.actions.length > 0 && (
                <div role="group" aria-label={`${capability.name} decisions`}>
                  {capability.actions.map((action) => (
                    <button
                      key={action.id}
                      type="button"
                      onClick={() => onDecision?.(capability.id, action.id)}
                    >
                      {action.label}
                    </button>
                  ))}
                </div>
              )}
            </article>
          );
        })}
      </section>

      <section aria-labelledby="queue-heading">
        <h2 id="queue-heading">Queue</h2>
        <ul>
          {data.queue.map((item) => (
            <li key={item.id}>
              <span>{safeLabel(item.label)}</span> {item.name}
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="activity-heading">
        <h2 id="activity-heading">Activity</h2>
        <ol>
          {[...data.activity]
            .sort((a, b) => a.sequence - b.sequence)
            .map((event) => {
              const href = safeLabel(event.label) === 'AI agent' ? safeHref(event.traceUrl) : null;
              return (
                <li key={event.sequence}>
                  <span>{safeLabel(event.label)}</span> {event.actor}: {event.outcome}
                  {href && <> <a href={href}>Trace</a></>}
                </li>
              );
            })}
        </ol>
      </section>
    </div>
  );
}
