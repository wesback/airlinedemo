import { useEffect, useMemo, useRef, useState, type ComponentType, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from 'react';
import HangarLocalDiagnostics from './localDiagnostics';
import HangarGuideLineAdapter from './GuideLineAdapter';
import type { HangarGuideMarker } from './guideLine';
// Type-only: the entry controller (and three.js behind it) is imported lazily in enterHangar.
import type { HangarCameraPresetRequest, HangarSceneProps } from './entryController';
import { validateHangarSceneEnvelope } from './sceneProjection';
import { canAdmitHangarCommands, refreshHangarScene, snapshotFromServer, type ServerSceneSnapshot } from './sceneRefresh';
import HangarSemanticControls from './semanticControls';
import HangarSelectionEvidence from './selectionPanel';
import HangarGuidedJourney from './guidedJourney';
import HangarCaseReset from './caseReset';
import HangarErrorBoundary from './errorBoundary';
import hangarManifest from './sceneManifest.json';
import { HANGAR_SOURCE_IDS, type HangarSourceSummaryEntry } from './sourceSummary';
import { createHangarWalkController } from './walkController';
import { createHangarFollowCamera, type HangarFollowCamera } from './followCamera';
import HangarNon3dDataRoom from './non3dDataRoom';
import './hangarMode.css';

export type HangarEntryAppProps = {
  hangarContractVersion?: unknown;
  hangarBrowserSupported?: unknown;
  hangarWebGl2Probe?: unknown;
  hangarLazyLoader?: () => unknown | Promise<unknown>;
  hangarMonotonicClock?: () => number;
  hangarAircraftSelectionHandler?: (aircraftId: string) => unknown;
  hangarClientSnapshot?: unknown;
};

export type HangarModeSession = { id: string; displayName: string; role: string; csrfToken: string; caseScopes: string[] };

export type HangarModeProps = HangarEntryAppProps & {
  session: HangarModeSession;
  onExit?: () => void;
  onOpenCase?: (caseId: string) => void;
  releasedCaseIds?: readonly string[];
};

type HangarView = 'loading' | '3d' | 'fallback' | 'contract-error';
type HangarFallbackReason = 'unsupported_browser' | 'webgl_unavailable' | 'asset_invalid' | 'asset_load_failed' | 'render_failed' | 'contract_invalid' | 'refresh_failed' | null;
type HangarDiagnostics = { sceneLoadDurationMs: number | null; webgl2Available: boolean; browserSupported: boolean; fallbackReason: HangarFallbackReason };
type HangarMotionControllers = {
  motion: { enable: () => boolean; disable: () => boolean; clearOverride: () => null; reduced: boolean };
  camera: { overview: () => unknown };
  avatar: { move: (target: { x: number; z: number }, duration?: number) => unknown };
};
type CaseTone = 'done' | 'human' | 'ai';
type Letter = 'A' | 'B' | 'C';

/** Presentation names for the synthetic case pack; the registrations in the scene data stay fictional. */
export const HANGAR_AIRCRAFT_MODELS: Record<string, string> = {
  'cp-case-a': 'Airbus A320neo',
  'cp-case-b': 'Embraer E195-E2',
  'cp-case-c': 'Airbus A350-900',
};

const AIRCRAFT_OBJECT_IDS = ['SYN-CP-A-AIR-001', 'SYN-CP-B-AIR-001', 'SYN-CP-C-AIR-001'];
const WALKABLE_POLYGON = [{ x: -5, z: -5 }, { x: 5, z: -5 }, { x: 5, z: 5 }, { x: -5, z: 5 }];
const NON_3D_ACTION_TARGETS = {
  selection: 'non-3d-data-room-heading',
  status: 'hangar-case-status-heading',
  evidence: 'non-3d-data-room-heading',
  journey: 'hangar-case-status-heading',
  review: 'hangar-case-status-heading',
} as const;
const NON_3D_ACTION_MESSAGES = {
  selection: 'Case selected.',
  status: 'Case status selected.',
  evidence: 'Case evidence selected.',
  journey: 'Case journey selected.',
  review: 'Technical review selected.',
} as const;

const availableSources = () => HANGAR_SOURCE_IDS.map(sourceId => ({ sourceId, mode: 'available' as const, coverage: true, modeText: 'mode: available', coverageText: 'coverage: true', text: `${sourceId}: mode: available; coverage: true` }));
const initialEntries = (): HangarSourceSummaryEntry[] => [
  { caseId: 'cp-case-a', name: 'Case A', category: 'narrow-body', unavailable: false, sources: availableSources() },
  { caseId: 'cp-case-b', name: 'Case B', category: 'regional', unavailable: false, sources: availableSources() },
  { caseId: 'cp-case-c', name: 'Case C', category: 'wide-body', unavailable: false, sources: availableSources() },
];

const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
const letterOf = (caseOrObjectId: string): Letter | null => {
  const fromCase = /^cp-case-([abc])$/.exec(caseOrObjectId)?.[1];
  if (fromCase) return fromCase.toUpperCase() as Letter;
  const fromObject = /^SYN-CP-([ABC])-/.exec(caseOrObjectId)?.[1];
  return (fromObject as Letter | undefined) ?? null;
};
const nowFrom = (clock?: () => number) => clock ? clock() : (typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now());

/** Maps the server case status to the design-token tone: green done, amber waiting for a person, blue AI at work. */
export function describeHangarCaseStatus(envelope: unknown, released = false): { tone: CaseTone; text: string } {
  if (released) return { tone: 'done', text: 'Released to the next lessee' };
  const caseView = isRecord(envelope) && isRecord(envelope.case) ? envelope.case : null;
  const status = caseView && isRecord(caseView.status) ? caseView.status : {};
  if (status.legal === 'signed-off') return { tone: 'done', text: 'Ready for release' };
  if (status.legal === 'rejected' || status.finance === 'reject' || status.acceptance === 'reject') return { tone: 'human', text: 'Returned for rework' };
  if (status.finance === 'approve') return { tone: 'human', text: 'Waiting for legal review' };
  if (status.acceptance === 'accept') return { tone: 'human', text: 'Waiting for finance review' };
  if (status.review === 'open' || status.review === 'closed') return { tone: 'human', text: 'Waiting for technical review' };
  return { tone: 'ai', text: 'AI agent preparing evidence' };
}

function nextActionText(caseView: Record<string, unknown> | null): string {
  const action = caseView?.nextAction;
  if (isRecord(action) && typeof action.text === 'string' && action.text.trim()) return action.text;
  if (typeof action === 'string' && action.trim()) return action;
  return 'No next action';
}

export default function HangarMode({
  session,
  onExit,
  onOpenCase,
  releasedCaseIds = [],
  hangarContractVersion: contractVersionProp,
  hangarBrowserSupported,
  hangarWebGl2Probe,
  hangarLazyLoader,
  hangarMonotonicClock,
  hangarAircraftSelectionHandler,
  hangarClientSnapshot,
}: HangarModeProps) {
  const [hangarView, setHangarView] = useState<HangarView>('loading');
  const [hangarFallback, setHangarFallback] = useState<HangarFallbackReason>(null);
  const [hangarContractVersion] = useState<unknown>(contractVersionProp === undefined ? 1 : contractVersionProp);
  const [hangarSceneLoaded, setHangarSceneLoaded] = useState(false);
  const [hangarEntries, setHangarEntries] = useState<HangarSourceSummaryEntry[]>(initialEntries);
  const [hangarSnapshot, setHangarSnapshot] = useState<ServerSceneSnapshot | null>(null);
  const [hangarCaseSnapshots, setHangarCaseSnapshots] = useState<Record<string, ServerSceneSnapshot>>({});
  const [hangarSceneEnvelopes, setHangarSceneEnvelopes] = useState<Record<string, unknown>>({});
  const [hangarSelectedObjectId, setHangarSelectedObjectId] = useState<string | null>(null);
  const [hangarSelectedCaseId, setHangarSelectedCaseId] = useState<string>('cp-case-a');
  const hangarSnapshotRef = useRef<ServerSceneSnapshot | null>(null);
  const hangarCaseSnapshotsRef = useRef<Record<string, ServerSceneSnapshot>>({});
  const hangarRefreshQueue = useRef<Promise<void>>(Promise.resolve());
  const [hangarStale, setHangarStale] = useState(false);
  const HangarSceneRef = useRef<ComponentType<HangarSceneProps> | null>(null);
  const [hangarActionMessage, setHangarActionMessage] = useState('');
  const hangarMotion = useRef<HangarMotionControllers | null>(null);
  const [hangarReducedMotion, setHangarReducedMotion] = useState(false);
  const hangarFollowCameraRef = useRef<HangarFollowCamera | null>(null);
  const hangarWalkControllerRef = useRef<ReturnType<typeof createHangarWalkController> | null>(null);
  const [hangarAvatarPosition, setHangarAvatarPosition] = useState({ x: 0, y: 0, z: 0 });
  const [hangarCameraPose, setHangarCameraPose] = useState({ x: 0, y: 0, z: 0 });
  const [hangarFollowEnabled, setHangarFollowEnabled] = useState(true);
  const [hangarDiagnostics, setHangarDiagnostics] = useState<HangarDiagnostics>({ sceneLoadDurationMs: null, webgl2Available: false, browserSupported: false, fallbackReason: null });
  const hangarEntryStartedAt = useRef<number | null>(null);
  const hangarContextLostRef = useRef(false);
  const hangarEntryToken = useRef(0);
  const hangarEnteredRef = useRef(false);
  const [hangarRenderFailed, setHangarRenderFailed] = useState(false);
  const [cameraPreset, setCameraPreset] = useState<HangarCameraPresetRequest | null>(null);
  const [activePreset, setActivePreset] = useState<string>('overview');
  const cameraRequestCounter = useRef(0);

  if (!hangarFollowCameraRef.current) hangarFollowCameraRef.current = createHangarFollowCamera({ x: 0, y: 0, z: 0 });
  if (!hangarWalkControllerRef.current) {
    hangarWalkControllerRef.current = createHangarWalkController({ x: 0, z: 0 }, { walkablePolygon: WALKABLE_POLYGON, followCamera: true });
  }

  function requestCamera(preset: 'overview' | 'aircraft', letter?: Letter) {
    cameraRequestCounter.current += 1;
    setCameraPreset({ preset, letter, requestId: cameraRequestCounter.current });
    setActivePreset(preset === 'aircraft' && letter ? `cp-case-${letter.toLowerCase()}` : 'overview');
  }

  function handleHangarContextLost() {
    if (hangarContextLostRef.current) return;
    hangarContextLostRef.current = true;
    setHangarView('fallback');
    setHangarFallback('render_failed');
    setHangarSceneLoaded(false);
    HangarSceneRef.current = null;
    setHangarDiagnostics(current => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'render_failed' }));
  }

  function handleHangarRenderError() {
    setHangarRenderFailed(true);
    setHangarDiagnostics(current => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'render_failed' }));
  }

  function handleHangarCanvasCreated(state: any) {
    if (hangarContextLostRef.current) return;
    const element = state?.gl?.domElement ?? state?.canvas;
    if (element && typeof element.addEventListener === 'function') {
      element.addEventListener('webglcontextlost', handleHangarContextLost, { once: true });
    }
    const endedAt = nowFrom(hangarMonotonicClock);
    const startedAt = hangarEntryStartedAt.current;
    if (startedAt !== null) setHangarDiagnostics(current => ({ ...current, sceneLoadDurationMs: Math.max(0, Math.round(endedAt - startedAt)), fallbackReason: null }));
  }

  const hangarDataRoomMarkers: Record<string, HangarGuideMarker> = Object.fromEntries(hangarEntries.map((entry, index) => [entry.caseId, index === 0 ? { x: 4, z: -4 } : index === 1 ? { x: -4, z: 4 } : { x: 0, z: 6 }]));

  const sharedHangarAircraftSelectionHandler = (aircraftId: string) => {
    if (hangarSnapshot && !canAdmitHangarCommands(hangarSnapshot)) return;
    let serverIndex = hangarEntries.findIndex(entry => entry.caseId === aircraftId);
    if (/^AIR-[1-9]\d*$/.test(aircraftId)) {
      serverIndex = Number(aircraftId.slice(4)) - 1;
      setHangarSelectedObjectId(AIRCRAFT_OBJECT_IDS[serverIndex] ?? null);
    }
    if (/^SYN-CP-[ABC]-AIR-001$/.test(aircraftId)) {
      serverIndex = AIRCRAFT_OBJECT_IDS.indexOf(aircraftId);
      setHangarSelectedObjectId(aircraftId);
      const letter = letterOf(aircraftId);
      if (letter) setHangarSelectedCaseId(`cp-case-${letter.toLowerCase()}`);
    }
    if (!hangarEntries[serverIndex]) return;
    hangarAircraftSelectionHandler?.(`AIR-${serverIndex + 1}`);
  };

  // Selecting in the scene always updates the local panel; only the external handler is gated on server permission.
  const handleSceneSelect = (objectId: string) => {
    const letter = letterOf(objectId);
    if (letter && (hangarManifest.objectIds as string[]).includes(objectId)) {
      setHangarSelectedObjectId(objectId);
      setHangarSelectedCaseId(`cp-case-${letter.toLowerCase()}`);
      if (objectId.endsWith('-AIR-001')) requestCamera('aircraft', letter);
    }
    sharedHangarAircraftSelectionHandler(objectId);
  };

  const handleDataRoomConfirm = (caseId: string) => {
    if (letterOf(caseId) && hangarEntries.some(entry => entry.caseId === caseId)) setHangarSelectedCaseId(caseId);
    sharedHangarAircraftSelectionHandler(caseId);
  };

  function setHangarMotionOverride(enabled: boolean) {
    const controllers = hangarMotion.current;
    if (!controllers) return;
    if (enabled) controllers.motion.enable(); else controllers.motion.disable();
    setHangarReducedMotion(controllers.motion.reduced);
  }

  function clearHangarMotionOverride() {
    const controllers = hangarMotion.current;
    if (!controllers) return;
    controllers.motion.clearOverride();
    setHangarReducedMotion(controllers.motion.reduced);
  }

  function toggleFollowCamera() {
    const camera = hangarFollowCameraRef.current;
    if (!camera) return;
    const next = camera.toggleFollow();
    setHangarFollowEnabled(next.follow);
    setHangarCameraPose(next.position);
  }

  function runNon3dAction(action: keyof typeof NON_3D_ACTION_TARGETS) {
    const target = document.getElementById(NON_3D_ACTION_TARGETS[action]);
    if (target && !target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1');
    target?.focus();
    if (typeof target?.scrollIntoView === 'function') target.scrollIntoView();
    setHangarActionMessage(NON_3D_ACTION_MESSAGES[action]);
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
          const envelopeCase = isRecord(envelope) ? envelope.case : null;
          const envelopeCaseId = isRecord(envelopeCase) ? envelopeCase.caseId : null;
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
        const recoveredCaseSnapshots = Object.fromEntries(Object.entries(nextCaseSnapshots).map(([caseId, snapshot]) => [caseId, { ...snapshot, current: true, fallbackReason: null }]));
        const recoveredSnapshot = Object.values(recoveredCaseSnapshots).reduce<ServerSceneSnapshot | null>((highest, snapshot) => !highest || (snapshot.revision ?? -1) > (highest.revision ?? -1) ? snapshot : highest, nextSnapshot);
        hangarCaseSnapshotsRef.current = recoveredCaseSnapshots;
        hangarSnapshotRef.current = recoveredSnapshot;
        setHangarSceneEnvelopes(nextSceneEnvelopes);
        setHangarCaseSnapshots(recoveredCaseSnapshots);
        setHangarSnapshot(recoveredSnapshot);
        setHangarStale(false);
        setHangarFallback(null);
        setHangarView('3d');
        setHangarSceneLoaded(true);
      } catch (error) {
        const staleCases: Record<string, ServerSceneSnapshot> = Object.fromEntries(Object.entries(hangarCaseSnapshotsRef.current).map(([caseId, snapshot]) => [caseId, { ...snapshot, current: false, fallbackReason: 'refresh_failed' as const }]));
        await refreshHangarScene(() => { throw new Error('refresh_failed'); }, hangarSnapshotRef.current);
        const retained = Object.values(staleCases).reduce<ServerSceneSnapshot | null>((highest, snapshot) => !highest || (snapshot.revision ?? -1) >= (highest.revision ?? -1) ? snapshot : highest, hangarSnapshotRef.current);
        const retainedStale = retained ? { ...retained, current: false, fallbackReason: 'refresh_failed' as const } : null;
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
        setHangarDiagnostics(current => ({ ...current, sceneLoadDurationMs: null, fallbackReason: 'refresh_failed' }));
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
    const token = ++hangarEntryToken.current;
    const current = () => token === hangarEntryToken.current;
    hangarContextLostRef.current = false;
    hangarEntryStartedAt.current = nowFrom(hangarMonotonicClock);
    setHangarView('loading');
    setHangarSceneLoaded(false);
    setHangarRenderFailed(false);
    setHangarFallback(null);
    HangarSceneRef.current = null;
    try {
      const listResponse = await fetch('/api/v1/cases');
      if (!listResponse.ok) throw new Error('refresh_failed');
      const list = await listResponse.json() as Record<string, unknown>;
      if (list.contractVersion !== 1 || !Array.isArray(list.cases)) throw { fallback: 'contract_invalid' };
      const cases = list.cases as Array<Record<string, unknown>>;
      const envelopes: unknown[] = [];
      for (const entry of cases) {
        const caseId = typeof entry.caseId === 'string' ? entry.caseId : '';
        if (!caseId) throw { fallback: 'contract_invalid' };
        const sceneResponse = await fetch(`/api/v1/cases/${encodeURIComponent(caseId)}/scene`);
        if (!sceneResponse.ok) throw { fallback: 'refresh_failed' };
        let envelope: unknown;
        try { envelope = await sceneResponse.json(); } catch { throw { fallback: 'contract_invalid' }; }
        envelopes.push(envelope);
      }
      for (const envelope of envelopes) {
        if (!validateHangarSceneEnvelope(envelope).valid) throw { fallback: 'contract_invalid' };
      }
      const entries = envelopes.map((envelope, index): HangarSourceSummaryEntry => {
        const scene = envelope as Record<string, unknown>;
        const c = isRecord(scene.case) ? scene.case : {};
        const listed = cases[index] ?? {};
        const listedId = typeof listed.caseId === 'string' ? listed.caseId : '';
        const envelopeId = typeof c.caseId === 'string' ? c.caseId : '';
        if (!listedId || envelopeId !== listedId) throw { fallback: 'contract_invalid' };
        return {
          caseId: listedId,
          name: typeof c.name === 'string' ? c.name : (typeof c.aircraftName === 'string' ? c.aircraftName : (typeof listed.name === 'string' ? listed.name : '')),
          category: typeof c.aircraftCategory === 'string' ? c.aircraftCategory : (typeof c.category === 'string' ? c.category : (typeof listed.aircraftCategory === 'string' ? listed.aircraftCategory : '')),
          unavailable: false,
          sources: availableSources(),
        };
      }).filter(entry => entry.caseId && entry.name && entry.category);
      if (!current()) return;
      setHangarEntries(entries);
      const firstEntryId = entries[0]?.caseId ?? 'cp-case-a';
      setHangarSelectedCaseId(firstEntryId);
      setHangarSelectedObjectId(`SYN-CP-${firstEntryId.slice(-1).toUpperCase()}-AIR-001`);
      const initialCaseSnapshots: Record<string, ServerSceneSnapshot> = {};
      const sceneEnvelopes: Record<string, unknown> = {};
      let latestSnapshot: ServerSceneSnapshot | null = null;
      for (let index = 0; index < envelopes.length; index += 1) {
        const snapshot = snapshotFromServer(envelopes[index]);
        if (!snapshot) throw { fallback: 'contract_invalid' };
        const caseId = typeof cases[index]?.caseId === 'string' ? cases[index].caseId as string : '';
        initialCaseSnapshots[caseId] = snapshot;
        if (caseId) sceneEnvelopes[caseId] = envelopes[index];
        latestSnapshot = snapshot;
      }
      setHangarSceneEnvelopes(sceneEnvelopes);
      hangarCaseSnapshotsRef.current = initialCaseSnapshots;
      hangarSnapshotRef.current = latestSnapshot;
      setHangarCaseSnapshots(initialCaseSnapshots);
      if (latestSnapshot) setHangarSnapshot(latestSnapshot);
      setHangarStale(false);
    } catch (error) {
      if (!current()) return;
      const fallback = isRecord(error) && 'fallback' in error ? String(error.fallback) : 'refresh_failed';
      if (fallback === 'contract_invalid') {
        setHangarView('contract-error');
        setHangarDiagnostics(state => ({ ...state, sceneLoadDurationMs: null, fallbackReason: 'contract_invalid' }));
        return;
      }
      setHangarFallback('refresh_failed');
      setHangarView('fallback');
      setHangarDiagnostics(state => ({ ...state, sceneLoadDurationMs: null, fallbackReason: 'refresh_failed' }));
      return;
    }
    const { isHangarBrowserSupported, probeWebGl2Capability, selectHangarEntry, loadHangarScene, createHangarSceneMotionControllers } = await import('./entryController');
    if (!current()) return;
    const browserSupported = isHangarBrowserSupported(hangarBrowserSupported !== undefined ? hangarBrowserSupported : undefined);
    const webgl2 = hangarWebGl2Probe !== undefined ? hangarWebGl2Probe as never : probeWebGl2Capability();
    const hangarMotionControllers = createHangarSceneMotionControllers();
    hangarMotion.current = hangarMotionControllers;
    setHangarReducedMotion(hangarMotionControllers.motion.reduced);
    const decision = await selectHangarEntry({ browserSupported, contractVersion: hangarContractVersion, webgl2 }, hangarLazyLoader ?? (() => loadHangarScene(hangarMotionControllers)));
    if (!current()) return;
    const fallbackReason = decision.fallbackReason as HangarFallbackReason;
    const webgl2Available = isRecord(webgl2) && (webgl2.webgl2 === true || webgl2.supported === true);
    setHangarDiagnostics({ sceneLoadDurationMs: null, webgl2Available, browserSupported, fallbackReason });
    if (decision.mode === '3d') {
      if (typeof decision.loadedScene === 'function') HangarSceneRef.current = decision.loadedScene as ComponentType<HangarSceneProps>;
      setActivePreset('overview');
      setHangarSceneLoaded(true);
      setHangarView('3d');
    } else if (decision.fallbackReason === 'contract_invalid') setHangarView('contract-error');
    else {
      setHangarFallback(fallbackReason);
      setHangarView('fallback');
    }
  }

  // Enter once per mount; the ref keeps a StrictMode double effect from starting a second entry.
  useEffect(() => {
    if (hangarEnteredRef.current) return;
    hangarEnteredRef.current = true;
    void enterHangar();
  }, []);

  const LoadedHangarScene = HangarSceneRef.current;
  const hangarActiveEnvelope = hangarSceneEnvelopes[hangarSelectedCaseId] ?? hangarSnapshot?.completeContent;
  const hangarComposedScene = isRecord(hangarActiveEnvelope) ? hangarActiveEnvelope : hangarSnapshot?.completeContent;
  const isTechnicalReviewer = session.role === 'technical-reviewer';
  const hangarCaseResetEnvelopeBase = hangarSceneEnvelopes['cp-case-c'] ?? hangarCaseSnapshots['cp-case-c']?.completeContent ?? hangarComposedScene;
  const hangarCaseResetEnvelope = !isTechnicalReviewer
    ? { ...(isRecord(hangarCaseResetEnvelopeBase) ? hangarCaseResetEnvelopeBase : {}), canResetBlockedVariant: false, allowedActions: [] }
    : hangarCaseResetEnvelopeBase;
  const hangarCompositionObjects = useMemo(() => hangarEntries.flatMap(entry => {
    const envelope = hangarSceneEnvelopes[entry.caseId];
    const objects = isRecord(envelope) && Array.isArray(envelope.objects) ? envelope.objects as Record<string, unknown>[] : [];
    return objects.map(object => {
      const objectId = String(object.objectId ?? '');
      const caseName = entry.name;
      const label = String(object.accessibleName ?? object.label ?? object.displayName ?? (
        objectId.endsWith('-AIR-001') ? `${caseName} aircraft`
          : objectId.endsWith('-ENG-001') ? `${caseName} engine`
            : objectId.endsWith('-CMP-001') ? `${caseName} component 1`
              : objectId.endsWith('-CMP-002') ? `${caseName} component 2` : objectId
      ));
      return { ...object, label, accessibleName: label } as Record<string, unknown> & { label: string; accessibleName: string };
    });
  }), [hangarEntries, hangarSceneEnvelopes]);
  const selectedCaseEnvelopeBase = hangarSceneEnvelopes[hangarSelectedCaseId] ?? (isRecord(hangarComposedScene) ? hangarComposedScene : {});
  const selectedCaseEnvelope = !isTechnicalReviewer && isRecord(selectedCaseEnvelopeBase)
    ? { ...selectedCaseEnvelopeBase, canResetBlockedVariant: false, allowedActions: [], case: { ...(isRecord(selectedCaseEnvelopeBase.case) ? selectedCaseEnvelopeBase.case : {}), permission: false, allowedActions: [] } }
    : selectedCaseEnvelopeBase;
  const selectedCaseRoot = isRecord(selectedCaseEnvelope) ? selectedCaseEnvelope : {};
  const combinedHangarEnvelope = { ...selectedCaseRoot, objects: hangarCompositionObjects };

  // Every aircraft gets a label in the scene: the overlay reads one case view per aircraft and the composed objects.
  const hangarSceneData = useMemo(() => {
    const base = isRecord(hangarComposedScene) ? hangarComposedScene : {};
    const cases = hangarEntries.map(entry => {
      const envelope = hangarSceneEnvelopes[entry.caseId];
      const caseView = isRecord(envelope) && isRecord(envelope.case) ? envelope.case : {};
      return {
        ...caseView,
        caseId: entry.caseId,
        name: HANGAR_AIRCRAFT_MODELS[entry.caseId] ?? entry.name,
        category: `${entry.name} – ${entry.category}`,
        capabilityLabel: typeof caseView.capabilityLabel === 'string' ? caseView.capabilityLabel : 'Unknown',
        nextAction: nextActionText(caseView),
      };
    });
    return { ...base, cases, objects: hangarCompositionObjects };
  }, [hangarComposedScene, hangarEntries, hangarSceneEnvelopes, hangarCompositionObjects]);

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
    if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
    const rackWalk = document.querySelector<HTMLElement>('[data-hangar-guide-line] [data-hangar-walk-region]');
    if (rackWalk && rackWalk !== target) rackWalk.dispatchEvent(new KeyboardEvent('keydown', { key: event.key, repeat: event.repeat, bubbles: true }));
    const controller = hangarWalkControllerRef.current;
    const camera = hangarFollowCameraRef.current;
    if (!controller || !camera) return;
    const result = controller.step(event.key, event.repeat);
    if (!result.accepted) return;
    setHangarAvatarPosition({ x: result.position.x, y: 0, z: result.position.z });
    if (camera.followEnabled) {
      const pose = camera.applyAvatarStep({ x: result.position.x, y: 0, z: result.position.z });
      setHangarCameraPose(pose.target.position);
    }
    event.preventDefault();
  };

  const caseViewOf = (caseId: string) => {
    const envelope = hangarSceneEnvelopes[caseId];
    return isRecord(envelope) && isRecord(envelope.case) ? envelope.case : null;
  };
  const modelOf = (entry: HangarSourceSummaryEntry) => HANGAR_AIRCRAFT_MODELS[entry.caseId] ?? entry.name;
  const selectedEntry = hangarEntries.find(entry => entry.caseId === hangarSelectedCaseId) ?? hangarEntries[0];
  const selectedObject = hangarCompositionObjects.find(object => object.objectId === hangarSelectedObjectId);

  function selectCase(entry: HangarSourceSummaryEntry) {
    const letter = letterOf(entry.caseId);
    setHangarSelectedCaseId(entry.caseId);
    if (letter) {
      setHangarSelectedObjectId(`SYN-CP-${letter}-AIR-001`);
      requestCamera('aircraft', letter);
    }
  }

  function statusPill(caseId: string) {
    const status = describeHangarCaseStatus(hangarSceneEnvelopes[caseId], releasedCaseIds.includes(caseId));
    return <span className={`hangar-pill hangar-pill--${status.tone}`} data-hangar-status-tone={status.tone}>{status.text}</span>;
  }

  function renderCaseStatusList(withSelect: boolean): ReactNode {
    return (
      <section className="hangar-card" aria-labelledby="hangar-case-status-heading">
        <h3 id="hangar-case-status-heading" className="hangar-card__title">Case status</h3>
        <ul className="hangar-case-list">
          {hangarEntries.map(entry => {
            const caseView = caseViewOf(entry.caseId);
            const selected = entry.caseId === hangarSelectedCaseId;
            return (
              <li key={entry.caseId} className={`hangar-case${selected ? ' hangar-case--selected' : ''}`} data-hangar-case={entry.caseId}>
                <div className="hangar-case__text">
                  <span className="hangar-case__model">{modelOf(entry)}</span>
                  <span className="hangar-case__meta">{entry.name} – {entry.category}</span>
                  {statusPill(entry.caseId)}
                  <span className="hangar-case__meta">Next: {nextActionText(caseView)}</span>
                </div>
                <div className="hangar-case__actions">
                  {withSelect && <button type="button" className="hangar-btn hangar-btn--quiet" aria-pressed={selected} onClick={() => selectCase(entry)}>{selected ? 'Selected' : 'Select'}<span className="hangar-visually-hidden"> {modelOf(entry)}</span></button>}
                  {onOpenCase && <button type="button" className="hangar-btn hangar-btn--quiet" onClick={() => onOpenCase(entry.caseId)}>Open journey<span className="hangar-visually-hidden"> for {modelOf(entry)}</span></button>}
                </div>
              </li>
            );
          })}
        </ul>
      </section>
    );
  }

  function renderNon3d(): ReactNode {
    return (
      <div className="hangar-fallback-grid" id="non-3d-list">
        <div className="hangar-card hangar-card--data-room">
          <HangarNon3dDataRoom
            entries={hangarEntries.map((entry): HangarSourceSummaryEntry => ({ ...entry, sources: availableSources() }))}
            scope={hangarEntries.map(entry => entry.caseId)}
            displayNames={HANGAR_AIRCRAFT_MODELS}
            onConfirm={handleDataRoomConfirm}
          />
        </div>
        <div className="hangar-fallback-side">
          {renderCaseStatusList(false)}
          <section className="hangar-card" aria-label="Legacy non-3D actions">
            <h3 className="hangar-card__title">Non-3D actions</h3>
            <div className="hangar-button-row">
              <button type="button" className="hangar-btn" onClick={() => runNon3dAction('selection')}>Select case</button>
              <button type="button" className="hangar-btn" onClick={() => runNon3dAction('status')}>View status</button>
              <button type="button" className="hangar-btn" onClick={() => runNon3dAction('evidence')}>View evidence</button>
              <button type="button" className="hangar-btn" onClick={() => runNon3dAction('journey')}>Follow journey</button>
              <button type="button" className="hangar-btn" onClick={() => runNon3dAction('review')}>Open review</button>
            </div>
            <p role="status" className="hangar-muted">{hangarActionMessage}</p>
          </section>
        </div>
      </div>
    );
  }

  function renderSelectionPanel(): ReactNode {
    if (!selectedEntry) return null;
    const caseView = caseViewOf(selectedEntry.caseId);
    const aircraftObject = hangarCompositionObjects.find(object => object.objectId === `SYN-CP-${letterOf(selectedEntry.caseId)}-AIR-001`);
    const registration = typeof aircraftObject?.displayName === 'string' ? aircraftObject.displayName : null;
    const part = selectedObject && !String(selectedObject.objectId).endsWith('-AIR-001') ? String(selectedObject.label) : null;
    let evidence: ReactNode = null;
    if (hangarSelectedObjectId) {
      const letter = letterOf(hangarSelectedObjectId)?.toLowerCase();
      const selectionSnapshotEnvelope = letter ? hangarCaseSnapshots[`cp-case-${letter}`]?.completeContent : null;
      const selectionEnvelopeFromSnapshot = isRecord(selectionSnapshotEnvelope) ? selectionSnapshotEnvelope : null;
      const sceneEnvelopeForSelection = letter ? hangarSceneEnvelopes[`cp-case-${letter}`] ?? selectedCaseEnvelope : selectedCaseEnvelope;
      const snapshotCase = selectionEnvelopeFromSnapshot && isRecord(selectionEnvelopeFromSnapshot.case) ? selectionEnvelopeFromSnapshot.case : null;
      const sceneCase = isRecord(sceneEnvelopeForSelection) && isRecord(sceneEnvelopeForSelection.case) ? sceneEnvelopeForSelection.case : null;
      const snapshotRevision = typeof snapshotCase?.caseRevision === 'number' ? snapshotCase.caseRevision : -1;
      const sceneRevision = typeof sceneCase?.caseRevision === 'number' ? sceneCase.caseRevision : -1;
      const selectionEnvelope = sceneRevision >= snapshotRevision ? sceneEnvelopeForSelection : selectionEnvelopeFromSnapshot;
      const selectionCase = isRecord(selectionEnvelope) && isRecord(selectionEnvelope.case) ? selectionEnvelope.case : undefined;
      const selectionRevision = typeof selectionCase?.caseRevision === 'number' ? selectionCase.caseRevision : null;
      const selectionSnapshot = letter ? hangarCaseSnapshots[`cp-case-${letter}`] : hangarSnapshot;
      evidence = <HangarSelectionEvidence key={`${hangarSelectedObjectId}:${selectionRevision}`} scene={selectionEnvelope} manifest={hangarManifest.objectIds} identifier={hangarSelectedObjectId} snapshot={hangarStale ? { ...selectionSnapshot, current: false, fallbackReason: 'refresh_failed' } : selectionSnapshot ? { ...selectionSnapshot, current: true, fallbackReason: null } : selectionSnapshot} stale={hangarStale} />;
    }
    return (
      <section className="hangar-card hangar-card--selection" aria-labelledby="hangar-selection-heading">
        <p className="hangar-card__eyebrow">Selected aircraft</p>
        <h3 id="hangar-selection-heading" className="hangar-card__headline">{modelOf(selectedEntry)}</h3>
        <p className="hangar-case__meta">{selectedEntry.name} – {selectedEntry.category}{registration ? <> <span aria-hidden="true">|</span> Registration {registration}</> : null}</p>
        {statusPill(selectedEntry.caseId)}
        <dl className="hangar-facts">
          {part && <div><dt>Selected part</dt><dd>{part}</dd></div>}
          <div><dt>Next action</dt><dd>{nextActionText(caseView)}</dd></div>
          <div><dt>Owner</dt><dd>{typeof caseView?.owner === 'string' && caseView.owner ? caseView.owner : 'Unassigned'}</dd></div>
          <div><dt>Capability</dt><dd>{typeof caseView?.capabilityLabel === 'string' ? caseView.capabilityLabel : 'Unknown'}</dd></div>
        </dl>
        {onOpenCase && <button type="button" className="hangar-btn hangar-btn--primary" onClick={() => onOpenCase(selectedEntry.caseId)}>Open journey</button>}
        {evidence && <div className="hangar-evidence">{evidence}</div>}
      </section>
    );
  }

  const fallbackNotice = (() => {
    if (hangarView === 'contract-error') return { title: 'Hangar data needs attention', body: <p>Invalid hangar scene contract. <a href="#non-3d-list">Use the supported contract version in the non-3D list.</a></p> };
    if (hangarFallback === 'unsupported_browser') return { title: '3D hangar not available in this browser', body: <p>Your browser is not supported. Use the current or previous major version of Chrome or Edge.</p> };
    if (hangarFallback === 'refresh_failed') return { title: 'Hangar data could not be loaded', body: <p>Hangar refresh failed. Use the non-3D list.</p> };
    if (hangarFallback === 'render_failed') return { title: 'The 3D scene stopped rendering', body: <p>The graphics context was lost. Reload the hangar or use the non-3D list.</p> };
    return { title: '3D graphics unavailable', body: <p>WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2-capable browser.</p> };
  })();

  const diagnosticsSection = (
    <details className="hangar-details">
      <summary>Accessibility and diagnostics</summary>
      <div className="hangar-details__body">
        <div aria-label="Hangar diagnostics">
          <HangarLocalDiagnostics session={session} {...hangarDiagnostics} />
        </div>
        {hangarView === '3d' && hangarSceneLoaded && (
          <>
            <HangarSemanticControls
              scene={combinedHangarEnvelope}
              manifest={hangarManifest}
              onActivate={(control, source) => {
                const objectId = typeof control === 'string' ? control : control.id;
                setHangarSelectedObjectId(objectId);
                if (isRecord(source) && typeof source.caseId === 'string') {
                  setHangarSelectedCaseId(source.caseId);
                } else {
                  const letter = letterOf(objectId);
                  if (letter) setHangarSelectedCaseId(`cp-case-${letter.toLowerCase()}`);
                }
              }}
            />
            <section className="hangar-walk" data-hangar-app-composition="true" data-hangar-walk-region="true" role="region" aria-label="Walk region" tabIndex={0} onKeyDown={handleHangarAppWalk}>
              <p className="hangar-muted">Focus this region and use the arrow keys to walk to the data rack. Escape clears the guide line.</p>
              <p data-hangar-avatar-position={`${hangarAvatarPosition.x},${hangarAvatarPosition.z}`}>{`Avatar: ${hangarAvatarPosition.x}, ${hangarAvatarPosition.z}`}</p>
              <p className="hangar-muted" data-hangar-camera-pose={`${hangarCameraPose.x},${hangarCameraPose.y},${hangarCameraPose.z}`}>Camera pose</p>
              <HangarGuideLineAdapter
                entries={hangarEntries}
                markers={hangarDataRoomMarkers}
                avatar={{ x: 0, z: 0 }}
                config={{ walkablePolygon: WALKABLE_POLYGON, avatarRadius: 0.3, gridSize: 0.25, avatar: { x: 0, z: 0 }, onSelect: sharedHangarAircraftSelectionHandler }}
                onSelect={sharedHangarAircraftSelectionHandler}
                reducedMotion={hangarReducedMotion}
                walkRegionLabel="Data rack walk region"
              />
              <div className="hangar-button-row" aria-label="Reduced motion controls">
                <button type="button" className="hangar-btn" aria-pressed={hangarReducedMotion} onClick={() => setHangarMotionOverride(true)}>Enable reduced motion</button>
                <button type="button" className="hangar-btn" aria-pressed={!hangarReducedMotion} onClick={() => setHangarMotionOverride(false)}>Disable reduced motion</button>
                <button type="button" className="hangar-btn" onClick={clearHangarMotionOverride}>Use system preference</button>
              </div>
            </section>
          </>
        )}
      </div>
    </details>
  );

  return (
    <section className="hangar-mode" aria-labelledby="hangar-heading">
      <header className="hangar-mode__header">
        <div>
          <p className="hangar-mode__eyebrow">Hangar mode</p>
          <h2 id="hangar-heading" className="hangar-mode__title">Hangar</h2>
          <p className="hangar-mode__lede">Select an aircraft in the hangar to see its evidence, then open its journey.</p>
          {hangarSnapshot && <p className="hangar-mode__snapshot hangar-visually-hidden" aria-label="Hangar retained snapshot">Revision: {hangarSnapshot.revision}; Labels: {String(hangarSnapshot.owner)}; {String(hangarSnapshot.capabilityLabel)}</p>}
        </div>
        <div className="hangar-button-row">
          <button type="button" className="hangar-btn" disabled={hangarView === 'loading'} onClick={() => { void enterHangar(); }}>Reload hangar</button>
          {onExit && <button type="button" className="hangar-btn hangar-btn--ghost" onClick={onExit}>Back to journey board</button>}
        </div>
      </header>

      {hangarView === 'loading' && (
        <div className="hangar-stage hangar-stage--loading" role="status">
          <span className="hangar-spinner" aria-hidden="true" />
          <span>Loading the hangar</span>
        </div>
      )}

      {hangarView === '3d' && hangarSceneLoaded && (
        <div className="hangar-mode__layout">
          <div className="hangar-mode__main">
            <div className="hangar-toolbar">
              <div className="hangar-segmented" role="group" aria-label="Hangar view presets">
                <button type="button" aria-pressed={activePreset === 'overview'} onClick={() => requestCamera('overview')}>Overview</button>
                {hangarEntries.map(entry => (
                  <button key={entry.caseId} type="button" aria-label={`View ${entry.name}`} aria-pressed={activePreset === entry.caseId} onClick={() => selectCase(entry)}>
                    {modelOf(entry)}
                  </button>
                ))}
              </div>
              <div className="hangar-toolbar__actions">
                <button type="button" className="hangar-btn" onClick={() => { void refreshHangarSnapshot(); }}>Refresh Hangar scene</button>
                <button type="button" className="hangar-btn hangar-btn--toggle" aria-pressed={hangarReducedMotion} onClick={() => setHangarMotionOverride(!hangarReducedMotion)}>Reduced motion</button>
                <button type="button" className="hangar-btn hangar-btn--toggle" aria-pressed={hangarFollowEnabled} onClick={toggleFollowCamera}>{hangarFollowEnabled ? 'Follow avatar: on' : 'Follow avatar: off'}</button>
              </div>
            </div>
            <div className="hangar-stage" aria-label="Hangar 3D scene">
              {hangarStale && <p className="hangar-stage__banner" role="status">Showing the last loaded scene. Refresh to try again.</p>}
              <HangarErrorBoundary snapshot={hangarSnapshot} lastSnapshot={hangarSnapshot} onError={handleHangarRenderError}>
                {LoadedHangarScene ? (
                  <LoadedHangarScene
                    {...({
                      sceneData: hangarSceneData,
                      hangarClientSnapshot,
                      refreshReader: async () => {
                        const first = hangarEntries[0]?.caseId;
                        if (!first) throw new Error('refresh_failed');
                        const response = await fetch(`/api/v1/cases/${encodeURIComponent(first)}/scene`);
                        if (!response.ok) throw new Error('refresh_failed');
                        return response.json();
                      },
                      externalRefresh: true,
                      snapshot: hangarSnapshot,
                    } as Record<string, unknown>)}
                    cameraPreset={cameraPreset}
                    selectedObjectId={hangarSelectedObjectId}
                    onSelect={handleSceneSelect}
                    onCreated={handleHangarCanvasCreated}
                    onContextLost={handleHangarContextLost}
                  />
                ) : <canvas aria-label="3D hangar scene" />}
              </HangarErrorBoundary>
              <ul className="hangar-legend" aria-label="Status colours">
                <li><span className="hangar-dot hangar-dot--done" aria-hidden="true" />Complete</li>
                <li><span className="hangar-dot hangar-dot--human" aria-hidden="true" />Waiting for a person</li>
                <li><span className="hangar-dot hangar-dot--ai" aria-hidden="true" />AI at work</li>
              </ul>
            </div>
            {hangarRenderFailed && renderNon3d()}
          </div>
          <aside className="hangar-side" aria-label="Hangar details">
            {renderSelectionPanel()}
            <div className="hangar-card hangar-card--journey">
              <HangarGuidedJourney scene={selectedCaseEnvelope} />
            </div>
            {renderCaseStatusList(true)}
            {hangarSelectedCaseId === 'cp-case-c' && (
              <div className="hangar-card hangar-card--reset">
                <HangarCaseReset
                  envelope={hangarCaseResetEnvelope}
                  csrfToken={session.csrfToken}
                  transport={(url, init) => fetch(url, init)}
                  refetch={() => refreshHangarSnapshot()}
                  confirmReset={() => true}
                />
              </div>
            )}
          </aside>
        </div>
      )}

      {(hangarView === 'fallback' || hangarView === 'contract-error') && (
        <div className="hangar-fallback">
          <div className="hangar-notice" role="alert">
            <h3 className="hangar-notice__title">{fallbackNotice.title}</h3>
            {fallbackNotice.body}
          </div>
          {hangarView === 'fallback' && (
            <div className="hangar-button-row">
              <button type="button" className="hangar-btn hangar-btn--primary" onClick={() => { void refreshHangarSnapshot(); }}>Refresh Hangar scene</button>
            </div>
          )}
          {renderNon3d()}
        </div>
      )}

      {hangarView !== 'loading' && diagnosticsSection}
    </section>
  );
}
