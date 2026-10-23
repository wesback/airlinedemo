import { useEffect, useRef, useState, type ComponentType } from 'react';
import CapabilityDashboard, { type DashboardData } from './CapabilityDashboard';
import SignIn, { type SignedInSession } from './components/SignIn';
import ReadinessSummary, { type ReadinessSummaryData } from './ReadinessSummary';
import InvestigationTrace, { type InvestigationTraceRecord, type InvestigationTraceView } from './InvestigationTrace';
import HangarLocalDiagnostics from './hangar/localDiagnostics';
import HangarGuideLineAdapter from './hangar/GuideLineAdapter';
import type { HangarGuideMarker } from './hangar/guideLine';
import type { HangarSceneProps } from './hangar/entryController';

type CaseProjection = { revision: number; basis: unknown[] };
type TechnicalReviewProjection = { basisVersion: number; decisions: { decision: string; current: boolean }[] };
type Projections = { caseState: CaseProjection; dashboard: DashboardData; review: TechnicalReviewProjection; readiness: ReadinessSummaryData };

class RequestError extends Error { constructor(readonly status: number) { super(`Request failed with status ${status}`); } }

async function getProjection<T>(url: string): Promise<T> { const response = await fetch(url); if (!response.ok) throw new RequestError(response.status); return response.json(); }

function loadError(error: unknown): string {
  return error instanceof RequestError && error.status === 401 ? 'Sign in again to view this case.'
    : error instanceof RequestError && error.status === 404 ? 'This case is not available to your account.'
      : 'Unable to load case data. Try again later.';
}

export type HangarEntryAppProps = {
  hangarContractVersion?: unknown;
  hangarBrowserSupported?: unknown;
  hangarWebGl2Probe?: unknown;
  hangarLazyLoader?: () => unknown | Promise<unknown>;
  hangarAircraftSelectionHandler?: (aircraftId: string) => unknown;
};

