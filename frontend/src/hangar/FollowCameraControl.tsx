import { createElement, useState, type ReactNode } from 'react';
import { HangarFollowCamera, type HangarFollowCameraState } from './followCamera';

export type FollowCameraControlProps = {
  controller: HangarFollowCamera;
  onFollowChange?: (enabled: boolean, state: HangarFollowCameraState) => void;
  onOverview?: () => void;
  onAircraft?: () => void;
};

export function FollowCameraControl({ controller, onFollowChange, onOverview, onAircraft }: FollowCameraControlProps) {
  const [state, setState] = useState(controller.getState());
  const change = (next: HangarFollowCameraState) => { setState(controller.getState()); onFollowChange?.(controller.followEnabled, next); };
  const toggle = () => change(controller.toggleFollow());
  const overview = () => { controller.applyPreset('overview'); change(controller.getState()); onOverview?.(); };
  const aircraft = () => { controller.applyPreset('aircraft'); change(controller.getState()); onAircraft?.(); };
  const keyDown = (event: { key?: unknown; target?: EventTarget | null; currentTarget?: EventTarget | null; preventDefault?: () => void }) => {
    const key = typeof event.key === 'string' ? event.key : '';
    if (!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','=','-','_','Tab'].includes(key) || key === 'Tab') return;
    if (event.target !== event.currentTarget && event.target instanceof HTMLButtonElement) return;
    change(controller.key(key));
    event.preventDefault?.();
  };
  return createElement('section', { 'data-hangar-camera-region': true, 'data-hangar-follow-camera': String(state.follow), 'aria-label': 'Camera controls', tabIndex: 0, onKeyDown: keyDown },
    createElement('button', { type: 'button', 'aria-pressed': state.follow, onClick: toggle }, state.follow ? 'Follow avatar: on' : 'Follow avatar: off'),
    createElement('button', { type: 'button', onClick: overview }, 'Overview'),
    createElement('button', { type: 'button', onClick: aircraft }, 'Aircraft'),
    createElement('button', { type: 'button', onKeyDown: keyDown }, 'Orbit camera'),
  );
}

export const HangarFollowCameraControl = FollowCameraControl;
export default FollowCameraControl;
