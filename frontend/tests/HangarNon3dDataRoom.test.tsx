import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarNon3dDataRoom, { HangarNon3dDataRoomController, activateHangarDataRoomCase } from '../src/hangar/non3dDataRoom';
import { HANGAR_SOURCE_IDS, SOURCE_UNAVAILABLE_TEXT } from '../src/hangar/sourceSummary';

const entries = [
  { caseId:'cp-case-a', name:'Case A', category:'narrow-body', unavailable:false, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`})) },
  { caseId:'cp-case-b', name:'Case B', category:'regional', unavailable:false, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'stale' as const,coverage:false,modeText:'mode: stale',coverageText:'coverage: false',text:`${sourceId}: mode: stale; coverage: false`})) },
  { caseId:'cp-case-c', name:'Case C', category:'wide-body', unavailable:true, sources:null },
];
const rackTexts = entries.flatMap(entry => entry.unavailable ? [SOURCE_UNAVAILABLE_TEXT] : entry.sources!.map(source => source.text));

afterEach(cleanup);

describe('HangarNon3dDataRoom', () => {
  it('HangarNon3dDataRoom parity with rack menu', async () => {
    const reader = vi.fn(async (caseId:string) => ({ contractVersion:1, caseId, sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available',coverage:true})) }));
    render(<HangarNon3dDataRoom caseList={[{caseId:'cp-case-a',name:'Case A',category:'narrow-body'},{caseId:'cp-case-b',name:'Case B',category:'regional'},{caseId:'cp-case-c',name:'Case C',category:'wide-body'}]} scope={['cp-case-a','cp-case-b','cp-case-c']} summaryReader={reader}/>);
    expect(await screen.findAllByRole('listitem')).toHaveLength(3);
    expect(screen.getAllByRole('group')).toHaveLength(15);
    expect(screen.getByText('Case A — narrow-body')).toBeTruthy();
    expect(screen.getByText('Case B — regional')).toBeTruthy();
    expect(screen.getByText('Case C — wide-body')).toBeTruthy();
    for (const text of ['technical: mode: available; coverage: true','maintenance: mode: available; coverage: true']) expect(screen.getAllByText(text).length).toBeGreaterThan(0);
    expect(reader).toHaveBeenCalledTimes(3);

    cleanup();
    render(<HangarNon3dDataRoom entries={[...entries.slice(0,2), { ...entries[2], unavailable:true, sources:null }]} onSelect={vi.fn()}/>);
    expect(screen.getByRole('status',{name:'Case C source status: unavailable'}).textContent).toBe(SOURCE_UNAVAILABLE_TEXT);

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
    const emptyScopeSelection=vi.fn();
    render(<HangarNon3dDataRoom entries={entries} scope={[]} onConfirm={emptyScopeSelection}/>);
    expect(screen.queryAllByRole('listitem')).toHaveLength(0);
    fireEvent.click(document.body);
    expect(emptyScopeSelection).not.toHaveBeenCalled();
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
