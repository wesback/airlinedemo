import { createElement } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarRackMenu, { HangarRackMenuController, HANGAR_RACK_MENU_INVALID_ENTRY, HANGAR_RACK_MENU_OPEN_LABEL } from '../src/hangar/rackMenu';
import sceneManifest from '../src/hangar/sceneManifest.json';
import { HANGAR_MANIFEST_ERROR } from '../src/hangar/dataRoomAdapter';
import { HANGAR_SOURCE_IDS } from '../src/hangar/sourceSummary';
import { renderHangarMode, stubCaseApi } from './hangarAppHarness';

const entries=[
 {caseId:'cp-case-a',name:'Case A',category:'narrow-body',unavailable:false,sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'available' as const,coverage:true,modeText:'mode: available',coverageText:'coverage: true',text:`${sourceId}: mode: available; coverage: true`}))},
 {caseId:'cp-case-b',name:'Case B',category:'regional',unavailable:false,sources:HANGAR_SOURCE_IDS.map(sourceId=>({sourceId,mode:'stale' as const,coverage:false,modeText:'mode: stale',coverageText:'coverage: false',text:`${sourceId}: mode: stale; coverage: false`}))},
 {caseId:'cp-case-c',name:'Case C',category:'wide-body',unavailable:true,sources:null},
];

afterEach(cleanup);

describe('HangarRackMenu',()=>{
 it('Hangar App rack menu guide route composition', async () => {
   stubCaseApi();
   const selection = vi.fn();
   renderHangarMode({
     hangarBrowserSupported: true, hangarWebGl2Probe: { webgl2: true },
     hangarLazyLoader: () => { function Scene() { return createElement('canvas', { 'aria-label': '3D hangar scene' }); } return Scene; },
     hangarAircraftSelectionHandler: selection,
   });
   fireEvent.click(await screen.findByRole('button', { name: HANGAR_RACK_MENU_OPEN_LABEL }));
   expect(screen.getAllByRole('menuitem').map(item => item.getAttribute('aria-label'))).toEqual(['Air One', 'Air Two', 'Air Three']);
   fireEvent.click(screen.getByRole('menuitem', { name: 'Air One' }));
   expect(screen.queryByRole('menu', { name: 'Data rack menu' })).toBeNull();
   expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
   const walk = screen.getByRole('region', { name: 'Walk region' });
   for (let index = 0; index < 8; index += 1) fireEvent.keyDown(walk, { key: 'ArrowRight' });
   for (let index = 0; index < 8; index += 1) fireEvent.keyDown(walk, { key: 'ArrowUp' });
   await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());
   expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('Arrival');
   expect(selection).toHaveBeenCalledTimes(1);
   expect(selection).toHaveBeenCalledWith('AIR-1');
   fireEvent.keyDown(walk, { key: 'ArrowDown' });
   expect(selection).toHaveBeenCalledTimes(1);
 });

 it('HangarRackMenu zone entry and focus lifecycle',async()=>{
   const selection=vi.fn(); render(<HangarRackMenu entries={entries} onConfirm={selection}/>);
   const walk=screen.getByRole('region',{name:'Walk region'});
   fireEvent.focus(walk);
   expect(screen.getByRole('menu',{name:'Data rack menu'})).toBeTruthy();
   expect(document.activeElement).toBe(screen.getByRole('menu',{name:'Data rack menu'}));
   fireEvent.focus(walk);
   expect(screen.getAllByRole('menuitem')).toHaveLength(3);
   fireEvent.keyDown(screen.getByRole('menu',{name:'Data rack menu'}),{key:'Escape'});
   expect(screen.queryByRole('menu')).toBeNull();
   expect(document.activeElement).toBe(walk);
   fireEvent.mouseLeave(walk);
   fireEvent.mouseEnter(walk);
   expect(screen.getByRole('menu',{name:'Data rack menu'})).toBeTruthy();
   fireEvent.keyDown(screen.getByRole('menu',{name:'Data rack menu'}),{key:'Escape'});
   const open=screen.getByRole('button',{name:HANGAR_RACK_MENU_OPEN_LABEL});
   fireEvent.click(open);
   expect(screen.getByRole('menu',{name:'Data rack menu'})).toBeTruthy();

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

 it('HangarRackMenu exposes invalid manifest and stays closed',()=>{
   const invalid=JSON.parse(JSON.stringify(sceneManifest)); invalid.rack.units[1].source='technical';
   render(<HangarRackMenu entries={entries} manifest={invalid}/>);
   fireEvent.click(screen.getByRole('button',{name:HANGAR_RACK_MENU_OPEN_LABEL}));
   expect(screen.queryByRole('menu',{name:'Data rack menu'})).toBeNull();
   expect(screen.getByRole('alert').textContent).toBe(HANGAR_MANIFEST_ERROR);
 });

 it('HangarRackMenu confirmation closes menu and invokes close callback once',()=>{
   const selection=vi.fn(); const closeMenu=vi.fn();
   render(<HangarRackMenu entries={entries} onConfirm={selection} closeMenu={closeMenu}/>);
   fireEvent.click(screen.getByRole('button',{name:HANGAR_RACK_MENU_OPEN_LABEL}));
   fireEvent.click(screen.getByRole('menuitem',{name:'Case A'}));
   expect(screen.queryByRole('menu',{name:'Data rack menu'})).toBeNull();
   expect(selection).toHaveBeenCalledTimes(1);
   expect(selection).toHaveBeenCalledWith('cp-case-a');
   expect(closeMenu).toHaveBeenCalledTimes(1);
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
