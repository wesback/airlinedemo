import { useCallback, useEffect, useRef, useState } from 'react';
import { caseUrl, getJson, type CaseHistory, type CaseRecord, type ObligationRecord, type Readiness, type ReserveDraft, type Session, type TechnicalReviews } from './api';
import { activityFrom, aircraftFor, deriveProgress, STEPS, waitingOn, type StepId } from './model';
import ActivityTrail from './ActivityTrail';
import { getCopilotActivity, type CopilotActivityItem } from './copilotApi';
import { ActorGlyph, ActorTag, AircraftArt, CheckIcon } from './Art';
import { StepPanel, type JourneyData } from './StepPanels';
import { aiBriefFor } from './aiBriefs';
import { AiInsightCard } from './AiInsight';
import ComplianceEvidence from './ComplianceEvidence';

type Props = {
  session: Session;
  caseId: string;
  onBack: () => void;
  onSwitchPersona: (userId: string) => void;
  onChanged: () => void;
  onOpenCase: (caseId: string) => void;
};

const optional = <T,>(url: string) => getJson<T>(url).catch(() => null);

export default function CaseJourney({ session, caseId, onBack, onSwitchPersona, onChanged, onOpenCase }: Props) {
  const [data, setData] = useState<JourneyData | null>(null);
  const [dataSessionId, setDataSessionId] = useState<string | null>(null);
  const [failedSessionId, setFailedSessionId] = useState<string | null>(null);
  const [selected, setSelected] = useState<StepId | null>(null);
  const [evidenceOpen, setEvidenceOpen] = useState(false);
  const [copilotTrail, setCopilotTrail] = useState<CopilotActivityItem[] | 'unavailable' | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const loadGeneration = useRef(0);

  const load = useCallback(async () => {
    const activeGeneration = ++loadGeneration.current;
    try {
      const record = await getJson<CaseRecord>(caseUrl(caseId));
      const [history, reviews, reserve, obligations, readiness, copilot] = await Promise.all([
        optional<CaseHistory>(caseUrl(caseId, '/history')),
        optional<TechnicalReviews>(caseUrl(caseId, '/technical-reviews')),
        optional<ReserveDraft>(caseUrl(caseId, '/reserve')),
        optional<ObligationRecord>(caseUrl(caseId, '/obligations')),
        optional<Readiness>(caseUrl(caseId, '/readiness')),
        getCopilotActivity(caseId),
      ]);
      if (activeGeneration !== loadGeneration.current) return;
      setCopilotTrail(copilot);
      setData({ record, progress: deriveProgress(record, history), history, reviews, reserve, obligations, readiness });
      setDataSessionId(session.id);
      setFailedSessionId(null);
    } catch {
      if (activeGeneration !== loadGeneration.current) return;
      setFailedSessionId(session.id);
    }
  }, [caseId, session.id]);

  useEffect(() => {
    setData(null);
    setDataSessionId(null);
    setFailedSessionId(null);
    setCopilotTrail(null);
    setSelected(null);
    setEvidenceOpen(false);
    void load();
    return () => { loadGeneration.current += 1; };
  }, [load]);
  useEffect(() => { headingRef.current?.focus(); }, [caseId]);

  const refresh = useCallback(async () => { await load(); onChanged(); }, [load, onChanged]);

  const aircraft = aircraftFor(caseId);
  const currentData = dataSessionId === session.id ? data : null;
  const failed = failedSessionId === session.id;
  const sourceDemo = caseId === 'case-001' && !currentData?.record.arrival;
  if (failed) {
    return (
      <main className="av-page" id="main">
        <button type="button" className="av-back" onClick={onBack}>Return pool</button>
        <p className="av-notice av-notice-alert" role="alert">This aircraft is not available to you. It may be outside your case scope.</p>
      </main>
    );
  }

  const progress = currentData?.progress;
  const arrivalIncomplete = Boolean(currentData?.record.arrival && !currentData.progress.arrivalReady);
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
          <p className="av-hero-type">{sourceDemo ? 'Synthetic source-system demonstration' : `${aircraft.manufacturer} ${aircraft.model}`}</p>
          <h1 ref={headingRef} tabIndex={-1}>{sourceDemo ? caseId : aircraft.registration}</h1>
          {!sourceDemo && <div className="av-transfer">
            <span><small>From</small>{aircraft.outgoingLessee}</span>
            <svg viewBox="0 0 120 24" aria-hidden="true" preserveAspectRatio="none"><path d="M2 18 C 40 18 70 6 118 6" /></svg>
            <span><small>To</small>{aircraft.nextLessee}</span>
          </div>}
          {wait && (
            <p className={`av-wait av-wait-${wait.actor} av-wait-large`}><ActorGlyph actor={wait.actor} size={14} />{wait.label}</p>
          )}
        </div>
        <div className="av-hero-art">
          <AircraftArt category={aircraft.category} title={sourceDemo ? 'Synthetic source-system case illustration' : `${aircraft.manufacturer} ${aircraft.model} side view`} />
        </div>
        <div className="av-hero-progress" aria-label={`${passed} of ${STEPS.length} gates passed`}>
          <span className="av-hero-progress-bar"><span className={`av-progress-fill av-progress-${passed}`} /></span>
          <span>{passed} of {STEPS.length} gates passed</span>
        </div>
      </section>

      {!currentData ? <p className="av-loading" role="status">Loading the journey…</p> : (
        <div className="av-journey-grid">
          <nav className="av-rail" aria-label="Journey steps">
            <ol>
              {STEPS.map((item, index) => {
                const state = currentData.progress.states[item.id];
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

          <section className="av-panel" aria-labelledby={evidenceOpen ? 'evidence-heading' : 'step-heading'}>
            {evidenceOpen ? <ComplianceEvidence key={`${caseId}:${session.id}`} caseId={caseId} onClose={() => setEvidenceOpen(false)}
              sourceCase={!sourceDemo && session.caseScopes.includes('case-001')
                ? { id: 'case-001', open: () => onOpenCase('case-001') } : undefined} /> : <>
            <header className="av-panel-head">
              <div>
                <p className="av-panel-kicker">Gate {activeIndex + 1} of {STEPS.length}</p>
                <h2 id="step-heading">{step.title}</h2>
              </div>
              <ActorTag actor={step.actor} label={step.actorLabel} />
            </header>
            <p className="av-panel-desc">{step.description}</p>
            <div className="av-panel-body">
              {arrivalIncomplete && activeId !== 'arrival' ? (
                <div className="av-arrival-gate-blocker" role="alert">
                  <strong>This gate is blocked until arrival is complete.</strong>
                  {currentData.progress.missingSources.length > 0
                    ? <p>Missing sources: {currentData.progress.missingSources.join(', ')}</p>
                    : <p>The presenter must complete the arrival step before this gate can continue.</p>}
                </div>
              ) : <StepPanel step={step} data={currentData} session={session}
                onChanged={async () => { setSelected(activeId === 'arrival' ? null : activeId); await refresh(); }} onSwitchPersona={onSwitchPersona} />}
            </div>
            <footer className="av-panel-foot">
              <button type="button" className="av-button av-button-ghost" disabled={activeIndex === 0} onClick={() => setSelected(STEPS[activeIndex - 1].id)}>Previous gate</button>
              <button type="button" className="av-button av-button-secondary" disabled={activeIndex === STEPS.length - 1 || arrivalIncomplete} onClick={() => setSelected(STEPS[activeIndex + 1].id)}>Next gate</button>
            </footer>
            </>}
          </section>

          <div className="av-journey-side">
          {session.role !== 'executive' && <aside className="av-activity" aria-label="Decision dossier">
            <h2>Decision dossier</h2>
            <p className="av-muted">Requirements, pinned record versions and recorded reviews.</p>
            <button type="button" className="av-button av-button-secondary" onClick={() => setEvidenceOpen(true)}>Compliance evidence</button>
          </aside>}
          {(() => {
            const brief = aiBriefFor(currentData.record.caseId, currentData.history);
            return brief && activeId !== 'investigation'
              ? <AiInsightCard brief={brief} onOpen={() => setSelected('investigation')} />
              : null;
          })()}
          <ActivityTrail business={activityFrom(currentData.record.audit)} copilot={copilotTrail} />
          </div>
        </div>
      )}
    </main>
  );
}
