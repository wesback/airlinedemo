import { createElement } from 'react';

vi.mock('@react-three/fiber', () => ({ Canvas: () => null }));
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHangarMode, stubCaseApi } from './hangarAppHarness';
import type { HangarSceneProps } from '../src/hangar/entryController';

let sceneProps: HangarSceneProps = {};
const lazyLoader = () => function Scene(props: HangarSceneProps) { sceneProps = props; return createElement('canvas', { 'aria-label': '3D hangar scene' }); };

async function renderPlayableHangar(selection = vi.fn()) {
  stubCaseApi();
  renderHangarMode({ hangarContractVersion: 1, hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: lazyLoader, hangarAircraftSelectionHandler: selection, onOpenCase: vi.fn() });
  await screen.findByRole('button', { name: 'Refresh Hangar scene' });
  return screen.getByLabelText('Hangar 3D scene');
}

function press(stage: HTMLElement, key: string, times = 1) {
  for (let i = 0; i < times; i++) act(() => { fireEvent.keyDown(stage, { key }); });
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); sceneProps = {}; });

describe('Hangar play', () => {
  it('walks the avatar in the scene with arrow keys, key repeats included, and the camera follows it', async () => {
    const stage = await renderPlayableHangar();
    expect(stage.tabIndex).toBe(0);
    expect(document.activeElement).toBe(stage);
    expect(sceneProps.avatarTarget).toEqual({ x: -4, z: 20 });
    expect(sceneProps.followPose).toEqual({ target: { x: -4, y: 1, z: 20 }, radius: 6, azimuthDegrees: 0, polarDegrees: 55 });

    press(stage, 'ArrowUp');
    act(() => { fireEvent.keyDown(stage, { key: 'ArrowUp', repeat: true }); });
    expect(sceneProps.avatarTarget).toEqual({ x: -4, z: 19 });
    expect(sceneProps.followPose?.target).toEqual({ x: -4, y: 1, z: 19 });

    press(stage, 'w');
    fireEvent.keyDown(screen.getByRole('button', { name: 'Refresh Hangar scene' }), { key: 'ArrowUp' });
    expect(sceneProps.avatarTarget).toEqual({ x: -4, z: 19 });

    fireEvent.click(within(screen.getByRole('group', { name: 'Hangar view presets' })).getByRole('button', { name: 'Overview' }));
    expect(sceneProps.followPose).toBeNull();
    expect(screen.getByRole('button', { name: 'Follow avatar: off' })).toBeTruthy();
  });

  it('opens the dossiers at the data rack, guides to the chosen aircraft and selects it on arrival', async () => {
    const selection = vi.fn();
    const stage = await renderPlayableHangar(selection);
    press(stage, 'ArrowLeft', 19);
    press(stage, 'ArrowUp', 79);
    expect(screen.queryByRole('dialog', { name: 'Aircraft dossiers' })).toBeNull();
    press(stage, 'ArrowRight', 16);
    expect(sceneProps.avatarTarget).toEqual({ x: -5.5, z: -19.5 });

    const dialog = screen.getByRole('dialog', { name: 'Aircraft dossiers' });
    const guideButtons = within(dialog).getAllByRole('button', { name: /^Guide me to / });
    expect(guideButtons.map(button => button.textContent)).toEqual(['Guide me to Airbus A320neo', 'Guide me to Embraer E195-E2', 'Guide me to Airbus A350-900']);
    expect(document.activeElement).toBe(guideButtons[0]);
    fireEvent.keyDown(guideButtons[0], { key: 'ArrowDown' });
    expect(document.activeElement).toBe(guideButtons[1]);
    fireEvent.click(guideButtons[1]);

    expect(screen.queryByRole('dialog', { name: 'Aircraft dossiers' })).toBeNull();
    await waitFor(() => expect((sceneProps.route as Array<{ x: number; z: number }> | null)?.at(-1)).toEqual({ x: -4, z: 9 }));
    expect(selection).not.toHaveBeenCalled();

    press(stage, 'ArrowLeft', 16);
    press(stage, 'ArrowDown', 57);
    press(stage, 'ArrowRight', 16);

    expect(sceneProps.route).toBeNull();
    expect(document.querySelector('[data-hangar-play-arrival="cp-case-b"]')?.textContent).toContain('Arrived at Embraer E195-E2.');
    expect(document.querySelector('[data-hangar-play-live]')?.textContent).toBe('Arrived at Embraer E195-E2.');
    expect(screen.getByRole('button', { name: 'Open Embraer E195-E2 journey' })).toBeTruthy();
    expect(sceneProps.selectedObjectId).toBe('SYN-CP-B-AIR-001');
    expect(selection).toHaveBeenCalledWith('AIR-2');
  });

  it('closes the dossiers with Escape and reopens them with E only inside the rack zone', async () => {
    const stage = await renderPlayableHangar();
    press(stage, 'e');
    expect(screen.queryByRole('dialog', { name: 'Aircraft dossiers' })).toBeNull();
    press(stage, 'ArrowLeft', 19);
    press(stage, 'ArrowUp', 79);
    press(stage, 'ArrowRight', 16);
    fireEvent.keyDown(screen.getByRole('dialog', { name: 'Aircraft dossiers' }), { key: 'Escape' });
    expect(screen.queryByRole('dialog', { name: 'Aircraft dossiers' })).toBeNull();
    expect(document.activeElement).toBe(stage);
    press(stage, 'e');
    expect(screen.getByRole('dialog', { name: 'Aircraft dossiers' })).toBeTruthy();
  });
});
