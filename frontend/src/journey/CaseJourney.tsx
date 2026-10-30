import { useCallback, useEffect, useRef, useState } from 'react';
import { caseUrl, getJson, type CaseHistory, type CaseRecord, type ObligationRecord, type Readiness, type ReserveDraft, type Session, type TechnicalReviews } from './api';
import { activityFrom, aircraftFor, deriveProgress, formatWhen, STEPS, waitingOn, type StepId } from './model';
import { ActorGlyph, ActorTag, AircraftArt, CheckIcon } from './Art';
import { StepPanel, type JourneyData } from './StepPanels';
import { aiBriefFor } from './aiBriefs';
import { AiInsightCard } from './AiInsight';

type Props = {
  session: Session;
  caseId: string;
  onBack: () => void;
  onSwitchPersona: (userId: string) => void;
  onChanged: () => void;
};

const optional = <T,>(url: string) => getJson<T>(url).catch(() => null);

export default function CaseJourney({ session, caseId, onBack, onSwitchPersona, onChanged }: Props) {
  const [data, setData] = useState<JourneyData | null>(null);
  const [failed, setFailed] = useState(false);
  const [selected, setSelected] = useState<StepId | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);

  const load = useCallback(async () => {
    try {
      const record = await getJson<CaseRecord>(caseUrl(caseId));
      const [history, reviews, reserve, obligations, readiness] = await Promise.all([
        optional<CaseHistory>(caseUrl(caseId, '/history')),
        optional<TechnicalReviews>(caseUrl(caseId, '/technical-reviews')),
        optional<ReserveDraft>(caseUrl(caseId, '/reserve')),
        optional<ObligationRecord>(caseUrl(caseId, '/obligations')),
        optional<Readiness>(caseUrl(caseId, '/readiness')),
      ]);
      setData({ record, progress: deriveProgress(record), history, reviews, reserve, obligations, readiness });
      setFailed(false);
    } catch {
      setFailed(true);
    }
  }, [caseId]);

  useEffect(() => { setData(null); setSelected(null); void load(); }, [load, session.id]);
  useEffect(() => { headingRef.current?.focus(); }, [caseId]);

  const refresh = useCallback(async () => { await load(); onChanged(); }, [load, onChanged]);

  const aircraft = aircraftFor(caseId);
  if (failed) {
    return (
      <main className="av-page" id="main">
        <button type="button" className="av-back" onClick={onBack}>Return pool</button>
        <p className="av-notice av-notice-alert" role="alert">This aircraft is not available to you. It may be outside your case scope.</p>
      </main>
    );
  }

  const progress = data?.progress;
  const activeId: StepId = selected ?? STEPS[progress?.stepIndex ?? 0].id;
  const activeIndex = STEPS.findIndex((step) => step.id === activeId);
  const step = STEPS[activeIndex];
  const wait = progress ? waitingOn(progress) : null;
  const passed = progress ? STEPS.filter((item) => progress.states[item.id] === 'done').length : 0;

  return (
    <main className="av-page av-journey" id="main">
      <button type="button" className="av-back" onClick={onBack}>
        <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M10 3.5L5.5 8l4.5 4.5" fill="none" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" /></svg>
        Return pool
      </button>

      <section className="av-hero">
        <div className="av-hero-text">
          <p className="av-hero-type">{aircraft.manufacturer} {aircraft.model}</p>
          <h1 ref={headingRef} tabIndex={-1}>{aircraft.registration}</h1>
          <div className="av-transfer">
            <span><small>From</small>{aircraft.outgoingLessee}</span>
            <svg viewBox="0 0 120 24" aria-hidden="true" preserveAspectRatio="none"><path d="M2 18 C 40 18 70 6 118 6" /></svg>
            <span><small>To</small>{aircraft.nextLessee}</span>
          </div>
          {wait && (
            <p className={`av-wait av-wait-${wait.actor} av-wait-large`}><ActorGlyph actor={wait.actor} size={14} />{wait.label}</p>
          )}
        </div>
        <div className="av-hero-art">
          <AircraftArt category={aircraft.category} title={`${aircraft.manufacturer} ${aircraft.model} side view`} />
        </div>
        <div className="av-hero-progress" aria-label={`${passed} of ${STEPS.length} gates passed`}>
          <span className="av-hero-progress-bar"><span className={`av-progress-fill av-progress-${passed}`} /></span>
          <span>{passed} of {STEPS.length} gates passed</span>
        </div>
      </section>

      {!data ? <p className="av-loading" role="status">Loading the journey…</p> : (
        <div className="av-journey-grid">
          <nav className="av-rail" aria-label="Journey steps">
            <ol>
              {STEPS.map((item, index) => {
                const state = data.progress.states[item.id];
                return (
                  <li key={item.id}>
                    <button type="button" className={`av-rail-step is-${state}${item.id === activeId ? ' is-active' : ''}`}
                      aria-current={item.id === activeId ? 'step' : undefined} onClick={() => setSelected(item.id)}>
                      <span className={`av-rail-dot av-rail-dot-${item.actor}`}>{state === 'done' ? <CheckIcon /> : <ActorGlyph actor={item.actor} size={14} />}</span>
                      <span className="av-rail-text">
                        <span className="av-rail-index">Gate {index + 1}</span>
                        <strong>{item.short}</strong>
                        <span className={`av-rail-actor av-rail-actor-${item.actor}`}>{item.actorLabel}</span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ol>
          </nav>

          <section className="av-panel" aria-labelledby="step-heading">
            <header className="av-panel-head">
              <div>
                <p className="av-panel-kicker">Gate {activeIndex + 1} of {STEPS.length}</p>
                <h2 id="step-heading">{step.title}</h2>
              </div>
              <ActorTag actor={step.actor} label={step.actorLabel} />
            </header>
            <p className="av-panel-desc">{step.description}</p>
            <div className="av-panel-body">
              <StepPanel step={step} data={data} session={session} onChanged={async () => { setSelected(activeId); await refresh(); }} onSwitchPersona={onSwitchPersona} />
            </div>
            <footer className="av-panel-foot">
              <button type="button" className="av-button av-button-ghost" disabled={activeIndex === 0} onClick={() => setSelected(STEPS[activeIndex - 1].id)}>Previous gate</button>
              <button type="button" className="av-button av-button-secondary" disabled={activeIndex === STEPS.length - 1} onClick={() => setSelected(STEPS[activeIndex + 1].id)}>Next gate</button>
            </footer>
          </section>

          <div className="av-journey-side">
          {(() => {
            const brief = aiBriefFor(data.record.caseId, data.history);
            return brief && activeId !== 'investigation'
              ? <AiInsightCard brief={brief} onOpen={() => setSelected('investigation')} />
              : null;
          })()}
          <aside className="av-activity" aria-labelledby="activity-heading">
            <h2 id="activity-heading">Activity</h2>
            {activityFrom(data.record.audit).length === 0 ? <p className="av-muted">No activity yet. The aircraft has just arrived.</p> : (
              <ol>
                {activityFrom(data.record.audit).map((item) => (
                  <li key={item.key} className={`av-activity-${item.actor}`}>
                    <span className="av-activity-dot"><ActorGlyph actor={item.actor} size={12} /></span>
                    <div>
                      <p>{item.what}</p>
                      {item.reason && <p className="av-activity-reason">{item.reason}</p>}
                      <p className="av-activity-meta">{item.who}, {formatWhen(item.at)}</p>
                    </div>
                  </li>
                ))}
              </ol>
            )}
          </aside>
          </div>
        </div>
      )}
    </main>
  );
}
