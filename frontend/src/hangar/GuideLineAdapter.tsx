import { createElement, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { HangarGuideLineController, type HangarGuideMarker, type HangarGuideSelectionHandler } from './guideLine';
import { type HangarRouteConfig } from './routePlanner';
import { shouldReduceHangarMotion } from './reducedMotion';
import { isHangarWalkStepAllowed, HANGAR_WALK_STEP } from './walkController';

export type HangarGuideLineAdapterProps = {
  config?: HangarRouteConfig; marker?: HangarGuideMarker | null; aircraftId?: string;
  avatar?: HangarGuideMarker; onSelect?: HangarGuideSelectionHandler; reducedMotion?: boolean;
  transport?: (request: unknown) => unknown; walkRegionLabel?: string;
};

export function HangarGuideLineAdapter({ config = {}, marker, aircraftId = 'selected-aircraft', avatar, onSelect, reducedMotion, transport, walkRegionLabel = 'Walk region' }: HangarGuideLineAdapterProps) {
  const stableConfig = { ...config, avatar: avatar ?? config.avatar, transport };
  const controllerRef = useRef<HangarGuideLineController | null>(null);
  if (!controllerRef.current) controllerRef.current = new HangarGuideLineController(stableConfig, onSelect ?? config.onSelect);
  const controller = controllerRef.current;
  const initialAvatar = avatar ?? config.avatar ?? { x: 0, z: 0 };
  const [position, setPosition] = useState<HangarGuideMarker>(initialAvatar);
  const [state, setState] = useState(() => marker ? controller.confirm(marker, aircraftId, initialAvatar) : controller.getState());
  useEffect(() => { if (marker) setState(controller.confirm(marker, aircraftId, avatar ?? config.avatar ?? { x: 0, z: 0 })); }, [marker?.x, marker?.z, aircraftId]); // route replacement is local state only
  const staticLine = reducedMotion ?? shouldReduceHangarMotion();
  const key = (event: ReactKeyboardEvent<HTMLElement>) => {
    if (event.key === 'Escape') { setState(controller.escape()); event.preventDefault(); return; }
    if (!['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key)) return;
    const current = position;
    const delta: Record<string, HangarGuideMarker> = { ArrowUp: { x: 0, z: -HANGAR_WALK_STEP }, ArrowDown: { x: 0, z: HANGAR_WALK_STEP }, ArrowLeft: { x: -HANGAR_WALK_STEP, z: 0 }, ArrowRight: { x: HANGAR_WALK_STEP, z: 0 } };
    const next = { x: current.x + delta[event.key].x, z: current.z + delta[event.key].z };
    if (!isHangarWalkStepAllowed(current, next, config)) { setState(controller.step(false, current)); event.preventDefault(); return; }
    setPosition(next); setState(controller.acceptedStep(next)); event.preventDefault();
  };
  return createElement('section', { 'data-hangar-guide-line': 'true', 'data-static-line': staticLine ? 'true' : 'false' },
    createElement('section', { 'data-hangar-walk-region': true, tabIndex: 0, 'aria-label': walkRegionLabel, onKeyDown: key }, 'Hangar walk region'),
    state.route.length ? createElement('svg', { 'aria-hidden': true, 'data-hangar-guide-line-visible': 'true', focusable: 'false', points: state.route.map(p => `${p.x},${p.z}`).join(' ') }, state.route.map((point, index) => createElement('circle', { key: index, cx: point.x, cy: point.z, r: 0.04 }))) : null,
    createElement('p', { 'aria-live': 'polite', 'aria-atomic': true, 'data-hangar-guide-live': true }, state.liveText ?? ''),
    state.status === 'unreachable' ? createElement('p', { role: 'alert' }, state.message) : null,
  );
}
export default HangarGuideLineAdapter;
export { HangarGuideLineAdapter as GuideLineAdapter };
