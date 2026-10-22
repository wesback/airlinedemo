import { createElement, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { HangarGuideLineController, type HangarGuideMarker, type HangarGuideSelectionHandler } from './guideLine';
import { type HangarRouteConfig } from './routePlanner';
import { shouldReduceHangarMotion } from './reducedMotion';
import { isHangarWalkStepAllowed, HANGAR_WALK_STEP } from './walkController';
import HangarRackMenu, { type HangarRackMenuEntry } from './rackMenu';

export type HangarGuideLineAdapterProps = {
  config?: HangarRouteConfig; marker?: HangarGuideMarker | null; aircraftId?: string;
  avatar?: HangarGuideMarker; onSelect?: HangarGuideSelectionHandler; reducedMotion?: boolean;
  transport?: (request: unknown) => unknown; walkRegionLabel?: string;
  onMenuClose?: () => unknown; onCloseMenu?: () => unknown; closeMenu?: () => unknown;
  entries?: HangarRackMenuEntry[] | null; markers?: Record<string, HangarGuideMarker>;
};

export function HangarGuideLineAdapter({ config = {}, marker, aircraftId = 'selected-aircraft', avatar, onSelect, reducedMotion, transport, walkRegionLabel = 'Walk region', onMenuClose, onCloseMenu, closeMenu, entries, markers }: HangarGuideLineAdapterProps) {
  const stableConfig = { ...config, avatar: avatar ?? config.avatar, transport };
  const controllerRef = useRef<HangarGuideLineController | null>(null);
  if (!controllerRef.current) controllerRef.current = new HangarGuideLineController(stableConfig, onSelect ?? config.onSelect);
  const controller = controllerRef.current;
  const initialAvatar = avatar ?? config.avatar ?? { x: 0, z: 0 };
  const [position, setPosition] = useState<HangarGuideMarker>(initialAvatar);
  const [selectedMarker, setSelectedMarker] = useState<HangarGuideMarker | null>(marker ?? null);
  const [selectedAircraft, setSelectedAircraft] = useState<string>(aircraftId);
  const effectiveMarker = marker !== undefined ? marker : selectedMarker;
  const effectiveAircraft = marker !== undefined ? aircraftId : selectedAircraft;
  const confirmEntry = (caseId: string) => {
    rackConfirmedRef.current = true;
    const nextMarker = markers?.[caseId] ?? config.marker ?? null;
    setSelectedAircraft(caseId);
    setSelectedMarker(nextMarker);
  };
  const staticLine = reducedMotion ?? shouldReduceHangarMotion();
  const [state, setState] = useState(() => {
    if (!marker) return controller.getState();
    return controller.confirm(marker, aircraftId, initialAvatar);
  });
  const confirmedKeyRef = useRef<string | null>(null);
  const rackConfirmedRef = useRef(false);
  useEffect(() => {
    if (marker !== undefined) {
      setSelectedMarker(marker);
      if (marker === null) {
        confirmedKeyRef.current = null;
        setState(controller.escape());
        return;
      }
    }
    if (!effectiveMarker) {
      confirmedKeyRef.current = null;
      setState(controller.escape());
      return;
    }
    const key = `${effectiveMarker.x},${effectiveMarker.z},${effectiveAircraft}`;
    const confirmed = controller.confirm(effectiveMarker, effectiveAircraft, avatar ?? config.avatar ?? { x: 0, z: 0 });
    setState(confirmed);
    if (confirmedKeyRef.current !== key) {
      confirmedKeyRef.current = key;
      if (!rackConfirmedRef.current) {
        const close = closeMenu ?? onMenuClose ?? onCloseMenu;
        close?.();
      }
      rackConfirmedRef.current = false;
    }
  }, [marker?.x, marker?.z, selectedMarker?.x, selectedMarker?.z, selectedAircraft]); // route replacement closes the rack menu locally
  const key = (event: ReactKeyboardEvent<HTMLElement>) => {
    if (event.key === 'Escape') { confirmedKeyRef.current = null; setState(controller.escape()); event.preventDefault(); return; }
    if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
    const current = position;
    const delta: Record<string, HangarGuideMarker> = { ArrowUp: { x: 0, z: -HANGAR_WALK_STEP }, ArrowDown: { x: 0, z: HANGAR_WALK_STEP }, ArrowLeft: { x: -HANGAR_WALK_STEP, z: 0 }, ArrowRight: { x: HANGAR_WALK_STEP, z: 0 } };
    const next = { x: current.x + delta[event.key].x, z: current.z + delta[event.key].z };
    if (!isHangarWalkStepAllowed(current, next, config)) { setState(controller.step(false, current)); event.preventDefault(); return; }
    setPosition(next); setState(controller.acceptedStep(next)); event.preventDefault();
  };
  return createElement('section', { 'data-hangar-guide-line': 'true', 'data-static-line': staticLine ? 'true' : 'false' },
    entries ? createElement(HangarRackMenu, { entries, onConfirm: confirmEntry, onClose: closeMenu, closeMenu, onMenuClose: onMenuClose, transport }) : null,
    staticLine && state.route.length ? createElement('span', { 'aria-hidden': true, 'data-hangar-static-guide': true }) : null,
    createElement('section', { 'data-hangar-walk-region': true, tabIndex: 0, 'aria-label': walkRegionLabel, onKeyDown: key }, 'Hangar walk region'),
    state.route.length ? (() => {
      const xs = state.route.map(point => point.x);
      const zs = state.route.map(point => point.z);
      const minX = Math.min(...xs), maxX = Math.max(...xs), minZ = Math.min(...zs), maxZ = Math.max(...zs);
      const width = Math.max(maxX - minX, 1);
      const height = Math.max(maxZ - minZ, 1);
      const padding = Math.max(width, height) * 0.1;
      return createElement('svg', { 'aria-hidden': true, 'data-hangar-guide-line-visible': 'true', focusable: 'false', viewBox: `${minX - padding} ${minZ - padding} ${width + padding * 2} ${height + padding * 2}` },
        createElement('polyline', { points: state.route.map(p => `${p.x},${p.z}`).join(' '), fill: 'none', stroke: 'currentColor' }));
    })() : null,
    createElement('p', { 'aria-live': 'polite', 'aria-atomic': true, 'data-hangar-guide-live': true }, state.liveText ?? ''),
    state.status === 'unreachable' ? createElement('p', { role: 'alert' }, state.message) : null,
  );
}
export default HangarGuideLineAdapter;
export { HangarGuideLineAdapter as GuideLineAdapter };
