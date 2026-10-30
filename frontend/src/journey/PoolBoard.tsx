import type { Session } from './api';
import { aiChipFor } from './aiBriefs';
import { aircraftFor, formatWhen, STEPS, waitingOn, type Actor } from './model';
import { ActorGlyph, ActorTag, AircraftArt, CheckIcon } from './Art';
import type { FleetEntry } from './useFleet';

type Props = {
  session: Session;
  entries: FleetEntry[];
  state: 'loading' | 'ready' | 'error';
  onOpen: (caseId: string) => void;
  onOpenHangar: () => void;
};

export default function PoolBoard({ session, entries, state, onOpen, onOpenHangar }: Props) {
  const waiting = entries.map((entry) => ({ entry, wait: waitingOn(entry.progress), step: STEPS[entry.progress.stepIndex] }));
  const humans = waiting.filter((item) => item.wait.actor === 'human' && item.step.id !== 'release').length;
  const ai = waiting.filter((item) => item.wait.actor === 'ai').length;
  const ready = waiting.filter((item) => item.step.id === 'release' && !item.entry.progress.released).length;
  const released = waiting.filter((item) => item.entry.progress.released).length;
  const mine = waiting.filter((item) => item.step.ownerRole === session.role && !item.entry.progress.released);
  const firstName = session.displayName.split(' ')[0];

  return (
    <main className="av-page av-board" id="main">
      <section className="av-board-head">
        <div>
          <h1>Return pool</h1>
          <p className="av-lede">
            {state === 'loading' ? 'Loading the fleet…' : (
              <>
                {entries.length} aircraft between leases.{' '}
                {mine.length > 0 ? `${mine.length} ${mine.length === 1 ? 'needs' : 'need'} a decision from you, ${firstName}.` : 'Nothing is waiting on you right now.'}
              </>
            )}
          </p>
        </div>
        <dl className="av-kpis">
          <Kpi label="In the pool" value={entries.length} />
          <Kpi label="Waiting on people" value={humans} actor="human" />
          <Kpi label="With the AI agent" value={ai} actor="ai" />
          <Kpi label="Ready for release" value={ready} tone="green" />
          <Kpi label="Released" value={released} tone="green" />
        </dl>
      </section>

      {state === 'error' && <p className="av-notice av-notice-alert" role="alert">The fleet could not be loaded. Refresh to try again.</p>}

      {mine.length > 0 && (
        <section className="av-mine" aria-labelledby="mine-heading">
          <h2 id="mine-heading">Waiting on you</h2>
          <ul>
            {mine.map(({ entry, step }) => {
              const aircraft = aircraftFor(entry.record.caseId);
              return (
                <li key={entry.record.caseId}>
                  <button type="button" className="av-mine-item" onClick={() => onOpen(entry.record.caseId)}>
                    <AircraftArt category={aircraft.category} className="av-mine-art" />
                    <span>
                      <strong>{step.title}</strong>
                      <span>{aircraft.manufacturer} {aircraft.model}, {aircraft.registration}</span>
                    </span>
                    <span className="av-button av-button-primary av-button-small">Review</span>
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      )}

      <section className="av-path" aria-labelledby="path-heading">
        <div className="av-path-title">
          <h2 id="path-heading">From return to redelivery</h2>
          <div className="av-legend" aria-label="Legend">
            <ActorTag actor="ai" label="AI agent" />
            <ActorTag actor="human" label="Human in the loop" />
            <ActorTag actor="rules" label="Automated rules" />
          </div>
        </div>
        <div className="av-path-scroll">
          <ol className="av-path-grid">
            {STEPS.map((step, index) => {
              const here = waiting.filter((item) => item.entry.progress.stepIndex === index);
              return (
                <li key={step.id} className={`av-lane av-lane-${step.actor}${here.length ? ' has-aircraft' : ''}`}>
                  <div className="av-gate">
                    <span className={`av-gate-marker av-gate-${step.actor}`}><ActorGlyph actor={step.actor} size={18} /></span>
                    <span className="av-gate-number">Gate {index + 1}</span>
                    <strong className="av-gate-name">{step.short}</strong>
                    <span className="av-gate-owner">{step.owner}</span>
                  </div>
                  <div className="av-lane-body">
                    {here.length === 0 && <span className="av-lane-empty">Clear</span>}
                    {here.map(({ entry, wait }) => {
                      const aircraft = aircraftFor(entry.record.caseId);
                      const ai = entry.progress.states.investigation === 'done' ? aiChipFor(entry.record.caseId) : null;
                      return (
                        <button key={entry.record.caseId} type="button" className={`av-plane-card${entry.progress.blocked ? ' is-blocked' : ''}${entry.progress.released ? ' is-released' : ''}`}
                          onClick={() => onOpen(entry.record.caseId)} aria-label={`${aircraft.manufacturer} ${aircraft.model} ${aircraft.registration}: ${wait.label}`}>
                          <AircraftArt category={aircraft.category} className="av-plane-card-art" />
                          <span className="av-plane-card-reg">{aircraft.registration}</span>
                          <span className="av-plane-card-model">{aircraft.manufacturer} {aircraft.model}</span>
                          <span className="av-plane-card-route">{aircraft.outgoingLessee}<span aria-hidden="true"> › </span>{aircraft.nextLessee}</span>
                          <span className={`av-wait av-wait-${entry.progress.released ? 'released' : wait.actor}`}>{entry.progress.released ? <CheckIcon /> : <ActorGlyph actor={wait.actor} size={12} />}{wait.label}</span>
                          {ai && <span className={`av-ai-chip av-ai-chip-${ai.severity}`}><ActorGlyph actor="ai" size={11} />{ai.label}</span>}
                          <span className="av-plane-card-when">{entry.progress.lastActivity ? `Updated ${formatWhen(entry.progress.lastActivity)}` : 'Just arrived'}</span>
                        </button>
                      );
                    })}
                  </div>
                </li>
              );
            })}
          </ol>
        </div>
      </section>

      <section className="av-hangar-teaser">
        <div>
          <h2>Walk the hangar in 3D</h2>
          <p>See the same aircraft parked in a virtual hangar, inspect their records spatially and open any case from the floor.</p>
        </div>
        <button type="button" className="av-button av-button-inverse" onClick={onOpenHangar}>Open the 3D hangar</button>
      </section>
    </main>
  );
}

function Kpi({ label, value, actor, tone }: { label: string; value: number; actor?: Actor; tone?: 'green' }) {
  return (
    <div className={`av-kpi${tone ? ` av-kpi-${tone}` : ''}${actor ? ` av-kpi-${actor}` : ''}`}>
      <dt>{actor && <ActorGlyph actor={actor} size={14} />}{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}
