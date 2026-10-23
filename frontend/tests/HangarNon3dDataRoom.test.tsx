import { createElement, type ComponentType } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarNon3dDataRoom, { HangarNon3dDataRoomController, activateHangarDataRoomCase, scopedInitialEntries } from '../src/hangar/non3dDataRoom';
import { HANGAR_SOURCE_IDS, SOURCE_UNAVAILABLE_TEXT } from '../src/hangar/sourceSummary';
import App, { type HangarEntryAppProps } from '../src/App';
import { signInAsReviewer, stubCaseApi } from './hangarAppHarness';

const entries = [
  { caseId:'cp-case-a', name:'Case A', category:'narrow-body', unavailable:false, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`})) },
  { caseId:'cp-case-b', name:'Case B', category:'regional', unavailable:false, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'stale' as const,coverage:false,modeText:'mode: stale',coverageText:'coverage: false',text:`${sourceId}: mode: stale; coverage: false`})) },
  { caseId:'cp-case-c', name:'Case C', category:'wide-body', unavailable:true, sources:null },
];
const rackTexts = entries.flatMap(entry => entry.unavailable ? [SOURCE_UNAVAILABLE_TEXT] : entry.sources!.map(source => source.text));

afterEach(cleanup);

describe('HangarNon3dDataRoom', () => {
  it('Hangar App non-3D Data Room parity', async () => {
    stubCaseApi();
    const selection = vi.fn();
    render(createElement(App as ComponentType<HangarEntryAppProps>, {
      hangarBrowserSupported: true,
      hangarWebGl2Probe: { webgl2: true },
      hangarLazyLoader: () => { function Scene() { return createElement('canvas', { 'aria-label': '3D hangar scene' }); } return Scene; },
      hangarAircraftSelectionHandler: selection,
    }));
    await signInAsReviewer();
    expect(screen.getByRole('heading', { name: 'Data Room aircraft' })).toBeTruthy();
    expect(screen.getAllByRole('listitem').map(item => item.getAttribute('data-case-id'))).toEqual(['cp-case-a', 'cp-case-b', 'cp-case-c']);
    fireEvent.click(screen.getByRole('button', { name: 'Case A, narrow-body' }));
    expect(selection).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Enter Hangar' }));
    await screen.findByRole('button', { name: 'Refresh Hangar scene' });
    expect(screen.getByRole('region', { name: 'Walk region' })).toBeTruthy();
    expect(document.querySelector('[data-hangar-app-composition="true"]')).not.toBeNull();
    expect(screen.getByRole('button', { name: 'Open data rack' })).toBeTruthy();
  });

  it('HangarNon3dDataRoom parity with rack menu', async () => {
    const reader = vi.fn(async (caseId:string) => ({ contractVersion:1, caseId, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available',coverage:true})) }));
    render(<HangarNon3dDataRoom caseList={[{caseId:'cp-case-a',name:'Case A',category:'narrow-body'},{caseId:'cp-case-b',name:'Case B',category:'regional'},{caseId:'cp-case-c',name:'Case C',category:'wide-body'}]} scope={['cp-case-a','cp-case-b','cp-case-c']} summaryReader={reader}/>);
    expect(await screen.findAllByRole('listitem')).toHaveLength(3);
    expect(screen.getAllByRole('group')).toHaveLength(15);
    expect(screen.getAllByRole('listitem').map(item => item.getAttribute('data-case-id'))).toEqual(['cp-case-a','cp-case-b','cp-case-c']);
    expect(screen.getAllByRole('button').map(button => button.textContent)).toEqual([
      'Case A — narrow-body', 'Case B — regional', 'Case C — wide-body'
    ]);
    const expectedSummaries = Array.from({ length: 3 }, () => HANGAR_SOURCE_IDS.map(sourceId => `${sourceId}: mode: available; coverage: true`)).flat();
    expect(screen.getAllByRole('group').map(group => group.querySelector('dd')?.textContent ?? '')).toEqual(expectedSummaries);
    expect(reader).toHaveBeenCalledTimes(3);

    cleanup();
    render(<HangarNon3dDataRoom entries={[...entries.slice(0,2), { ...entries[2], unavailable:true, sources:null }]} onSelect={vi.fn()}/>);
    expect(screen.getByRole('status',{name:'Case C source status: unavailable'}).textContent).toBe(SOURCE_UNAVAILABLE_TEXT);

    cleanup();
    const staleReader = vi.fn(async (caseId:string) => ({ contractVersion:1, caseId, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'stale',coverage:false})) }));
    render(<HangarNon3dDataRoom caseList={[{caseId:'cp-case-a',name:'Case A',category:'narrow-body'}]} scope={['cp-case-a']} summaryReader={staleReader}/>);
    await screen.findByText('technical: mode: stale; coverage: false');
    for (const sourceId of HANGAR_SOURCE_IDS) {
      expect(screen.getByText(`${sourceId}: mode: stale; coverage: false`).textContent).toBe(`${sourceId}: mode: stale; coverage: false`);
    }

    cleanup();
    const unsupportedReader = vi.fn(async (caseId:string) => ({ contractVersion:2, caseId, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available',coverage:true})) }));
    render(<HangarNon3dDataRoom caseList={[{caseId:'cp-case-a',name:'Case A',category:'narrow-body'}]} scope={['cp-case-a']} summaryReader={unsupportedReader}/>);
    expect((await screen.findByRole('status',{name:'Case A source status: unavailable'})).textContent).toBe(SOURCE_UNAVAILABLE_TEXT);
  });

  it('HangarNon3dDataRoom activates shared selection', async () => {
    const selection=vi.fn(); render(<HangarNon3dDataRoom entries={entries} scope={['cp-case-a','cp-case-b']} onConfirm={selection}/>);
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    fireEvent.click(screen.getByRole('button',{name:'Case A, narrow-body'}));
    expect(selection).toHaveBeenCalledWith('cp-case-a');
    expect(activateHangarDataRoomCase(entries,'cp-case-c',selection)).toBe(false);
    expect(activateHangarDataRoomCase(entries,'DOES-NOT-EXIST',selection)).toBe(false);
    expect(selection).toHaveBeenCalledTimes(1);
    cleanup();
    render(<HangarNon3dDataRoom entries={entries} browserIdentifier='DOES-NOT-EXIST'/>);
    expect(screen.getByRole('alert').textContent).toBe('Unknown or out-of-scope identifier');
    expect(screen.queryByRole('region',{name:/Selection/})).toBeNull();

    cleanup();
    const excludedSelection=vi.fn();
    const availableExcluded={...entries[2],unavailable:false,sources:entries[0].sources};
    render(<HangarNon3dDataRoom entries={[entries[0],availableExcluded]} scope={['cp-case-a']} browserIdentifier='cp-case-c' onConfirm={excludedSelection}/>);
    expect(screen.getByText('Case A — narrow-body')).toBeTruthy();
    expect(screen.queryByRole('button',{name:'Case C, wide-body'})).toBeNull();
    // Attempt every activation the component offers: only the in-scope case reaches the shared handler.
    for (const button of screen.getAllByRole('button')) { fireEvent.click(button); fireEvent.keyDown(button,{key:'Enter'}); }
    expect(excludedSelection.mock.calls.map(([caseId])=>caseId)).toEqual(['cp-case-a','cp-case-a']);
    expect(screen.queryByText('Case C — wide-body')).toBeNull();
    expect(screen.queryByRole('region',{name:/Selection/})).toBeNull();
    // The controller built from the component's own scoping refuses the available excluded case.
    const scopedSelection=vi.fn();
    const scopedController=new HangarNon3dDataRoomController(scopedInitialEntries([entries[0],availableExcluded],['cp-case-a']),scopedSelection);
    expect(scopedController.activate('cp-case-c')).toBe(false);
    expect(scopedController.confirm('cp-case-c')).toBe(false);
    expect(scopedSelection).not.toHaveBeenCalled();
    expect(scopedController.activate('cp-case-a')).toBe(true);
    expect(scopedSelection).toHaveBeenCalledTimes(1);

    cleanup();
    const scopeSelection = vi.fn();
    const rerendered = render(<HangarNon3dDataRoom entries={entries} scope={['cp-case-a','cp-case-b']} onConfirm={scopeSelection}/>);
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    rerendered.rerender(<HangarNon3dDataRoom entries={entries} scope={['cp-case-a']} onConfirm={scopeSelection}/>);
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Case A, narrow-body' }));
    expect(screen.queryByRole('button', { name: /Case B/ })).toBeNull();
    expect(scopeSelection.mock.calls.map(([caseId]) => caseId)).toEqual(['cp-case-a']);

    cleanup();
    const emptyScopeSelection=vi.fn();
    render(<HangarNon3dDataRoom entries={entries} scope={[]} onConfirm={emptyScopeSelection}/>);
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
    fireEvent.click(document.body);
    expect(emptyScopeSelection).not.toHaveBeenCalled();

    cleanup();
    const malformedScopeSelection=vi.fn();
    for (const malformedScope of ['cp-case-a', ['cp-case-a', 42], [''], { caseScopes: ['cp-case-a', 42] }, { caseScopes: 'cp-case-a' }, { caseScopes: [''], unexpected: true }, null]) {
      cleanup();
      render(<HangarNon3dDataRoom entries={entries} scope={malformedScope} onConfirm={malformedScopeSelection}/>);
      expect(screen.queryAllByRole('listitem')).toHaveLength(0);
      expect(new HangarNon3dDataRoomController(scopedInitialEntries(entries, malformedScope), malformedScopeSelection).activate('cp-case-a')).toBe(false);
      expect(malformedScopeSelection).not.toHaveBeenCalled();
    }
  });

  it('HangarNon3dDataRoom keyboard parity', async () => {
    const selection=vi.fn(); render(<HangarNon3dDataRoom entries={entries} onSelect={selection}/>);
    const first=screen.getByRole('button',{name:'Case A, narrow-body'}); first.focus();
    const user=userEvent.setup(); await user.keyboard('{Enter}');
    expect(selection).toHaveBeenCalledWith('cp-case-a');
    const unavailable=screen.getByRole('button',{name:`Case C, wide-body, ${SOURCE_UNAVAILABLE_TEXT}`});
    expect((unavailable as HTMLButtonElement).disabled).toBe(true);
    const controller=new HangarNon3dDataRoomController(entries,selection); controller.activate('cp-case-b'); expect(selection).toHaveBeenCalledWith('cp-case-b');
    void rackTexts;
  });
});
