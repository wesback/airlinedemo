import { createElement, type ComponentType } from 'react';

export const SUPPORTED_HANGAR_CONTRACT_VERSION = 1;
export const SUPPORTED_HANGAR_BROWSERS = ['Chrome', 'Edge'] as const;
export type HangarFallbackReason = 'unsupported_browser' | 'webgl_unavailable' | 'contract_invalid';
export type WebGl2Probe = { webgl2?: boolean; supported?: boolean } | object | null;
export type HangarEntryOptions = {
  browserSupported?: unknown;
  browserEligibility?: unknown;
  browser?: unknown;
  mode?: 'regular' | 'hangar' | '3d';
  userAgent?: string;
  webgl2?: WebGl2Probe;
  webgl2Probe?: WebGl2Probe;
  probe?: WebGl2Probe;
  contractVersion?: unknown;
};
export type HangarEntryDecision = {
  mode: '3d' | 'fallback';
  fallbackReason: HangarFallbackReason | null;
  browserSupported: boolean;
  contractValid: boolean;
  loaderCalled: boolean;
  loadedScene?: unknown;
};

export function isHangarBrowserSupported(browser: unknown, userAgent?: string): boolean {
  if (typeof browser === 'boolean') return browser;
  if (browser && typeof browser === 'object') {
    const candidate = browser as { supported?: unknown; eligible?: unknown; browserSupported?: unknown; eligibleBrowser?: unknown; major?: unknown; version?: unknown; name?: unknown };
    if (Object.keys(candidate).length === 0) return false;
    if ((candidate.name === 'Chrome' || candidate.name === 'Edge') && typeof candidate.version === 'number') return candidate.version >= 120;
    if (typeof candidate.supported === 'boolean') return candidate.supported;
    if (typeof candidate.eligibleBrowser === 'boolean') return candidate.eligibleBrowser;
    if (typeof candidate.name === 'string' && candidate.name !== 'Chrome' && candidate.name !== 'Edge') return false;
    if (typeof candidate.major === 'number') return candidate.major >= 120;
    if (typeof candidate.version === 'string' || typeof candidate.version === 'number') return Number(String(candidate.version).split('.')[0]) >= 120;
    if (typeof candidate.eligible === 'boolean') return candidate.eligible;
    if (typeof candidate.browserSupported === 'boolean') return candidate.browserSupported;
  }
  const ua = typeof browser === 'string' ? browser : userAgent ?? (typeof navigator !== 'undefined' ? navigator.userAgent : '');
  if (/Firefox|Safari|FxiOS|CriOS|EdgiOS/i.test(ua) && !/EdgA?|Chrome|Chromium/i.test(ua)) return false;
  const match = ua.match(/(?:Chrome|Chromium|Edg(?:e|A|iOS)?|Edg)\/([0-9]+)/i);
  if (!match) return false;
  return Number(match[1]) >= 120;
}

export function probeWebGl2(input?: WebGl2Probe | true | false | unknown): WebGl2Probe | null {
  if (input !== undefined) {
    if (input === true) return { webgl2: true };
    if (typeof input !== 'object' || input === null || Array.isArray(input)) return null;
    const probe = input as { webgl2?: unknown; supported?: unknown };
    if ('webgl2' in probe && probe.webgl2 !== true) return null;
    if ('supported' in probe && probe.supported !== true) return null;
    return ('webgl2' in probe || 'supported' in probe) ? input as WebGl2Probe : null;
  }
  if (typeof document === 'undefined') return null;
  try {
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('webgl2');
    return context ? { webgl2: true } : null;
  } catch { return null; }
}

export function getHangarEntryDecision(options: HangarEntryOptions = {}): HangarEntryDecision {
  const browserSupported = options.browserSupported !== undefined
    ? isHangarBrowserSupported(options.browserSupported)
    : options.browserEligibility !== undefined
      ? isHangarBrowserSupported(options.browserEligibility)
      : options.browser !== undefined
        ? isHangarBrowserSupported(options.browser)
        : isHangarBrowserSupported(options.userAgent);
  const probeInput = options.webgl2Probe !== undefined
    ? options.webgl2Probe
    : options.webgl2 !== undefined
      ? options.webgl2
      : options.probe;
  const hasProbeInput = probeInput !== undefined;
  const probe = hasProbeInput ? probeWebGl2(probeInput) : probeWebGl2();
  const contractValid = options.contractVersion === undefined || options.contractVersion === SUPPORTED_HANGAR_CONTRACT_VERSION;
  let fallbackReason: HangarFallbackReason | null = null;
  if (!contractValid) fallbackReason = 'contract_invalid';
  else if (!browserSupported) fallbackReason = 'unsupported_browser';
  else if (probe === null) fallbackReason = 'webgl_unavailable';
  return { mode: fallbackReason ? 'fallback' : '3d', fallbackReason, browserSupported, contractValid, loaderCalled: false };
}

export async function selectHangarEntry<T>(options: HangarEntryOptions = {}, lazyLoader?: () => Promise<T> | T): Promise<HangarEntryDecision> {
  const decision = getHangarEntryDecision(options);
  if (decision.mode !== '3d' || !lazyLoader) return decision;
  const loadedScene = await lazyLoader();
  return { ...decision, loaderCalled: true, loadedScene };
}

export async function loadHangarScene(): Promise<ComponentType> {
  const { Canvas } = await import('@react-three/fiber');
  const HangarScene: ComponentType = () => createElement(
    Canvas,
    { shadows: true, camera: { position: [4, 3, 6], fov: 45 } },
    createElement('color', { attach: 'background', args: ['#dbe7f0'] }),
    createElement('ambientLight', { intensity: 1 }),
    createElement('directionalLight', { position: [5, 8, 4], intensity: 2 }),
    createElement('mesh', { position: [0, 0, 0] },
      createElement('boxGeometry', { args: [3, 1.5, 1.5] }),
      createElement('meshStandardMaterial', { color: '#64748b', roughness: 0.65 }),
    ),
  );
  return HangarScene;
}

export function isNon3dFallbackDecision(decision: HangarEntryDecision): boolean { return decision.mode === 'fallback'; }
export const decideHangarEntry = getHangarEntryDecision;
export const evaluateHangarEntry = getHangarEntryDecision;
export const canEnterHangar3d = (options: HangarEntryOptions = {}) => getHangarEntryDecision(options).mode === '3d';

export const HANGAR_NON_3D_ACTIONS = ['selection', 'status', 'evidence', 'journey', 'review'] as const;
export function getHangarNon3dActions(): readonly string[] { return HANGAR_NON_3D_ACTIONS; }
export const isSupportedBrowser = isHangarBrowserSupported;
export const probeWebGl2Capability = probeWebGl2;
export function checkHangarContractEligibility(contractVersion: unknown): boolean { return contractVersion === undefined || contractVersion === SUPPORTED_HANGAR_CONTRACT_VERSION; }
export function getHangarFallbackReason(options: HangarEntryOptions = {}): HangarFallbackReason | null { return getHangarEntryDecision(options).fallbackReason; }
export const HANGAR_3D_ACTIONS = HANGAR_NON_3D_ACTIONS;