export default function App({
  hangarContractVersion: contractVersionProp,
  hangarBrowserSupported,
  hangarWebGl2Probe,
  hangarLazyLoader,
  hangarAircraftSelectionHandler,
}: HangarEntryAppProps = {}) {
  const [apiState, setApiState] = useState<'checking' | 'ready' | 'unavailable'>('checking');
  const [session, setSession] = useState<SignedInSession | null>(null);
  const [data, setData] = useState<Projections | null>(null);
  const [loadingCase, setLoadingCase] = useState(false);
  const [caseError, setCaseError] = useState('');
  const [reason, setReason] = useState('');
  const [decisionBusy, setDecisionBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [traceView, setTraceView] = useState<InvestigationTraceView | null>(null);
  const traceRequestId = useRef(0);
  const [hangarView, setHangarView] = useState<'regular' | 'loading' | '3d' | 'fallback' | 'contract-error'>('regular');
  const [hangarFallback, setHangarFallback] = useState<string | null>(null);
  const [hangarContractVersion] = useState<unknown>(contractVersionProp === undefined ? 1 : contractVersionProp);
  const [hangarSceneLoaded, setHangarSceneLoaded] = useState(false);
  const HangarSceneRef = useRef<ComponentType<HangarSceneProps> | null>(null);
  const [hangarActionMessage, setHangarActionMessage] = useState('');
  const hangarMotion = useRef<{ motion: { enable: () => boolean; disable: () => boolean; clearOverride: () => null; reduced: boolean }; camera: { overview: () => unknown }; avatar: { move: (target: {x:number;z:number}, duration?: number) => unknown } } | null>(null);
  const [hangarReducedMotion, setHangarReducedMotion] = useState(false);
  const [hangarDiagnostics, setHangarDiagnostics] = useState({ sceneLoadDurationMs: 0, webgl2Available: false, browserSupported: false, fallbackReason: null as 'unsupported_browser' | 'webgl_unavailable' | 'asset_invalid' | 'asset_load_failed' | 'render_failed' | 'refresh_failed' | null });
  const hangarDataRoomEntries = [
    { caseId: 'AIR-1', name: 'Air One', category: 'narrow-body', unavailable: false, sources: null },
    { caseId: 'AIR-2', name: 'Air Two', category: 'wide-body', unavailable: false, sources: null },
  ];
  const hangarDataRoomMarkers: Record<string, HangarGuideMarker> = {
    'AIR-1': { x: 4, z: -4 },
    'AIR-2': { x: -4, z: 4 },
  };
  const sharedHangarAircraftSelectionHandler = (aircraftId: string) => {
    hangarAircraftSelectionHandler?.(aircraftId);
  };

  function setHangarMotionOverride(enabled: boolean) {
    const controllers = hangarMotion.current;
    if (!controllers) return;
    enabled ? controllers.motion.enable() : controllers.motion.disable();
    setHangarReducedMotion(controllers.motion.reduced);
  }

  function clearHangarMotionOverride() {
    const controllers = hangarMotion.current;
    if (!controllers) return;
    controllers.motion.clearOverride();
    setHangarReducedMotion(controllers.motion.reduced);
  }

  function runNon3dAction(action: 'selection' | 'status' | 'evidence' | 'journey' | 'review') {
    const selector = {
      selection: '#case-heading',
      status: '.readiness-summary',
      evidence: '#capabilities-heading',
      journey: '#activity-heading',
      review: '#technical-review-heading, #capabilities-heading',
    }[action];
    const target = action === 'review'
      ? document.querySelector<HTMLElement>('#technical-review-heading')
        ?? document.querySelector<HTMLElement>('#cap-technical-review')
        ?? document.querySelector<HTMLElement>('#capabilities-heading')
      : document.querySelector<HTMLElement>(selector);
    if (target && !target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
    target?.focus();
    if (typeof target?.scrollIntoView === 'function') target.scrollIntoView();
    setHangarActionMessage({
      selection: 'Case selected.',
      status: 'Case status selected.',
      evidence: 'Case evidence selected.',
      journey: 'Case journey selected.',
      review: 'Technical review selected.',
    }[action]);
  }

  async function enterHangar() {
    const startedAt = typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now();
    setHangarView('loading');
    setHangarSceneLoaded(false);
    HangarSceneRef.current = null;
    const { isHangarBrowserSupported, probeWebGl2Capability, selectHangarEntry, loadHangarScene, createHangarSceneMotionControllers } = await import('./hangar/entryController');
    const browserSupported = hangarBrowserSupported !== undefined
      ? isHangarBrowserSupported(hangarBrowserSupported)
      : isHangarBrowserSupported(undefined);
    const webgl2 = hangarWebGl2Probe !== undefined
      ? hangarWebGl2Probe as never
      : probeWebGl2Capability();
    const hangarMotionControllers = createHangarSceneMotionControllers();
    hangarMotion.current = hangarMotionControllers;
    setHangarReducedMotion(hangarMotionControllers.motion.reduced);
    const decision = await selectHangarEntry({
      browserSupported,
      contractVersion: hangarContractVersion,
      webgl2,
    }, hangarLazyLoader ?? (() => loadHangarScene(hangarMotionControllers)));
    const endedAt = typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now();
    const fallbackReason = decision.fallbackReason && decision.fallbackReason !== 'contract_invalid'
      ? decision.fallbackReason as 'unsupported_browser' | 'webgl_unavailable'
      : null;
    const webgl2Available = webgl2 !== null && typeof webgl2 === 'object';
    setHangarDiagnostics({ sceneLoadDurationMs: Math.max(0, Math.round(endedAt - startedAt)), webgl2Available, browserSupported, fallbackReason });
    if (decision.mode === '3d') {
      if (typeof decision.loadedScene === 'function') HangarSceneRef.current = decision.loadedScene as ComponentType<HangarSceneProps>;
      setHangarSceneLoaded(true);
      setHangarView('3d');
    } else if (decision.fallbackReason === 'contract_invalid') setHangarView('contract-error');
    else {
      setHangarFallback(decision.fallbackReason);
      setHangarView('fallback');
    }
  }

  useEffect(() => {
    let active = true;
    fetch('/api/health')
      .then((response) => {
        if (!response.ok) throw new Error('API health check failed');
        return response.json();
      })
      .then(() => { if (active) setApiState('ready'); })
      .catch(() => { if (active) setApiState('unavailable'); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!session) {
      traceRequestId.current += 1;
      setData(null);
      setCaseError('');
      setLoadingCase(false);
      setReason('');
      setTraceView(null);
      return;
    }
    let active = true;
    setLoadingCase(true);
    setCaseError('');
    (async () => {
      try {
        const caseProjection = await getProjection<CaseProjection>('/api/cases/case-001');
        const dashboardProjection = await getProjection<DashboardData>('/api/cases/case-001/dashboard');
        const reviewProjection = await getProjection<TechnicalReviewProjection>('/api/cases/case-001/technical-reviews');
        const readiness = await getProjection<ReadinessSummaryData>('/api/cases/case-001/readiness');
        if (!active) return;
        setData({ caseState: caseProjection, dashboard: dashboardProjection, review: reviewProjection, readiness });
      } catch (error) {
        if (!active) return;
        setData(null);
        setCaseError(loadError(error));
      } finally {
        if (active) setLoadingCase(false);
      }
    })();
    return () => { active = false; };
  }, [session, refresh]);

  async function showTrace(url: string) {
    const requestId = ++traceRequestId.current;
    setTraceView({ status: 'loading' });
    try {
      const trace = await getProjection<InvestigationTraceRecord>(url);
      if (requestId === traceRequestId.current) setTraceView({ status: 'loaded', trace });
    } catch (error) {
      if (requestId !== traceRequestId.current) return;
      setTraceView(error instanceof RequestError && error.status === 404
        ? { status: 'not-found' }
        : { status: 'error' });
    }
  }

  async function submitDecision(capabilityId: string, decision: string) {
    if (
      capabilityId !== 'technical-review'
      || !['accept', 'reject'].includes(decision)
      || !data?.dashboard.allowedDecisions.some((action) => action.id === decision)
      || !session || !data
      || !Number.isInteger(data.caseState.revision) || !Number.isInteger(data.review.basisVersion)
      || !reason.trim()
    ) return;

    setDecisionBusy(true);
    setCaseError('');
    try {
      const response = await fetch('/api/cases/case-001/technical-reviews', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': session.csrfToken },
        body: JSON.stringify({
          expectedRevision: data.caseState.revision,
          expectedBasisVersion: data.review.basisVersion,
          decision,
          reason: reason.trim(),
        }),
      });
      if (response.status === 403) {
        setCaseError('You are not permitted to record this decision.');
        return;
      }
      if (response.status === 409) {
        setCaseError('The case changed. Refresh the case before deciding.');
        return;
      }
      if (!response.ok) throw new RequestError(response.status);
      setRefresh((value) => value + 1);
    } catch {
      setCaseError('Unable to record the decision. Try again later.');
    } finally {
      setDecisionBusy(false);
    }
  }

  const currentDecision = data?.review.decisions.filter((item) => item.current).at(-1)?.decision;
  const healthMessage = {
    checking: 'Checking service connection…',
    ready: 'Service connected',
    unavailable: 'Service unavailable. Try again later.',
  }[apiState];

  const LoadedHangarScene = HangarSceneRef.current;
  return (
    <main>
      <h1>Airline asset review</h1>
      <p>Reviewer workspace</p>
      <p role="status" aria-live="polite">{healthMessage}</p>
      <SignIn onSessionChange={setSession} />
      {session && (
        <section aria-labelledby="case-heading">
          {data && <ReadinessSummary summary={data.readiness} />}
          <h2 id="case-heading" tabIndex={-1}>Case case-001</h2>
          {loadingCase && <p role="status">Loading case data…</p>}
          {caseError && <p role="alert">{caseError}</p>}
          {data && (
            <>
              <p>Revision: {data.caseState.revision}</p>
              <p>Evidence basis: {data.caseState.basis.length}</p>
              <p>Technical-review decision: {currentDecision ?? 'No current decision'}</p>
              <button type="button" onClick={() => { void enterHangar(); }}>Enter Hangar</button>
              {hangarView !== 'regular' && hangarView !== 'loading' && (
                <section aria-labelledby="hangar-heading">
                  <h2 id="hangar-heading">Hangar</h2>
                  <HangarLocalDiagnostics
                    session={session}
                    {...hangarDiagnostics}
                  />
                  {hangarView === '3d' && hangarSceneLoaded && (
                    <div aria-label="Hangar 3D scene">
                      {LoadedHangarScene ? <LoadedHangarScene onSelect={sharedHangarAircraftSelectionHandler} /> : <canvas aria-label="3D hangar scene" />}
                      <HangarGuideLineAdapter
                        entries={hangarDataRoomEntries}
                        markers={hangarDataRoomMarkers}
                        avatar={{ x: 0, z: 0 }}
                        config={{
                          walkablePolygon: [{ x: -5, z: -5 }, { x: 5, z: -5 }, { x: 5, z: 5 }, { x: -5, z: 5 }],
                          avatarRadius: 0.3,
                          gridSize: 0.25,
                          avatar: { x: 0, z: 0 },
                          onSelect: sharedHangarAircraftSelectionHandler,
                        }}
                        onSelect={sharedHangarAircraftSelectionHandler}
                        reducedMotion={hangarReducedMotion}
                      />
                      <div aria-label="Reduced motion controls">
                        <button type="button" aria-pressed={hangarReducedMotion} onClick={() => setHangarMotionOverride(true)}>Enable reduced motion</button>
                        <button type="button" aria-pressed={!hangarReducedMotion} onClick={() => setHangarMotionOverride(false)}>Disable reduced motion</button>
                        <button type="button" onClick={clearHangarMotionOverride}>Use system preference</button>
                      </div>
                    </div>
                  )}
                  {hangarView === 'fallback' && <p role="alert">{hangarFallback === 'unsupported_browser'
                    ? 'Your browser is not supported. Use the current or previous major version of Chrome or Edge.'
                    : 'WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2-capable browser.'}</p>}
                  {hangarView === 'contract-error' && <p role="alert">Unsupported hangar contract version {String(hangarContractVersion)}. <a href="#non-3d-list">Use the supported contract version in the non-3D list.</a></p>}
                  {(hangarView === 'fallback' || hangarView === 'contract-error') && (
                    <section id="non-3d-list" aria-labelledby="non-3d-heading">
                      <h3 id="non-3d-heading">Non-3D hangar</h3>
                      <ul>
                        <li><button type="button" onClick={() => runNon3dAction('selection')}>Select case</button></li>
                        <li><button type="button" onClick={() => runNon3dAction('status')}>View status</button></li>
                        <li><button type="button" onClick={() => runNon3dAction('evidence')}>View evidence</button></li>
                        <li><button type="button" onClick={() => runNon3dAction('journey')}>Follow journey</button></li>
                        <li><button type="button" onClick={() => runNon3dAction('review')}>Open review</button></li>
                        <li><p role="status">{hangarActionMessage}</p></li>
                      </ul>
                    </section>
                  )}
                </section>
              )}
              <CapabilityDashboard
                data={data.dashboard}
                decisionReason={reason}
                onDecisionReasonChange={setReason}
                onTraceSelect={(url) => { void showTrace(url); }}
                onDecision={(capabilityId, decision) => {
                  if (!decisionBusy) void submitDecision(capabilityId, decision);
                }}
              />
              {traceView && <InvestigationTrace view={traceView} />}
            </>
          )}
        </section>
      )}
    </main>
  );
}
