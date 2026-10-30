// Importing HangarMode loads neither the entry controller nor @react-three/fiber: the counting mocks below see exactly
// what HangarMode (through the harness) loads, so a static import of either anywhere under it counts before the first
// test runs.
const hangarModuleLoads = vi.hoisted(() => ({ entryController: 0, threeFiber: 0 }));
vi.mock('../src/hangar/entryController', async (importOriginal) => {
  hangarModuleLoads.entryController += 1;
  return await importOriginal();
});
vi.mock('@react-three/fiber', () => {
  hangarModuleLoads.threeFiber += 1;
  return { Canvas: () => null };
});
import { cleanup, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHangarMode, stubCaseApi } from './hangarAppHarness';

const loadsAtStartup = { ...hangarModuleLoads };

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('HangarEntry lazy loading', () => {
  it('HangarEntry lazy loads only after the hangar mounts', async () => {
    // Importing HangarMode loaded no 3D module.
    expect(loadsAtStartup).toEqual({ entryController: 0, threeFiber: 0 });

    const modelRequest = vi.fn();
    stubCaseApi(modelRequest);
    function LoadedScene() { return null; }
    const loader = vi.fn(async () => LoadedScene);
    renderHangarMode({ hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: loader });

    // Mounting the hangar loads the entry controller once and calls the lazy loader once; fiber stays unloaded.
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));
    expect(hangarModuleLoads.entryController).toBe(1);
    expect(hangarModuleLoads.threeFiber).toBe(0);
    expect(modelRequest).not.toHaveBeenCalled();

    // Fallback-only entry never calls the loader.
    cleanup();
    const fallbackLoader = vi.fn();
    renderHangarMode({ hangarBrowserSupported: false, hangarWebGl2Probe: { webgl2: true }, hangarLazyLoader: fallbackLoader });
    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');
    expect(fallbackLoader).not.toHaveBeenCalled();
    expect(hangarModuleLoads.threeFiber).toBe(0);
    expect(modelRequest).not.toHaveBeenCalled();
  });
});
