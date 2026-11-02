import { useId, useState } from 'react';
import { ActorGlyph } from './Art';
import type { CopilotActivityItem } from './copilotApi';
import { formatWhen, mergeTrail, type ActivityItem } from './model';

type Filter = 'all' | 'copilot' | 'ui';
const FILTERS: { value: Filter; label: string }[] = [{ value: 'all', label: 'All' }, { value: 'copilot', label: 'Copilot' }, { value: 'ui', label: 'UI' }];
export const COPILOT_TRAIL_UNAVAILABLE = 'Copilot activity is unavailable right now. Showing activity recorded in the app only.';

type Props = { business: ActivityItem[]; copilot: CopilotActivityItem[] | 'unavailable' | null };

/** Read-only detail of one copilot invocation: identities, versions and codes only, never record text. */
function CopilotDetail({ item, id }: { item: CopilotActivityItem; id: string }) {
  return <dl id={id} className="av-activity-detail">
    <dt>Who</dt><dd>{item.user ?? 'unknown user'}</dd>
    <dt>Role</dt><dd>{item.role}</dd>
    <dt>Tool</dt><dd>{item.operation}</dd>
    <dt>Outcome</dt><dd>{`${item.outcome} (${item.httpStatus} ${item.resultCode})`}</dd>
    <dt>Records</dt><dd>{item.records.length === 0 ? 'None' : item.records.map((ref) => `${ref.sourceId} ${ref.recordId} v${ref.recordVersion}`).join(', ')}</dd>
    <dt>Screening</dt><dd>{item.screening === null ? 'None'
      : `${item.screening.state}${item.screening.flaggedFields.length > 0 ? `: ${item.screening.flaggedFields.join(', ')}` : ''}`}</dd>
    <dt>Proposal</dt><dd>{item.proposalId ?? 'None'}</dd>
    <dt>Gate</dt><dd>{item.gate === null ? 'None'
      : `${item.gate.name}: ${item.gate.result}${item.gate.blockers.length > 0 ? `; blockers: ${item.gate.blockers.join(', ')}` : ''}`}</dd>
  </dl>;
}

/** The case Activity panel: UI and copilot entries in one read-only timeline with an All, Copilot or UI filter. */
export default function ActivityTrail({ business, copilot }: Props) {
  const ids = useId();
  const [filter, setFilter] = useState<Filter>('all');
  const [open, setOpen] = useState<string | null>(null);
  const entries = mergeTrail(business, Array.isArray(copilot) ? copilot : [])
    .filter((entry) => filter === 'all' || entry.origin === filter);
  const empty = business.length === 0 && (!Array.isArray(copilot) || copilot.length === 0);

  return <aside className="av-activity" aria-labelledby="activity-heading">
    <h2 id="activity-heading">Activity</h2>
    <fieldset className="av-segmented av-activity-filter">
      <legend className="av-visually-hidden">Show activity</legend>
      {FILTERS.map(({ value, label }) => <label key={value} className={`av-segment${filter === value ? ' is-selected' : ''}`}>
        <input type="radio" name={`${ids}-filter`} value={value} checked={filter === value} onChange={() => setFilter(value)} />{label}
      </label>)}
    </fieldset>
    {copilot === 'unavailable' && <p className="av-notice" role="status">{COPILOT_TRAIL_UNAVAILABLE}</p>}
    {empty ? <p className="av-muted">No activity yet. The aircraft has just arrived.</p>
      : entries.length === 0 ? <p className="av-muted">No entries match this filter.</p> : (
        <ol>
          {entries.map((entry) => entry.origin === 'ui'
            ? <li key={entry.key} id={`case-activity-${entry.item.key}`} tabIndex={-1} className={`av-activity-${entry.item.actor}`}>
              <span className="av-activity-dot"><ActorGlyph actor={entry.item.actor} size={12} /></span>
              <div>
                <p>{entry.item.what}</p>
                {entry.item.reason && <p className="av-activity-reason">{entry.item.reason}</p>}
                <p className="av-activity-meta">{entry.item.who}, {formatWhen(entry.item.at)}</p>
              </div>
            </li>
            : <li key={entry.key} className="av-activity-ai av-activity-copilot">
              <span className="av-activity-dot"><ActorGlyph actor="ai" size={12} /></span>
              <div>
                <p><span className="av-activity-badge">Copilot</span> {`${entry.item.operation}: ${entry.item.outcome}`}</p>
                <p className="av-activity-meta">{entry.item.user ?? 'unknown user'}, {formatWhen(entry.item.at)}</p>
                <button type="button" className="av-button av-button-secondary av-button-small" aria-expanded={open === entry.key}
                  aria-controls={`${ids}-${entry.item.invocationId}`} onClick={() => setOpen(open === entry.key ? null : entry.key)}>
                  {`Details for ${entry.item.operation} at ${formatWhen(entry.item.at)}`}
                </button>
                {open === entry.key && <CopilotDetail item={entry.item} id={`${ids}-${entry.item.invocationId}`} />}
              </div>
            </li>)}
        </ol>
      )}
  </aside>;
}
