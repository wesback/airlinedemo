import { createElement, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { confirmHangarRackMenuEntry, type HangarRackSelectionHandler } from './rackMenu';
import { projectHangarSourceSummary, SOURCE_UNAVAILABLE_TEXT, type HangarSourceSummaryEntry, type SourceSummaryReader } from './sourceSummary';

export const HANGAR_NON_3D_DATA_ROOM_INVALID_ENTRY = 'Unknown or out-of-scope identifier';
export type HangarNon3dDataRoomSelectionHandler = HangarRackSelectionHandler;
export type HangarNon3dDataRoomProps = {
  entries?: HangarSourceSummaryEntry[] | null; caseList?: unknown; scope?: unknown;
  summaryReader?: SourceSummaryReader; onSelect?: HangarNon3dDataRoomSelectionHandler;
  onConfirm?: HangarNon3dDataRoomSelectionHandler; onActivate?: HangarNon3dDataRoomSelectionHandler;
  browserIdentifier?: unknown;
};

export class HangarNon3dDataRoomController {
  constructor(private entries: HangarSourceSummaryEntry[] = [], private handler?: HangarNon3dDataRoomSelectionHandler) {}
  setEntries(entries: HangarSourceSummaryEntry[]) { this.entries = entries; }
  getEntries() { return this.entries.map(entry => ({ ...entry, sources: entry.sources?.map(source => ({ ...source })) ?? null })); }
  setSelectionHandler(handler?: HangarNon3dDataRoomSelectionHandler) { this.handler = handler; }
  activate(identifier: unknown): boolean {
    if (typeof identifier !== 'string') return false;
    const entry = this.entries.find(candidate => candidate.caseId === identifier);
    if (!entry || entry.unavailable) return false;
    return confirmHangarRackMenuEntry(this.entries, identifier, this.handler);
  }
  confirm(identifier: unknown): boolean { return this.activate(identifier); }
}

export function activateHangarDataRoomCase(entries: HangarSourceSummaryEntry[], identifier: unknown, handler?: HangarNon3dDataRoomSelectionHandler): boolean {
  return confirmHangarRackMenuEntry(entries, identifier, handler);
}

export function hasValidHangarDataRoomScope(scope: unknown): boolean {
  if (scope === undefined) return true;
  const identifiers = Array.isArray(scope)
    ? scope
    : scope !== null && typeof scope === 'object'
      && Array.isArray((scope as { caseScopes?: unknown }).caseScopes)
      && Object.keys(scope).every(key => key === 'caseScopes')
      ? (scope as { caseScopes: unknown[] }).caseScopes
      : null;
  return identifiers !== null && identifiers.every(identifier => typeof identifier === 'string' && identifier.trim().length > 0);
}

export function scopedInitialEntries(entries: HangarSourceSummaryEntry[] | null | undefined, scope: unknown): HangarSourceSummaryEntry[] {
  if (!entries) return [];
  if (scope === undefined) return entries;

  let identifiers: unknown[];
  if (Array.isArray(scope)) {
    identifiers = scope;
  } else if (scope !== null && typeof scope === 'object' && Array.isArray((scope as { caseScopes?: unknown }).caseScopes)
      && Object.keys(scope).every(key => key === 'caseScopes')) {
    identifiers = (scope as { caseScopes: unknown[] }).caseScopes;
  } else {
    return [];
  }

  if (!identifiers.every(identifier => typeof identifier === 'string' && identifier.trim().length > 0)) return [];
  const scoped = new Set(identifiers as string[]);
  return entries.filter(entry => scoped.has(entry.caseId));
}

export function HangarNon3dDataRoom({ entries: initialEntries, caseList, scope, summaryReader, onSelect, onConfirm, onActivate, browserIdentifier }: HangarNon3dDataRoomProps) {
  const [projectedEntries, setProjectedEntries] = useState<HangarSourceSummaryEntry[]>([]);
  const entries = initialEntries !== undefined
    ? scopedInitialEntries(initialEntries, scope)
    : scopedInitialEntries(projectedEntries, scope);
  const selectionHandler = onConfirm ?? onActivate ?? onSelect;
  const controllerRef = useRef<HangarNon3dDataRoomController | null>(null);
  if (!controllerRef.current) controllerRef.current = new HangarNon3dDataRoomController(entries, selectionHandler);
  const controller = controllerRef.current;
  controller.setEntries(entries);
  controller.setSelectionHandler(selectionHandler);

  useEffect(() => {
    let active = true;
    if (initialEntries !== undefined) {
      setProjectedEntries(scopedInitialEntries(initialEntries, scope));
    } else if (Array.isArray(caseList) || (caseList !== null && typeof caseList === 'object')) {
      void projectHangarSourceSummary(caseList, scope, summaryReader).then(projected => {
        if (active) setProjectedEntries(projected);
      });
    } else {
      setProjectedEntries([]);
    }
    return () => { active = false; };
  }, [initialEntries, caseList, scope, summaryReader]);

  const invalidBrowserIdentifier = typeof browserIdentifier === 'string' && !entries.some(entry => !entry.unavailable && entry.caseId === browserIdentifier);
  return createElement('section', { id: 'non-3d-data-room', 'aria-labelledby': 'non-3d-data-room-heading', 'data-hangar-data-room-non3d': 'true' },
    createElement('h3', { id: 'non-3d-data-room-heading' }, 'Data Room aircraft'),
    invalidBrowserIdentifier ? createElement('p', { role: 'alert' }, HANGAR_NON_3D_DATA_ROOM_INVALID_ENTRY) : null,
    createElement('ul', { 'aria-label': 'Data Room in-scope cases' }, entries.map((entry, index) => {
      const unavailable = entry.unavailable || !entry.sources;
      const accessibleName = `${entry.name}, ${entry.category}${unavailable ? `, ${SOURCE_UNAVAILABLE_TEXT}` : ''}`;
      return createElement('li', { key: entry.caseId, 'data-case-id': entry.caseId, 'data-case-index': String(index) },
        createElement('button', {
          type: 'button', 'aria-label': accessibleName, disabled: unavailable,
          onClick: () => controller.activate(entry.caseId),
          onKeyDown: (event: ReactKeyboardEvent<HTMLButtonElement>) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); controller.activate(entry.caseId); } },
        }, createElement('span', null, `${entry.name} — ${entry.category}`)),
        unavailable
          ? createElement('p', { role: 'status', 'aria-label': `${entry.name} source status: unavailable` }, SOURCE_UNAVAILABLE_TEXT)
          : createElement('dl', { 'aria-label': `${entry.name} five-source summary` }, (entry.sources ?? []).map(source =>
              createElement('div', { key: source.sourceId, role: 'group', 'aria-label': `${source.sourceId} source status` }, createElement('dt', null, source.sourceId), createElement('dd', null, `${source.sourceId}: ${source.modeText}; ${source.coverageText}`)))),
      );
    })));
}

export const createHangarNon3dDataRoomController = (entries: HangarSourceSummaryEntry[] = [], handler?: HangarNon3dDataRoomSelectionHandler) => new HangarNon3dDataRoomController(entries, handler);
export const selectHangarDataRoomCase = activateHangarDataRoomCase;
export const activateHangarNon3dDataRoomEntry = activateHangarDataRoomCase;
export const confirmHangarNon3dDataRoomEntry = activateHangarDataRoomCase;
export const HangarDataRoomNon3dList = HangarNon3dDataRoom;
export const Non3dDataRoom = HangarNon3dDataRoom;
export default HangarNon3dDataRoom;
