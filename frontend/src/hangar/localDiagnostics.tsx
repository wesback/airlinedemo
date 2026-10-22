import { createElement, useState, type ReactNode } from 'react';

export type HangarLocalFallbackReason =
  | 'unsupported_browser' | 'webgl_unavailable' | 'asset_invalid'
  | 'asset_load_failed' | 'render_failed' | 'refresh_failed';
export type HangarLocalDiagnosticsData = {
  sceneLoadDurationMs: number;
  webgl2Available: boolean;
  browserSupported: boolean;
  fallbackReason: HangarLocalFallbackReason | null;
};

export const HANGAR_LOCAL_FALLBACK_CODES: readonly HangarLocalFallbackReason[] =
  ['unsupported_browser', 'webgl_unavailable', 'asset_invalid', 'asset_load_failed', 'render_failed', 'refresh_failed'];

const objectOf = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
export function isHangarPresenter(input: unknown): boolean {
  const role = typeof input === 'string' ? input : objectOf(input) ? input.role : undefined;
  return typeof role === 'string' && role.trim().toLowerCase() === 'presenter';
}
export function isHangarLocalFallbackReason(value: unknown): value is HangarLocalFallbackReason {
  return typeof value === 'string' && (HANGAR_LOCAL_FALLBACK_CODES as readonly string[]).includes(value);
}
export function buildHangarLocalDiagnostics(input: {
  presenter?: unknown; isPresenter?: unknown; role?: unknown; session?: unknown;
  sceneLoadDurationMs?: unknown; clockStart?: number; clockEnd?: number; now?: () => number;
  webgl2Available?: unknown; browserSupported?: unknown; fallbackReason?: unknown; transport?: unknown; outcome?: unknown;
} = {}): HangarLocalDiagnosticsData | null {
  const presenter = input.isPresenter !== undefined ? input.isPresenter === true : input.presenter !== undefined ? input.presenter === true : input.session !== undefined ? isHangarPresenter(input.session) : isHangarPresenter(input.role);
  if (!presenter) return null;
  const fallback = input.fallbackReason ?? null;
  if (fallback !== null && !isHangarLocalFallbackReason(fallback)) return null;
  if (typeof input.webgl2Available !== 'boolean' || typeof input.browserSupported !== 'boolean') return null;
  let duration = typeof input.sceneLoadDurationMs === 'number' && Number.isFinite(input.sceneLoadDurationMs) && input.sceneLoadDurationMs >= 0
    ? input.sceneLoadDurationMs : 0;
  if (typeof input.clockStart === 'number' && typeof input.clockEnd === 'number') duration = Math.max(0, input.clockEnd - input.clockStart);
  else if (typeof input.now === 'function' && typeof input.clockStart === 'number') duration = Math.max(0, input.now() - input.clockStart);
  return { sceneLoadDurationMs: duration, webgl2Available: input.webgl2Available, browserSupported: input.browserSupported, fallbackReason: fallback as HangarLocalFallbackReason | null };
}
export const createHangarLocalDiagnostics = buildHangarLocalDiagnostics;
export const getHangarLocalDiagnostics = buildHangarLocalDiagnostics;

export type HangarLocalDiagnosticsProps = {
  presenter?: unknown; isPresenter?: unknown; role?: unknown; session?: unknown; open?: boolean;
  sceneLoadDurationMs?: unknown; clockStart?: number; clockEnd?: number; now?: () => number;
  webgl2Available?: unknown; browserSupported?: unknown; fallbackReason?: unknown;
  transport?: unknown; outcome?: unknown; children?: ReactNode;
};
export function HangarLocalDiagnostics(props: HangarLocalDiagnosticsProps) {
  const [opened, setOpened] = useState(props.open === true);
  const presenter = props.isPresenter !== undefined ? props.isPresenter === true : props.presenter !== undefined ? props.presenter === true : props.session !== undefined ? isHangarPresenter(props.session) : isHangarPresenter(props.role);
  if (!presenter) return null;
  const fallback = props.fallbackReason ?? null;
  const data = buildHangarLocalDiagnostics({ ...props, fallbackReason: fallback });
  if (!data) return null;
  return createElement('div', { 'aria-label': 'Hangar local diagnostics' },
    props.open === undefined ? createElement('button', { type: 'button', 'aria-label': 'Open local diagnostics', onClick: () => setOpened(true) }, 'Open local diagnostics') : null,
    opened ? createElement('section', { role: 'region', 'aria-label': 'Local diagnostics panel', 'data-hangar-local-diagnostics': 'true' },
      createElement('dl', null,
        createElement('dt', null, 'sceneLoadDurationMs'), createElement('dd', null, String(data.sceneLoadDurationMs)),
        createElement('dt', null, 'webgl2Available'), createElement('dd', null, String(data.webgl2Available)),
        createElement('dt', null, 'browserSupported'), createElement('dd', null, String(data.browserSupported)),
        createElement('dt', null, 'fallbackReason'), createElement('dd', null, data.fallbackReason === null ? 'null' : data.fallbackReason),
      ),
    ) : null,
  );
}
export default HangarLocalDiagnostics;
