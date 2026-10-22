import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarRackMenu, { HangarRackMenuController, HANGAR_RACK_MENU_INVALID_ENTRY, HANGAR_RACK_MENU_OPEN_LABEL } from '../src/hangar/rackMenu';
import { HANGAR_SOURCE_IDS } from '../src/hangar/sourceSummary';

const entries=[
 {caseId:'cp-case-a',name:'Case A',category:'narrow-body',unavailable:false,sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`}))},
 {caseId:'cp-case-b',name:'Case B',category:'regional',unavailable:false,sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'stale' as const,coverage:false,modeText:'mode: stale',coverageText:'coverage: false',text:`${sourceId}: mode: stale; coverage: false`}))},
 {caseId:'cp-case-c',name:'Case C',category:'wide-body',unavailable:true,sources:null},
];

afterEach(cleanup);

describe('HangarRackMenu',()=>{
 it('HangarRackMenu zone entry and focus lifecycle',async()=>{
   const focusWalk=vi.fn(); const controller=new HangarRackMenuController(entries,{onFocusWalk:focusWalk});
   controller.enterZone(); expect(controller.getState().open).toBe(true); controller.stepInside(true); controller.stepInside(true);
   expect(controller.getState().open).toBe(true); controller.escape(); expect(controller.getState().open).toBe(false); expect(focusWalk).toHaveBeenCalledTimes(1);
   controller.escape(); expect(focusWalk).toHaveBeenCalledTimes(1); controller.stepInside(true); expect(controller.getState().open).toBe(false);
   controller.leaveZone(); controller.enterZone(); expect(controller.getState().open).toBe(true); controller.explicitOpen(); expect(controller.getState().open).toBe(true);
   controller.close();
 });

 it('HangarRackMenu keyboard navigation contract',async()=>{
   const selection=vi.fn(); const controller=new HangarRackMenuController(entries,{onSelect:selection}); controller.open();
   expect(controller.down()?.caseId).toBe('cp-case-b'); expect(controller.down()?.caseId).toBe('cp-case-c'); expect(controller.down()?.caseId).toBe('cp-case-a');
   expect(controller.up()?.caseId).toBe('cp-case-c'); expect(controller.end()?.caseId).toBe('cp-case-c'); expect(controller.home()?.caseId).toBe('cp-case-a');
   controller.end(); controller.key('Enter'); expect(selection).not.toHaveBeenCalled(); // unavailable entry is not selectable
   controller.key('Escape'); expect(controller.getState().open).toBe(false); expect(selection).not.toHaveBeenCalled();
   const open2=new HangarRackMenuController(entries,{onConfirm:selection}); open2.open(); expect(open2.key('ArrowDown')).toBe(true); expect(open2.key('Enter')).toBe(true); expect(selection).toHaveBeenCalledTimes(1);
   open2.close(); expect(open2.key('Enter')).toBe(false); expect(open2.key('Escape')).toBe(false);
 });

 it('HangarRackMenu confirms scope-listed cases only',()=>{
   const selection=vi.fn(); const controller=new HangarRackMenuController(entries,{onSelect:selection}); controller.open();
   expect(controller.confirm('cp-case-b')).toBe(true); expect(selection).toHaveBeenCalledTimes(1); expect(selection).toHaveBeenCalledWith('cp-case-b'); expect(controller.getState().open).toBe(false);
   controller.open(); expect(controller.confirm('cp-case-c')).toBe(false); expect(controller.confirm('DOES-NOT-EXIST')).toBe(false); expect(controller.getState().error).toBe(HANGAR_RACK_MENU_INVALID_ENTRY); expect(selection).toHaveBeenCalledTimes(1);
 });

 it('HangarRackMenu transport isolation',()=>{
   const transport=vi.fn(); const selection=vi.fn(); const controller=new HangarRackMenuController(entries,{transport,onConfirm:selection});
   controller.open(); controller.down(); controller.escape(); controller.open(); controller.confirmHighlighted();
   expect(transport).not.toHaveBeenCalled(); expect(selection).toHaveBeenCalledTimes(1);
   const reader=vi.fn(async ()=>({contractVersion:1,caseId:'cp-case-a',sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available',coverage:true}))}));
   const transportReader=vi.fn(()=>({response:'no transport'})); void reader; void transportReader;
 });

 it('HangarRackMenu DOM roles names focus and explicit control',async()=>{
   const selection=vi.fn(); render(<><HangarRackMenu entries={entries} onConfirm={selection}/></>);
   const open=screen.getByRole('button',{name:HANGAR_RACK_MENU_OPEN_LABEL}); fireEvent.click(open);
   expect(screen.getByRole('menu',{name:'Data rack menu'})).toBeTruthy();
   const first=screen.getByRole('menuitem',{name:'Case A'}); expect(first.getAttribute('aria-current')).toBe('true'); first.focus();
   const user=userEvent.setup(); await user.keyboard('{ArrowDown}{End}{Home}{Enter}'); expect(selection).toHaveBeenCalledTimes(1); expect(selection).toHaveBeenCalledWith('cp-case-a');
   expect(screen.queryByRole('menu')).toBeNull(); await user.click(open); expect(screen.getByRole('menu')).toBeTruthy();
 });
});
