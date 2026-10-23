import { createElement, type ComponentType } from 'react';

vi.mock('@react-three/fiber', () => ({ Canvas: () => null }));
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import App, { type HangarEntryAppProps } from '../src/App';
import { signInAsReviewer, stubCaseApi } from './hangarAppHarness';
import { confirmHangarRackMenuEntry } from '../src/hangar/rackMenu';
import userEvent from '@testing-library/user-event';
import {
  HANGAR_3D_ACTIONS,
  HANGAR_NON_3D_ACTIONS,
  getHangarEntryDecision,
  getHangarFallbackReason,
  getHangarNon3dActions,
  selectHangarEntry,
  loadHangarScene,
} from '../src/hangar/entryController';

const HangarApp = App as ComponentType<HangarEntryAppProps>;

describe('HangarEntry', () => {
  it('HangarEntry rejects unsupported browser and missing WebGL', () => {
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: false, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: true, webgl2: null }).fallbackReason).toBe('webgl_unavailable');
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: false, webgl2: null })).toMatchObject({ mode: 'fallback', fallbackReason: 'unsupported_browser' });
    expect(getHangarEntryDecision({ contractVersion: 1, browserSupported: true, webgl2: { webgl2: true }, webgl2Probe: null }).fallbackReason).toBe('webgl_unavailable');
  });

  it('HangarEntry contract eligibility cases', async () => {
    const loader = vi.fn();
    await expect(selectHangarEntry({ browserSupported: true, contractVersion: 1, webgl2: { webgl2: true } }, loader)).resolves.toMatchObject({ mode: '3d', loaderCalled: true });
    expect(loader).toHaveBeenCalledTimes(1);

    const unsupported = await selectHangarEntry({ browserSupported: false, contractVersion: 1, webgl2: { webgl2: true } }, loader);
    expect(unsupported).toMatchObject({ fallbackReason: 'unsupported_browser', mode: 'fallback' });

    const contractCases: Array<{ contractVersion: unknown; browserEligibility: unknown; probe: unknown }> = [
      { contractVersion: null, browserEligibility: true, probe: { webgl2: true } },
      { contractVersion: 2, browserEligibility: true, probe: { webgl2: true } },
      { contractVersion: 1, browserEligibility: false, probe: { webgl2: true } },
      { contractVersion: 1, browserEligibility: true, probe: null },
    ];
    for (const entry of contractCases) {
      const calls = vi.fn();
      const decision = await selectHangarEntry({ contractVersion: entry.contractVersion, browserSupported: entry.browserEligibility, webgl2: entry.probe as never }, calls);
      expect(decision.mode).toBe('fallback');
      expect(calls).not.toHaveBeenCalled();
    }
  });

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

  it('HangarEntry loads scene envelopes in exact case-list order', async () => {
    stubCaseApi();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function Scene() { return null; } return Scene; },
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    const requests = vi.mocked(fetch).mock.calls.map(([input, init]) => `${init?.method ?? 'GET'} ${String(input)}`);
    expect(requests.filter((request) => request.includes('/api/v1/cases'))).toEqual([
      'GET /api/v1/cases',
      'GET /api/v1/cases/cp-case-a/scene',
      'GET /api/v1/cases/cp-case-b/scene',
      'GET /api/v1/cases/cp-case-c/scene',
    ]);
    fireEvent.click(await screen.findByRole('button', { name: 'Open data rack' }));
    expect(screen.getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'))).toEqual(['Air One', 'Air Two', 'Air Three']);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh Hangar scene' }));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/scene')).length).toBe(6));
  });

  it('HangarEntry default loader returns a renderable scene component', async () => {
    const Scene = await loadHangarScene();
    expect(typeof Scene).toBe('function');
  });

  it('HangarEntry production Data Room confirmation starts mapped guide route', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function ProductionScene() { return null; } return ProductionScene; },
      hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Open data rack' }));
    // App offers only its Data Room entries: an unmapped identifier such as AIR-X has no menu item to confirm.
    expect(screen.getAllByRole('menuitem').map((item) => item.getAttribute('aria-label'))).toEqual(['Air One', 'Air Two', 'Air Three']);
    expect(screen.queryByRole('menuitem', { name: /AIR-X/ })).toBeNull();
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Air One' }));
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
    const routePoints = document.querySelector('polyline')?.getAttribute('points')?.trim().split(/\s+/) ?? [];
    expect(routePoints.at(-1)).toBe('4,-4');
    expect(screen.queryByRole('menu', { name: 'Data rack menu' })).toBeNull();
    expect(selection).not.toHaveBeenCalled();

    const unknownConfirmed = confirmHangarRackMenuEntry(hangarDataRoomEntriesForTest(), 'AIR-X', selection);
    expect(unknownConfirmed).toBe(false);
    expect(selection).not.toHaveBeenCalled();
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();

    const walk = screen.getByRole('region', { name: 'Walk region' });
    fireEvent.keyDown(walk, { key: 'Escape' });
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open data rack' }));
    expect(confirmHangarRackMenuEntry(hangarDataRoomEntriesForTest(), 'AIR-X', selection)).toBe(false);
    expect(selection).not.toHaveBeenCalled();
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
  });

  function hangarDataRoomEntriesForTest() {
    return [{ caseId: 'AIR-1', name: 'Air One', category: 'narrow-body', unavailable: false, sources: null }];
  }

  it('HangarEntry production guide arrival uses shared aircraft selection once', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Pointer select AIR-1' }));
    expect(selection).toHaveBeenCalledTimes(1);
    expect(selection).toHaveBeenLastCalledWith('AIR-1');
    // A real keyboard press (keydown, then the button's own click): exactly one selection.
    screen.getByRole('button', { name: 'Keyboard select AIR-2' }).focus();
    await userEvent.setup().keyboard('{Enter}');
    expect(selection).toHaveBeenCalledTimes(2);
    expect(selection).toHaveBeenLastCalledWith('AIR-2');
    selection.mockClear();
    fireEvent.click(await screen.findByRole('button', { name: 'Open data rack' }));
    fireEvent.click(screen.getByRole('menuitem', { name: 'Air One' }));
    const walk = screen.getByRole('region', { name: 'Walk region' });
    for (let index = 0; index < 8; index += 1) fireEvent.keyDown(walk, { key: 'ArrowRight' });
    for (let index = 0; index < 8; index += 1) fireEvent.keyDown(walk, { key: 'ArrowUp' });
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('Arrival');
    expect(selection).toHaveBeenCalledTimes(1);
    expect(selection).toHaveBeenCalledWith('AIR-1');
    fireEvent.keyDown(walk, { key: 'ArrowDown' });
    expect(selection).toHaveBeenCalledTimes(1);
  });

  it('HangarEntry non-3D parity', async () => {
    const requiredActions = ['selection', 'status', 'evidence', 'journey', 'review'];
    expect(HANGAR_NON_3D_ACTIONS).toEqual(requiredActions);
    expect(HANGAR_3D_ACTIONS).toEqual(requiredActions);
    expect(getHangarNon3dActions()).toEqual(requiredActions);
    expect(getHangarFallbackReason({ contractVersion: 1, browserSupported: false, webgl2: { webgl2: true } })).toBe('unsupported_browser');

    stubCaseApi();
    render(createElement(HangarApp, {
      hangarBrowserSupported: false,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: vi.fn(),
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');

    const renderedLabels = ['Select case', 'View status', 'View evidence', 'Follow journey', 'Open review'];
    expect(renderedLabels).toEqual(['Select case', 'View status', 'View evidence', 'Follow journey', 'Open review']);
    for (const label of renderedLabels) expect(screen.getByRole('button', { name: label })).toBeTruthy();
    expect(screen.getAllByRole('button').filter((button) => renderedLabels.includes(button.textContent?.trim() ?? ''))).toHaveLength(5);
  });

  it('hangar UI rejects fallback entry before mounting a canvas', async () => {
    stubCaseApi();

    render(createElement(HangarApp, {
      hangarBrowserSupported: false,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: vi.fn(),
    } as HangarEntryAppProps));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));

    await screen.findByText('Your browser is not supported. Use the current or previous major version of Chrome or Edge.');
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    const names = ['Select case', 'View status', 'View evidence', 'Follow journey', 'Open review'];
    for (const name of names) expect(screen.getByRole('button', { name })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Select case' }));
    expect(await screen.findByText('Case selected.')).toBeTruthy();
    for (const [name, message] of [
      ['View status', 'Case status selected.'],
      ['View evidence', 'Case evidence selected.'],
      ['Follow journey', 'Case journey selected.'],
      ['Open review', 'Technical review selected.'],
    ]) {
      fireEvent.click(screen.getByRole('button', { name }));
      expect(await screen.findByText(message)).toBeTruthy();
    }
    fireEvent.click(screen.getByRole('button', { name: 'Open review' }));
    expect(document.activeElement?.id).toBe('cap-technical-review');

    cleanup();
    const nullLoader = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: null,
      hangarLazyLoader: nullLoader,
    } as HangarEntryAppProps));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText('WebGL 2 is unavailable. Enable hardware acceleration or use a WebGL 2-capable browser.');
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(nullLoader).not.toHaveBeenCalled();
  });

  it('supported versions load once and contract fallback renders no canvas with a non-3D link', async () => {
    stubCaseApi();
    const loader = vi.fn();
    render(createElement(HangarApp, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: loader,
    } as HangarEntryAppProps));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));
    expect(document.querySelectorAll('canvas')).toHaveLength(1);

    cleanup();
    render(createElement(HangarApp, {
      hangarContractVersion: 2,
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: loader,
    } as HangarEntryAppProps));
    // A fresh mount verifies contract rejection with the same production entry path.
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByText(/Unsupported hangar contract version 2/);
    expect(document.querySelectorAll('canvas')).toHaveLength(0);
    expect(loader).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('link', { name: 'Use the supported contract version in the non-3D list.' }).getAttribute('href')).toBe('#non-3d-list');
  });

  it('supported versions pass eligibility and unsupported versions reject', () => {
    for (const version of [120, 121, 139, 140]) {
      expect(getHangarEntryDecision({ contractVersion: 1, browser: { name: 'Chrome', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
      expect(getHangarEntryDecision({ contractVersion: 1, browser: { name: 'Edge', version }, webgl2: { webgl2: true } }).mode).toBe('3d');
    }
    expect(getHangarEntryDecision({ contractVersion: 1, browser: { name: 'Firefox', version: 140 }, webgl2: { webgl2: true } }).fallbackReason).toBe('unsupported_browser');
  });
});
