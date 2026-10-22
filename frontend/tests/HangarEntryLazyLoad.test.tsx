// Regular mode loads no Hangar module. This file imports nothing from src/hangar itself, so the counting mocks below see
// exactly what App loads: a static import of the entry controller or of @react-three/fiber anywhere under App counts
// before the first test runs.
const hangarModuleLoads = vi.hoisted(() => ({ entryController: 0, threeFiber: 0 }));
vi.mock('../src/hangar/entryController', async (importOriginal) => {
  hangarModuleLoads.entryController += 1;
  return await importOriginal();
});
vi.mock('@react-three/fiber', () => {
  hangarModuleLoads.threeFiber += 1;
  return { Canvas: () => null };
});
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createElement, type ComponentType } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App, { type HangarEntryAppProps } from '../src/App';
import { signInAsReviewer, stubCaseApi } from './hangarAppHarness';

const HangarApp = App as ComponentType<HangarEntryAppProps>;
const loadsAtStartup = { ...hangarModuleLoads };

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('HangarEntry lazy loading', () => {
  it('HangarEntry lazy loads only after selection', async () => {
    // Importing App loaded no Hangar module.
    expect(loadsAtStartup).toEqual({ entryController: 0, threeFiber: 0 });

    const modelRequest = vi.fn();
    stubCaseApi(modelRequest);
    function LoadedScene() { return null; }
    const loader = vi.fn(async () => LoadedScene);
    render(createElement(HangarApp, { hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: loader }));
    await signInAsReviewer();

    // Regular mode, signed in, with the Hangar entry on screen: still no Hangar module, loader call or model request.
    expect(screen.getByRole('button', { name: 'Enter Hangar' })).toBeTruthy();
    expect(hangarModuleLoads).toEqual({ entryController: 0, threeFiber: 0 });
    expect(loader).not.toHaveBeenCalled();
    expect(modelRequest).not.toHaveBeenCalled();

    // Selecting the Hangar loads the entry controller once and calls the lazy loader once.
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));
    expect(hangarModuleLoads.entryController).toBe(1);
    expect(hangarModuleLoads.threeFiber).toBe(0);
    expect(modelRequest).not.toHaveBeenCalled();

    // Fallback-only entry never calls the loader.
    cleanup();
    const fallbackLoader = vi.fn();
    render(createElement(HangarApp, { hangarBrowserSupported: false, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: fallbackLoader }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');
    expect(fallbackLoader).not.toHaveBeenCalled();
    expect(hangarModuleLoads.threeFiber).toBe(0);
    expect(modelRequest).not.toHaveBeenCalled();
  });
});
