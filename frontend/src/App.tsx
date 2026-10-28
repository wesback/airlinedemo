import { useEffect, useRef, useState, type ComponentType, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import CapabilityDashboard, { type DashboardData } from './CapabilityDashboard';
import SignIn, { type SignedInSession } from './components/SignIn';
import ReadinessSummary, { type ReadinessSummaryData } from './ReadinessSummary';
import InvestigationTrace, { type InvestigationTraceRecord, type InvestigationTraceView } from './InvestigationTrace';
import HangarLocalDiagnostics from './hangar/localDiagnostics';
import HangarGuideLineAdapter from './hangar/GuideLineAdapter';
import type { HangarGuideMarker } from './hangar/guideLine';
import type { HangarSceneProps } from './hangar/entryController';
import { validateHangarSceneEnvelope } from './hangar/sceneProjection';
import { canAdmitHangarCommands, refreshHangarScene, snapshotFromServer, type ServerSceneSnapshot } from './hangar/sceneRefresh';
import HangarSemanticControls from './hangar/semanticControls';
import HangarSelectionEvidence from './hangar/selectionPanel';
import HangarGuidedJourney from './hangar/guidedJourney';
import HangarCaseReset from './hangar/caseReset';
import HangarErrorBoundary, { HangarFallbackPanel } from './hangar/errorBoundary';
import hangarManifest from './hangar/sceneManifest.json';
import { HANGAR_SOURCE_IDS, type HangarSourceSummaryEntry } from './hangar/sourceSummary';
import { createHangarWalkController } from './hangar/walkController';
import { createHangarFollowCamera, type HangarFollowCamera } from './hangar/followCamera';
import HangarNon3dDataRoom from './hangar/non3dDataRoom';
import WorkspaceShell from './components/WorkspaceShell';
import RoleNavigation from './components/RoleNavigation';
import CaseSearch from './components/CaseSearch';
import { resolveReturnTo, validateReturnTo } from './routing/returnTo';

type CaseProjection = { revision: number; basis: unknown[] };
type TechnicalReviewProjection = { basisVersion: number; decisions: { decision: string; current: boolean }[] };
type Projections = { caseState: CaseProjection; dashboard: DashboardData; review: TechnicalReviewProjection; readiness: ReadinessSummaryData };

const PERSONA_OPTIONS = [
  { id: 'presenter', label: 'Demo presenter', role: 'presenter' },
  { id: 'reviewer', label: 'Technical reviewer', role: 'technical-reviewer' },
  { id: 'fin-1', label: 'Finance reviewer', role: 'finance-reviewer' },
  { id: 'legal-1', label: 'Legal reviewer', role: 'legal-reviewer' },
  { id: 'exec-1', label: 'Executive observer', role: 'executive' },
] as const;

const ROLE_HOME_PATHS: Record<string, string> = {
  'technical-reviewer': '/work',
  'finance-reviewer': '/work',
  'legal-reviewer': '/work',
  executive: '/portfolio',
  presenter: '/demo',
};

const ROLE_ALLOWED_PATHS: Record<string, string[]> = {
  'technical-reviewer': ['/work', '/cases', '/cases/:caseId/overview', '/cases/:caseId/findings', '/cases/:caseId/history', '/hangar'],
  'finance-reviewer': ['/work', '/cases', '/cases/:caseId/overview', '/cases/:caseId/reserve', '/cases/:caseId/history', '/hangar'],
  'legal-reviewer': ['/work', '/cases', '/cases/:caseId/overview', '/cases/:caseId/obligations', '/cases/:caseId/history', '/hangar'],
  executive: ['/portfolio', '/cases', '/cases/:caseId/overview', '/cases/:caseId/history', '/hangar'],
  presenter: ['/work', '/portfolio', '/demo', '/cases', '/cases/:caseId/overview', '/cases/:caseId/findings', '/cases/:caseId/reserve', '/cases/:caseId/obligations', '/cases/:caseId/history', '/hangar'],
};

function normalizeRoutePath(value: string | null | undefined): string {
  if (typeof value !== 'string' || !value.trim()) return '/';
  const trimmed = value.trim();
  if (!trimmed.startsWith('/')) return '/';
  try {
    const parsed = new URL(trimmed, 'http://localhost');
    return parsed.pathname + parsed.search;
  } catch {
    return '/';
  }
}

function resolveSessionCaseId(session: SignedInSession | null, currentCaseId: string | null): string | null {
  if (!session) return null;
  if (currentCaseId && session.caseScopes.includes(currentCaseId)) return currentCaseId;
  return session.caseScopes[0] ?? null;
}

function routeMatchesRole(pathname: string, role: string): boolean {
  const normalized = normalizeRoutePath(pathname).split('?')[0];
  const allowed = ROLE_ALLOWED_PATHS[role] ?? [];
  if (!allowed.length) return false;
  return allowed.some((pattern) => {
    const patternPath = pattern.split('?')[0];
    if (patternPath.includes(':caseId')) {
      const regex = new RegExp(`^${patternPath.replace(/:caseId/g, '[^/]+')}$`);
      return regex.test(normalized);
    }
    return normalized === patternPath;
  });
}

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
  hangarMonotonicClock?: () => number;
  hangarAircraftSelectionHandler?: (aircraftId: string) => unknown;
  hangarClientSnapshot?: unknown;
};

export default function App({
  hangarContractVersion: contractVersionProp,
  hangarBrowserSupported,
  hangarWebGl2Probe,
  hangarLazyLoader,
  hangarMonotonicClock,
  hangarAircraftSelectionHandler,
  hangarClientSnapshot,
}: HangarEntryAppProps = {}) {
  const [apiState, setApiState] = useState<'checking' | 'ready' | 'unavailable'>('checking');
  const [session, setSession] = useState<SignedInSession | null>(null);
  const [selectedCaseId, setSelectedCaseId] = useState<string | null>(null);
  const [data, setData] = useState<Projections | null>(null);
  const [routePath, setRoutePath] = useState<string>(() => (typeof window !== 'undefined' ? window.location.pathname + window.location.search : '/'));
  const [returnTo, setReturnTo] = useState<string | null>(() => {
    if (typeof window === 'undefined') return null;
    const value = new URLSearchParams(window.location.search).get('returnTo');
    return validateReturnTo(value);
  });
  const [loadingCase, setLoadingCase] = useState(false);
  const [caseError, setCaseError] = useState('');
  const [reason, setReason] = useState('');
  const [decisionBusy, setDecisionBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [traceView, setTraceView] = useState<InvestigationTraceView | null>(null);
  const traceRequestId = useRef(0);
  const [hangarView, setHangarView] = useState<'regular' | 'loading' | '3d' | 'fallback' | 'contract-error'>('regular');
  const [routeError, setRouteError] = useState<string | null>(null);
  const [hangarFallback, setHangarFallback] = useState<string | null>(null);
  const [hangarContractVersion] = useState<unknown>(contractVersionProp === undefined ? 1 : contractVersionProp);
  const [hangarSceneLoaded, setHangarSceneLoaded] = useState(false);
  const [hangarEntries, setHangarEntries] = useState<Array<HangarSourceSummaryEntry>>([
    { caseId: 'cp-case-a', name: 'Case A', category: 'narrow-body', unavailable: false, sources: HANGAR_SOURCE_IDS.map(sourceId => ({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`})) },
    { caseId: 'cp-case-b', name: 'Case B', category: 'regional', unavailable: false, sources: HANGAR_SOURCE_IDS.map(sourceId => ({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`})) },
    { caseId: 'cp-case-c', name: 'Case C', category: 'wide-body', unavailable: false, sources: HANGAR_SOURCE_IDS.map(sourceId => ({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`})) },
  ]);
  const [hangarSnapshot, setHangarSnapshot] = useState<ServerSceneSnapshot|null>(null);
  const [hangarCaseSnapshots, setHangarCaseSnapshots] = useState<Record<string, ServerSceneSnapshot>>({});
  const [hangarSceneEnvelopes, setHangarSceneEnvelopes] = useState<Record<string, unknown>>({});
  const [hangarSelectedObjectId, setHangarSelectedObjectId] = useState<string | null>(null);
  const [hangarSelectedCaseId, setHangarSelectedCaseId] = useState<string>('cp-case-a');
  const hangarSnapshotRef = useRef<ServerSceneSnapshot|null>(null);
  const hangarCaseSnapshotsRef = useRef<Record<string, ServerSceneSnapshot>>({});
  const hangarRefreshQueue = useRef<Promise<void>>(Promise.resolve());
  const [hangarStale, setHangarStale] = useState(false);
  const HangarSceneRef = useRef<ComponentType<HangarSceneProps> | null>(null);
  const [hangarActionMessage, setHangarActionMessage] = useState('');
  const hangarMotion = useRef<{ motion: { enable: () => boolean; disable: () => boolean; clearOverride: () => null; reduced: boolean }; camera: { overview: () => unknown }; avatar: { move: (target: {x:number;z:number}, duration?: number) => unknown } } | null>(null);
  const [hangarReducedMotion, setHangarReducedMotion] = useState(false);
  const hangarFollowCameraRef = useRef<HangarFollowCamera | null>(null);
  const hangarWalkControllerRef = useRef<ReturnType<typeof createHangarWalkController> | null>(null);
  const [hangarAvatarPosition, setHangarAvatarPosition] = useState({ x: 0, y: 0, z: 0 });
  const [hangarCameraPose, setHangarCameraPose] = useState({ x: 0, y: 0, z: 0 });
  const [hangarFollowEnabled, setHangarFollowEnabled] = useState(true);
  const [hangarDiagnostics, setHangarDiagnostics] = useState<{sceneLoadDurationMs:number|null;webgl2Available:boolean;browserSupported:boolean;fallbackReason:'unsupported_browser'|'webgl_unavailable'|'asset_invalid'|'asset_load_failed'|'render_failed'|'contract_invalid'|'refresh_failed'|null}>({ sceneLoadDurationMs: null, webgl2Available: false, browserSupported: false, fallbackReason: null });
  const hangarEntryStartedAt = useRef<number|null>(null);
  const hangarContextLostRef = useRef(false);

  function resetHangarState() {
    setHangarView('regular');
    setHangarFallback(null);
    setHangarSceneLoaded(false);
    setHangarSelectedObjectId(null);
    setHangarActionMessage('');
    setHangarStale(false);
    setHangarDiagnostics((current) => ({ ...current, sceneLoadDurationMs: null, fallbackReason: null }));
    HangarSceneRef.current = null;
  }

  function handleHangarContextLost() {
    if (hangarContextLostRef.current) return;
    hangarContextLostRef.current = true;
    setHangarView('fallback');
    setHangarFallback('render_failed');
    setHangarSceneLoaded(false);
    HangarSceneRef.current = null;
    setHangarDiagnostics((current) => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'render_failed' }));
  }

  function handleHangarCanvasCreated(state: any) {
    if (hangarContextLostRef.current) return;
    const element = state?.gl?.domElement ?? state?.canvas;
    if (element && typeof element.addEventListener === 'function') {
      element.addEventListener('webglcontextlost', handleHangarContextLost, { once: true });
    }
    const endedAt = hangarMonotonicClock ? hangarMonotonicClock() : (typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now());
    const startedAt = hangarEntryStartedAt.current;
    if (startedAt !== null) setHangarDiagnostics((current) => ({ ...current, sceneLoadDurationMs: Math.max(0, Math.round(endedAt - startedAt)), fallbackReason: null }));
  }
  const hangarDataRoomMarkers: Record<string, HangarGuideMarker> = Object.fromEntries(hangarEntries.map((entry, index) => [entry.caseId, index === 0 ? { x: 4, z: -4 } : index === 1 ? { x: -4, z: 4 } : { x: 0, z: 6 }]));
  const sharedHangarAircraftSelectionHandler = (aircraftId: string) => {
    if (hangarSnapshot && !canAdmitHangarCommands(hangarSnapshot)) return;
    let serverIndex = hangarEntries.findIndex((entry) => entry.caseId === aircraftId);
    if (/^AIR-[1-9]\d*$/.test(aircraftId)) {
      serverIndex = Number(aircraftId.slice(4)) - 1;
      setHangarSelectedObjectId(['SYN-CP-A-AIR-001','SYN-CP-B-AIR-001','SYN-CP-C-AIR-001'][serverIndex] ?? null);
    }
    if (/^SYN-CP-[ABC]-AIR-001$/.test(aircraftId)) {
      serverIndex = ['SYN-CP-A-AIR-001','SYN-CP-B-AIR-001','SYN-CP-C-AIR-001'].indexOf(aircraftId);
      setHangarSelectedObjectId(aircraftId);
      const letter = aircraftId.match(/-CP-([ABC])-/)?.[1];
      if (letter) setHangarSelectedCaseId(`cp-case-${letter.toLowerCase()}`);
    }
    if (!hangarEntries[serverIndex]) return;
    hangarAircraftSelectionHandler?.(`AIR-${serverIndex + 1}`);
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

  function refreshHangarSnapshot() {
    hangarRefreshQueue.current = hangarRefreshQueue.current.then(async () => {
      setHangarStale(false);
      try {
        const cases = hangarEntries;
        const nextCaseSnapshots = { ...hangarCaseSnapshotsRef.current };
        const nextSceneEnvelopes = { ...hangarSceneEnvelopes };
        let nextSnapshot = hangarSnapshotRef.current;
        for (const entry of cases) {
          const response = await fetch(`/api/v1/cases/${encodeURIComponent(entry.caseId)}/scene`);
          if (!response.ok) throw new Error('refresh_failed');
          const envelope = await response.json();
          const envelopeCase = typeof envelope === 'object' && envelope !== null ? (envelope as Record<string,unknown>).case : null;
          const envelopeCaseId = typeof envelopeCase === 'object' && envelopeCase !== null ? (envelopeCase as Record<string,unknown>).caseId : null;
          if (envelopeCaseId !== entry.caseId) throw new Error('refresh_failed');
          const result = await refreshHangarScene(() => envelope, nextCaseSnapshots[entry.caseId] ?? hangarSnapshotRef.current);
          if (!result.valid || !result.snapshot) {
            if (result.stale && (result.error === 'Stale scene revision' || result.error === 'Changed content at retained scene revision')) {
              throw new Error(result.error);
            }
            throw new Error('refresh_failed');
          }
          nextCaseSnapshots[entry.caseId] = result.snapshot;
          nextSceneEnvelopes[entry.caseId] = envelope;
          if (!nextSnapshot?.revision || (result.snapshot.revision ?? -1) >= (nextSnapshot.revision ?? -1)) nextSnapshot = result.snapshot;
        }
        if (!cases.length) throw new Error('refresh_failed');
        const recoveredCaseSnapshots = Object.fromEntries(Object.entries(nextCaseSnapshots).map(([caseId, snapshot]) => [caseId,{...snapshot,current:true,fallbackReason:null}]));
        const recoveredSnapshot = Object.values(recoveredCaseSnapshots).reduce<ServerSceneSnapshot|null>((highest, snapshot) => !highest || (snapshot.revision ?? -1) > (highest.revision ?? -1) ? snapshot : highest, nextSnapshot);
        hangarCaseSnapshotsRef.current = recoveredCaseSnapshots;
        hangarSnapshotRef.current = recoveredSnapshot;
        setHangarSceneEnvelopes(nextSceneEnvelopes);
        setHangarCaseSnapshots(recoveredCaseSnapshots);
        setHangarSnapshot(recoveredSnapshot);
        setHangarStale(false);
        setHangarView('3d');
        setHangarSceneLoaded(true);
      } catch (error) {
        const staleCases: Record<string, ServerSceneSnapshot> = Object.fromEntries(Object.entries(hangarCaseSnapshotsRef.current).map(([caseId, snapshot]) => [caseId, {...snapshot,current:false,fallbackReason:'refresh_failed' as const}]));
        const result = await refreshHangarScene(() => { throw new Error('refresh_failed'); }, hangarSnapshotRef.current);
        const retained = Object.values(staleCases).reduce<ServerSceneSnapshot|null>((highest, snapshot) => !highest || (snapshot.revision ?? -1) >= (highest.revision ?? -1) ? snapshot : highest, hangarSnapshotRef.current);
        const retainedStale = retained ? {...retained,current:false,fallbackReason:'refresh_failed' as const} : null;
        hangarCaseSnapshotsRef.current = staleCases;
        hangarSnapshotRef.current = retainedStale;
        setHangarCaseSnapshots(staleCases);
        setHangarSnapshot(retainedStale);
        setHangarStale(true);
        const staleRevisionError = error instanceof Error && (error.message === 'Stale scene revision' || error.message === 'Changed content at retained scene revision');
        if (staleRevisionError) {
          setHangarView('fallback');
          setHangarSceneLoaded(false);
        } else if (hangarSelectedObjectId) {
          setHangarView('3d');
          setHangarSceneLoaded(true);
        } else {
          setHangarView('fallback');
        }
        setHangarFallback('refresh_failed');
        setHangarDiagnostics((current) => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'refresh_failed' }));
      }
    });
  }

  useEffect(() => {
    if (hangarView !== '3d') return;
    const onFocus = () => { void refreshHangarSnapshot(); };
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [hangarView, hangarEntries, hangarSnapshot, hangarSelectedObjectId]);

  async function enterHangar() {
    hangarContextLostRef.current = false;
    const startedAt = hangarMonotonicClock ? hangarMonotonicClock() : (typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now());
    hangarEntryStartedAt.current = startedAt;
    setHangarView('loading');
    setHangarSceneLoaded(false);
    HangarSceneRef.current = null;
    try {
      const listResponse = await fetch('/api/v1/cases');
      if (!listResponse.ok) throw new Error('refresh_failed');
      const list = await listResponse.json() as Record<string,unknown>;
      if (list.contractVersion !== 1 || !Array.isArray(list.cases)) throw {fallback:'contract_invalid'};
      const cases = list.cases as Array<Record<string,unknown>>;
      const envelopes: unknown[] = [];
      for (const entry of cases) {
        const caseId = typeof entry.caseId === 'string' ? entry.caseId : '';
        if (!caseId) throw {fallback:'contract_invalid'};
        const sceneResponse = await fetch(`/api/v1/cases/${encodeURIComponent(caseId)}/scene`);
        if (sceneResponse.status === 403 || sceneResponse.status === 500) throw {fallback:'refresh_failed'};
        if (!sceneResponse.ok) throw {fallback:'refresh_failed'};
        let envelope: unknown;
        try { envelope = await sceneResponse.json(); } catch { throw {fallback:'contract_invalid'}; }
        envelopes.push(envelope);
      }
      for (const envelope of envelopes) {
        const validated = validateHangarSceneEnvelope(envelope);
        if (!validated.valid) throw {fallback:'contract_invalid'};
      }
      const entries = envelopes.map((envelope, index): HangarSourceSummaryEntry => {
        const scene = envelope as Record<string,unknown>;
        const c = typeof scene.case === 'object' && scene.case !== null ? scene.case as Record<string,unknown> : {};
        const listedId = typeof cases[index]?.caseId === 'string' ? cases[index].caseId as string : '';
        const envelopeId = typeof c.caseId === 'string' ? c.caseId : '';
        if (!listedId || envelopeId !== listedId) throw {fallback:'contract_invalid'};
        return {
          caseId: listedId,
          name: typeof c.name === 'string' ? c.name : (typeof c.aircraftName === 'string' ? c.aircraftName : (typeof cases[index]?.name === 'string' ? cases[index].name as string : '')),
          category: typeof c.aircraftCategory === 'string' ? c.aircraftCategory : (typeof c.category === 'string' ? c.category : (typeof cases[index]?.aircraftCategory === 'string' ? cases[index].aircraftCategory as string : '')),
          unavailable: false,
          sources: HANGAR_SOURCE_IDS.map(sourceId => ({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`})),
        };
      }).filter((entry) => entry.caseId && entry.name && entry.category);
      setHangarEntries(entries);
      const firstEntryId = entries[0]?.caseId ?? 'cp-case-a';
      const firstObjectId = `SYN-CP-${firstEntryId.slice(-1).toUpperCase()}-AIR-001`;
      setHangarSelectedCaseId(firstEntryId);
      setHangarSelectedObjectId(firstObjectId);
      const initialCaseSnapshots: Record<string, ServerSceneSnapshot> = {};
      let latestSnapshot: ServerSceneSnapshot | null = hangarSnapshot;
      for (let index = 0; index < envelopes.length; index += 1) {
        const snapshot = snapshotFromServer(envelopes[index]);
        if (!snapshot) throw {fallback:'contract_invalid'};
        const caseId = typeof cases[index]?.caseId === 'string' ? cases[index].caseId as string : '';
        initialCaseSnapshots[caseId] = snapshot;
        latestSnapshot = snapshot;
      }
      const sceneEnvelopes: Record<string, unknown> = {};
      for (let index = 0; index < envelopes.length; index += 1) {
        const caseId = typeof cases[index]?.caseId === 'string' ? cases[index].caseId as string : '';
        if (caseId) sceneEnvelopes[caseId] = envelopes[index];
      }
      setHangarSceneEnvelopes(sceneEnvelopes);
      hangarCaseSnapshotsRef.current = initialCaseSnapshots;
      hangarSnapshotRef.current = latestSnapshot;
      setHangarCaseSnapshots(initialCaseSnapshots);
      if (latestSnapshot) setHangarSnapshot(latestSnapshot);
      setHangarStale(false);
    } catch (error) {
      const fallback = typeof error === 'object' && error !== null && 'fallback' in error
        ? String((error as {fallback:unknown}).fallback) : 'refresh_failed';
      if (fallback === 'contract_invalid') {
        setHangarView('contract-error');
        setHangarDiagnostics((current) => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'contract_invalid' }));
        return;
      }
      setHangarFallback('refresh_failed');
      setHangarView('fallback');
      setHangarDiagnostics((current) => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'refresh_failed' }));
      return;
    }
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
    const fallbackReason = decision.fallbackReason as 'unsupported_browser' | 'webgl_unavailable' | 'asset_invalid' | 'asset_load_failed' | 'render_failed' | 'contract_invalid' | 'refresh_failed' | null;
    const webgl2Available = typeof webgl2 === 'object' && webgl2 !== null && (
      (webgl2 as { webgl2?: unknown }).webgl2 === true || (webgl2 as { supported?: unknown }).supported === true
    );
    setHangarDiagnostics({ sceneLoadDurationMs: decision.mode === 'fallback' ? null : null, webgl2Available, browserSupported, fallbackReason });
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

  function applyRoutePath(nextRoute: string) {
    if (typeof window === 'undefined') return;
    const normalized = nextRoute.startsWith('/') ? nextRoute : `/${nextRoute}`;
    const currentPath = `${window.location.pathname}${window.location.search}`;
    if (currentPath !== normalized) {
      window.history.replaceState({}, '', normalized);
    }
    setRoutePath(normalized);
  }

  useEffect(() => {
    if (typeof window !== 'undefined') {
      const pathname = window.location.pathname;
      const search = window.location.search;
      if (!session && pathname !== '/' && pathname !== '/sign-in') {
        const params = new URLSearchParams(search);
        const explicitReturnTo = validateReturnTo(params.get('returnTo'));
        const redirectTarget = explicitReturnTo ?? (validateReturnTo(pathname + search) ? pathname + search : null);
        if (redirectTarget) {
          const target = `/sign-in?returnTo=${encodeURIComponent(redirectTarget)}`;
          if (window.location.pathname !== '/sign-in' || window.location.search !== `?returnTo=${encodeURIComponent(redirectTarget)}`) {
            window.history.replaceState({}, '', target);
            setRoutePath(target);
          }
        }
      }
    }
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

  async function handleRoleSwitch(userId: string) {
    if (!session) return;
    try {
      const response = await fetch('/api/auth/switch-user', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-Token': session.csrfToken,
        },
        body: JSON.stringify({ userId }),
      });
      if (!response.ok) {
        setCaseError('You do not have permission for this action.');
        return;
      }
      const nextSession: SignedInSession = await response.json();
      resetHangarState();
      setSession(nextSession);
      setSelectedCaseId(nextSession.caseScopes?.[0] ?? null);
      traceRequestId.current += 1;
      setData(null);
      setReason('');
      setDecisionBusy(false);
      setTraceView(null);
      const path = ROLE_HOME_PATHS[nextSession.role] ?? '/work';
      if (typeof window !== 'undefined') window.location.assign(path);
    } catch {
      setCaseError('Unable to switch roles. Try again later.');
    }
  }

  function handleSessionChange(nextSession: SignedInSession | null) {
    resetHangarState();
    setSession(nextSession);
    setSelectedCaseId(resolveSessionCaseId(nextSession, selectedCaseId));
    traceRequestId.current += 1;
    setData(null);
    setReason('');
    setDecisionBusy(false);
    setTraceView(null);
    if (nextSession) {
      const currentSearch = typeof window !== 'undefined' ? window.location.search : '';
      const requestedFromSearch = validateReturnTo(new URLSearchParams(currentSearch).get('returnTo'));
      const requestedPath = requestedFromSearch ?? returnTo ?? null;
      const roleHome = ROLE_HOME_PATHS[nextSession.role] ?? '/work';
      const resolvedPath = resolveReturnTo(requestedPath, roleHome);
      const nextRoute = routeMatchesRole(resolvedPath, nextSession.role) ? resolvedPath : roleHome;
      if (typeof window !== 'undefined') {
        applyRoutePath(nextRoute);
        setReturnTo(routeMatchesRole(resolvedPath, nextSession.role) ? resolvedPath : null);
      }
    }
  }

  useEffect(() => {
    if (typeof window !== 'undefined') {
      const nextPath = window.location.pathname + window.location.search;
      setRoutePath(nextPath);
      if (!session) {
        const returnParam = new URLSearchParams(window.location.search).get('returnTo');
        if (returnParam && validateReturnTo(returnParam)) {
          setReturnTo(validateReturnTo(returnParam));
        } else {
          setReturnTo(null);
        }
      }
    }
    if (!session) {
      traceRequestId.current += 1;
      setData(null);
      setCaseError('');
      setLoadingCase(false);
      setReason('');
      setTraceView(null);
      return;
    }
    const sessionCaseId = resolveSessionCaseId(session, selectedCaseId);
    if (!sessionCaseId) {
      setData(null);
      setCaseError('');
      setLoadingCase(false);
      return;
    }
    if (selectedCaseId !== sessionCaseId) {
      setSelectedCaseId(sessionCaseId);
      return;
    }
    let active = true;
    setLoadingCase(true);
    setCaseError('');
    const encodedCaseId = encodeURIComponent(sessionCaseId);
    const caseBase = `/api/cases/${encodedCaseId}`;
    (async () => {
      try {
        const caseProjection = await getProjection<CaseProjection>(caseBase);
        const dashboardProjection = await getProjection<DashboardData>(`${caseBase}/dashboard`);
        const reviewProjection = await getProjection<TechnicalReviewProjection>(`${caseBase}/technical-reviews`);
        const readiness = await getProjection<ReadinessSummaryData>(`${caseBase}/readiness`);
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
  }, [session, selectedCaseId, refresh]);

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
      || !session || !selectedCaseId || !data
      || !Number.isInteger(data.caseState.revision) || !Number.isInteger(data.review.basisVersion)
      || !reason.trim()
    ) return;

    setDecisionBusy(true);
    setCaseError('');
    try {
      const response = await fetch(`/api/cases/${encodeURIComponent(selectedCaseId)}/technical-reviews`, {
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

  function handleCaseNavigate(nextPath: string) {
    const match = nextPath.match(/^\/cases\/([^/]+)(?:\/.*)?$/);
    if (match) {
      const nextCaseId = decodeURIComponent(match[1]);
      resetHangarState();
      setSelectedCaseId(nextCaseId);
      const normalized = normalizeRoutePath(nextPath);
      setRoutePath(normalized);
      if (typeof window !== 'undefined') {
        const currentPath = `${window.location.pathname}${window.location.search}`;
        if (currentPath !== normalized) {
          window.history.pushState({}, '', normalized);
        }
      }
    }
  }

  const LoadedHangarScene = HangarSceneRef.current;
  const hangarActiveEnvelope = hangarSceneEnvelopes[hangarSelectedCaseId] ?? hangarSnapshot?.completeContent;
  const hangarComposedScene = typeof hangarActiveEnvelope === 'object' && hangarActiveEnvelope !== null
    ? hangarActiveEnvelope
    : hangarSnapshot?.completeContent;
  const hangarCaseResetEnvelopeBase = hangarSceneEnvelopes['cp-case-c'] ?? hangarCaseSnapshots['cp-case-c']?.completeContent ?? hangarComposedScene;
  const hangarCaseResetEnvelope = session?.role !== 'technical-reviewer'
    ? { ...(typeof hangarCaseResetEnvelopeBase === 'object' && hangarCaseResetEnvelopeBase !== null ? hangarCaseResetEnvelopeBase as Record<string, unknown> : {}), canResetBlockedVariant: false, allowedActions: [] }
    : hangarCaseResetEnvelopeBase;
  const hangarCompositionObjects = hangarEntries.flatMap((entry) => {
    const envelope = hangarSceneEnvelopes[entry.caseId];
    const root = typeof envelope === 'object' && envelope !== null ? envelope as Record<string, unknown> : {};
    const objects = Array.isArray(root.objects) ? root.objects as Record<string, unknown>[] : [];
    return objects.map((object) => {
      const objectId = String(object.objectId ?? '');
      const caseName = entry.name;
      const label = String(object.accessibleName ?? object.label ?? object.displayName ?? (
        objectId.endsWith('-AIR-001') ? `${caseName} aircraft`
          : objectId.endsWith('-ENG-001') ? `${caseName} engine`
            : objectId.endsWith('-CMP-001') ? `${caseName} component 1`
              : objectId.endsWith('-CMP-002') ? `${caseName} component 2` : objectId
      ));
      return { ...object, label, accessibleName: label };
    });
  });
  const selectedCaseEnvelopeBase = hangarSceneEnvelopes[hangarSelectedCaseId]
    ?? (typeof hangarComposedScene === 'object' && hangarComposedScene !== null ? hangarComposedScene : {});
  const selectedCaseEnvelope = session?.role !== 'technical-reviewer' && typeof selectedCaseEnvelopeBase === 'object' && selectedCaseEnvelopeBase !== null
    ? { ...selectedCaseEnvelopeBase as Record<string, unknown>, canResetBlockedVariant: false, allowedActions: [], case: { ...((selectedCaseEnvelopeBase as Record<string, unknown>).case as Record<string, unknown> ?? {}), permission: false, allowedActions: [] } }
    : selectedCaseEnvelopeBase;
  const selectedCaseRoot = typeof selectedCaseEnvelope === 'object' && selectedCaseEnvelope !== null
    ? selectedCaseEnvelope as Record<string, unknown> : {};
  const combinedHangarEnvelope = {
    ...selectedCaseRoot,
    objects: hangarCompositionObjects,
  };
  if (!hangarFollowCameraRef.current) hangarFollowCameraRef.current = createHangarFollowCamera({ x: 0, y: 0, z: 0 });
  if (!hangarWalkControllerRef.current) {
    hangarWalkControllerRef.current = createHangarWalkController({ x: 0, z: 0 }, {
      walkablePolygon: [{ x: -5, z: -5 }, { x: 5, z: -5 }, { x: 5, z: 5 }, { x: -5, z: 5 }],
      followCamera: true,
    });
  }
  const handleHangarAppWalk = (event: ReactKeyboardEvent<HTMLElement>) => {
    const target = event.target;
    if (!(target instanceof HTMLElement) || !target.closest('[data-hangar-walk-region]')) return;
    if (target !== event.currentTarget && target.closest('[data-hangar-rack-menu], [data-hangar-guide-line]')) return;
    if (target.closest('button, a, input, textarea, select, [role="button"], [role="menu"], [role="menuitem"]')) return;
    if (event.key === 'Escape') {
      const rackWalk = document.querySelector<HTMLElement>('[data-hangar-guide-line] [data-hangar-walk-region]');
      rackWalk?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      event.preventDefault();
      return;
    }
    if (!['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(event.key)) return;
    const rackWalk = document.querySelector<HTMLElement>('[data-hangar-guide-line] [data-hangar-walk-region]');
    if (rackWalk && rackWalk !== target) rackWalk.dispatchEvent(new KeyboardEvent('keydown', { key: event.key, repeat: event.repeat, bubbles: true }));
    const controller = hangarWalkControllerRef.current;
    const camera = hangarFollowCameraRef.current;
    if (!controller || !camera) return;
    const before = controller.getState();
    const result = controller.step(event.key, event.repeat);
    if (!result.accepted) return;
    setHangarAvatarPosition({ x: result.position.x, y: 0, z: result.position.z });
    if (camera.followEnabled) {
      const pose = camera.applyAvatarStep({ x: result.position.x, y: 0, z: result.position.z });
      setHangarCameraPose(pose.target.position);
    }
    event.preventDefault();
    void before;
  };
  return (
    <main>
      <h1>Airline asset review</h1>
      <p>Reviewer workspace</p>
      <p role="status" aria-live="polite">{healthMessage}</p>
      <SignIn onSessionChange={handleSessionChange} />
      {session && (
        <WorkspaceShell
          userLabel={session.displayName}
          role={session.role}
          roleOptions={PERSONA_OPTIONS.map((option) => ({ id: option.id, label: option.label, role: option.role }))}
          onRoleSelect={handleRoleSwitch}
          searchInput={<CaseSearch caseScopes={session.caseScopes} onNavigate={handleCaseNavigate} />}
          primaryNavigation={<RoleNavigation role={session.role} />}
        >
          <section aria-labelledby="case-heading">
            {!session.caseScopes?.length ? (
              <p>No cases are assigned to your account.</p>
            ) : (
              <>
                {session.caseScopes.length > 1 && (
                  <label>
                    Case
                    <select value={selectedCaseId ?? ''} onChange={(event) => {
                      resetHangarState();
                      setData(null);
                      setReason('');
                      setDecisionBusy(false);
                      setTraceView(null);
                      setCaseError('');
                      setSelectedCaseId(event.target.value);
                    }}>
                      {session.caseScopes.map((caseId) => <option key={caseId} value={caseId}>{caseId}</option>)}
                    </select>
                  </label>
                )}
                {data && <ReadinessSummary summary={data.readiness} />}
                {selectedCaseId && <h2 id="case-heading" tabIndex={-1}>Case {selectedCaseId}</h2>}
              </>
            )}
            {loadingCase && <p role="status">Loading case data…</p>}
            {caseError && <p role="alert">{caseError}</p>}
            {data && (
              <>
                <p>Revision: {data.caseState.revision}</p>
                <p>Evidence basis: {data.caseState.basis.length}</p>
                <p>Technical-review decision: {currentDecision ?? 'No current decision'}</p>
                <button type="button" onClick={() => { void enterHangar(); }}>Enter Hangar</button>
                {hangarView === 'regular' && (
                  <HangarNon3dDataRoom
                    entries={hangarEntries}
                    scope={hangarEntries.map(entry => entry.caseId)}
                    onConfirm={sharedHangarAircraftSelectionHandler}
                  />
                )}
                {hangarView !== 'regular' && hangarView !== 'loading' && (
                  <section aria-labelledby="hangar-heading">
                    <h2 id="hangar-heading">Hangar</h2>
                    {hangarSnapshot && <p aria-label="Hangar retained snapshot">Revision: {hangarSnapshot.revision}; Labels: {String(hangarSnapshot.owner)}; {String(hangarSnapshot.capabilityLabel)}</p>}
                    <div aria-label="Hangar diagnostics">
                      <HangarLocalDiagnostics
                        session={session}
                        {...hangarDiagnostics}
                      />
                    </div>
                    {hangarView === '3d' && hangarSceneLoaded && (
                      <div aria-label="Hangar 3D scene">
                        <button type="button" onClick={() => { void refreshHangarSnapshot(); }}>Refresh Hangar scene</button>
                        {hangarEntries.map((entry) => {
                          const letter = entry.caseId.slice(-1).toUpperCase();
                          const envelope = hangarSceneEnvelopes[entry.caseId] as Record<string, unknown> | undefined;
                          const caseView = envelope?.case as Record<string, unknown> | undefined;
                          const nextAction = caseView?.nextAction as Record<string, unknown> | string | undefined;
                          const actionText = typeof nextAction === 'object' && nextAction !== null ? String(nextAction.text ?? '') : String(nextAction ?? '');
                          return <div key={entry.caseId} data-hangar-label={`SYN-CP-${letter}-AIR-001`} aria-hidden="true">{String(caseView?.name ?? entry.name)}<br />{String(caseView?.category ?? entry.category)}<br />{String(caseView?.capabilityLabel ?? '')}<br />{actionText}</div>;
                        })}
                        <HangarErrorBoundary snapshot={hangarSnapshot} lastSnapshot={hangarSnapshot}>
                          {LoadedHangarScene ? <LoadedHangarScene {...({ sceneData: hangarComposedScene, hangarClientSnapshot, refreshReader: async () => {
                            const first = hangarEntries[0]?.caseId;
                            if (!first) throw new Error('refresh_failed');
                            const response = await fetch(`/api/v1/cases/${encodeURIComponent(first)}/scene`);
                            if (!response.ok) throw new Error('refresh_failed');
                            return response.json();
                          }, externalRefresh: true, snapshot: hangarSnapshot } as Record<string, unknown>) } onSelect={(aircraftId) => {
                            sharedHangarAircraftSelectionHandler(aircraftId);
                            const match = aircraftId.match(/([ABC])-/);
                            if (match) setHangarSelectedCaseId(`cp-case-${match[1].toLowerCase()}`);
                          }} onCreated={handleHangarCanvasCreated} onContextLost={handleHangarContextLost} /> : <canvas aria-label="3D hangar scene" />}
                        </HangarErrorBoundary>
                        <div aria-label="Hangar view presets">
                          <button type="button" onClick={() => setHangarSelectedCaseId(hangarEntries[0]?.caseId ?? 'cp-case-a')}>Overview</button>
                          {hangarEntries.map((entry) => <button key={entry.caseId} type="button" aria-label={`View ${entry.name}`} onClick={() => setHangarSelectedCaseId(entry.caseId)}>View {entry.name}</button>)}
                        </div>
                        <HangarSemanticControls
                          scene={combinedHangarEnvelope}
                          manifest={hangarManifest}
                          onActivate={(control, source) => {
                            const objectId = typeof control === 'string' ? control : control.id;
                            setHangarSelectedObjectId(objectId);
                            if (typeof source === 'object' && source !== null && typeof (source as Record<string, unknown>).caseId === 'string') {
                              setHangarSelectedCaseId(String((source as Record<string, unknown>).caseId));
                            } else {
                              const letter = objectId.match(/-CP-([ABC])-/)?.[1];
                              if (letter) setHangarSelectedCaseId(`cp-case-${letter.toLowerCase()}`);
                            }
                          }}
                        />
                        {hangarSelectedObjectId && (() => {
                          const letter = hangarSelectedObjectId.match(/-CP-([ABC])-/)?.[1]?.toLowerCase();
                          const selectionSnapshotEnvelope = letter ? hangarCaseSnapshots[`cp-case-${letter}`]?.completeContent : null;
                          const selectionEnvelopeFromSnapshot = typeof selectionSnapshotEnvelope === 'object' && selectionSnapshotEnvelope !== null ? selectionSnapshotEnvelope : null;
                          const sceneEnvelopeForSelection = letter ? hangarSceneEnvelopes[`cp-case-${letter}`] ?? selectedCaseEnvelope : selectedCaseEnvelope;
                          const snapshotCase = selectionEnvelopeFromSnapshot && typeof (selectionEnvelopeFromSnapshot as Record<string,unknown>).case === 'object' ? ((selectionEnvelopeFromSnapshot as Record<string,unknown>).case as Record<string,unknown>) : null;
                          const sceneCase = typeof sceneEnvelopeForSelection === 'object' && sceneEnvelopeForSelection !== null ? (sceneEnvelopeForSelection as Record<string,unknown>).case as Record<string,unknown> : null;
                          const snapshotRevision = typeof snapshotCase?.caseRevision === 'number' ? snapshotCase.caseRevision : -1;
                          const sceneRevision = typeof sceneCase?.caseRevision === 'number' ? sceneCase.caseRevision : -1;
                          const selectionEnvelope = sceneRevision >= snapshotRevision ? sceneEnvelopeForSelection : selectionEnvelopeFromSnapshot;
                          const selectionCase = typeof selectionEnvelope === 'object' && selectionEnvelope !== null ? (selectionEnvelope as Record<string,unknown>).case as Record<string,unknown> | undefined : undefined;
                          const selectionRevision = typeof selectionCase?.caseRevision === 'number' ? selectionCase.caseRevision : null;
                          const selectionSnapshot = letter ? hangarCaseSnapshots[`cp-case-${letter}`] : hangarSnapshot;
                          return <HangarSelectionEvidence key={`${hangarSelectedObjectId}:${selectionRevision}`} scene={selectionEnvelope} manifest={hangarManifest.objectIds} identifier={hangarSelectedObjectId} snapshot={hangarStale ? {...selectionSnapshot,current:false,fallbackReason:'refresh_failed'} : selectionSnapshot ? {...selectionSnapshot,current:true,fallbackReason:null} : selectionSnapshot} stale={hangarStale} />;
                        })()}
                        <HangarGuidedJourney scene={selectedCaseEnvelope} />
                        <HangarCaseReset
                          envelope={hangarCaseResetEnvelope}
                          csrfToken={session.csrfToken}
                          transport={(url, init) => fetch(url, init)}
                          refetch={() => refreshHangarSnapshot()}
                          confirmReset={() => true}
                        />
                        <section data-hangar-app-composition="true" data-hangar-walk-region="true" role="region" aria-label="Walk region" tabIndex={0} onKeyDown={handleHangarAppWalk}>
                        <p data-hangar-avatar-position={`${hangarAvatarPosition.x},${hangarAvatarPosition.z}`}>{`Avatar: ${hangarAvatarPosition.x}, ${hangarAvatarPosition.z}`}</p>
                        <p data-hangar-camera-pose={`${hangarCameraPose.x},${hangarCameraPose.y},${hangarCameraPose.z}`}>Camera pose</p>
                        {hangarFollowCameraRef.current && <button type="button" aria-pressed={hangarFollowEnabled} onClick={() => { const camera = hangarFollowCameraRef.current!; const next = camera.toggleFollow(); setHangarFollowEnabled(next.follow); setHangarCameraPose(next.position); }}>{hangarFollowEnabled ? 'Follow avatar: on' : 'Follow avatar: off'}</button>}
                        <HangarGuideLineAdapter
                          entries={hangarEntries}
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
                          walkRegionLabel="Data rack walk region"
                        />
                        <div aria-label="Reduced motion controls">
                          <button type="button" aria-pressed={hangarReducedMotion} onClick={() => setHangarMotionOverride(true)}>Enable reduced motion</button>
                          <button type="button" aria-pressed={!hangarReducedMotion} onClick={() => setHangarMotionOverride(false)}>Disable reduced motion</button>
                          <button type="button" onClick={clearHangarMotionOverride}>Use system preference</button>
                        </div>
                        </section>
                      </div>
                    )}
                    {hangarView === 'fallback' && <p role="alert">{hangarFallback === 'unsupported_browser'
                      ? 'Your browser is not supported. Use the current or previous major version of Chrome or Edge.'
                      : hangarFallback === 'refresh_failed'
                        ? 'Hangar refresh failed. Use the non-3D list.'
                        : 'WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2-capable browser.'}</p>}
                    {hangarView === 'contract-error' && <p role="alert">Invalid hangar scene contract. <a href="#non-3d-list">Use the supported contract version in the non-3D list.</a></p>}
                    {(hangarView === 'fallback' || hangarView === 'contract-error') && (
                      hangarView === 'fallback' && <button type="button" onClick={() => { void refreshHangarSnapshot(); }}>Refresh Hangar scene</button>
                    )}
                    {(hangarView === 'fallback' || hangarView === 'contract-error') && (
                      <>
                        <HangarNon3dDataRoom
                          entries={hangarEntries.map((entry): HangarSourceSummaryEntry => ({
                            ...entry,
                            sources: HANGAR_SOURCE_IDS.map(sourceId => ({
                              sourceId, mode: 'available', coverage: true,
                              modeText: 'mode: available', coverageText: 'coverage: true',
                              text: `${sourceId}: mode: available; coverage: true`,
                            })),
                          }))}
                          scope={hangarEntries.map(entry => entry.caseId)}
                          onConfirm={sharedHangarAircraftSelectionHandler}
                        />
                        <section aria-label="Legacy non-3D actions">
                          <button type="button" onClick={() => runNon3dAction('selection')}>Select case</button>
                          <button type="button" onClick={() => runNon3dAction('status')}>View status</button>
                          <button type="button" onClick={() => runNon3dAction('evidence')}>View evidence</button>
                          <button type="button" onClick={() => runNon3dAction('journey')}>Follow journey</button>
                          <button type="button" onClick={() => runNon3dAction('review')}>Open review</button>
                          <p role="status">{hangarActionMessage}</p>
                        </section>
                      </>
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
        </WorkspaceShell>
      )}
    </main>
  );
}
