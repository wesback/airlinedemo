import { createElement, type ComponentType } from 'react';

vi.mock('@react-three/fiber', () => ({ Canvas: () => null }));
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HangarGuideLineController, createHangarGuideLineController } from '../src/hangar/guideLine';
import HangarGuideLineAdapter from '../src/hangar/GuideLineAdapter';
import App, { type HangarEntryAppProps } from '../src/App';
import { signInAsReviewer, stubCaseApi } from './hangarAppHarness';

const floor=[{x:-5,z:-5},{x:5,z:-5},{x:5,z:5},{x:-5,z:5}];
const config={walkablePolygon:floor,aircraftFootprints:[{x:1,z:1,width:2,depth:2}],avatarRadius:0.3,gridSize:0.25,avatar:{x:-4,z:-4}};
afterEach(cleanup);

describe('Hangar guide line',()=>{
  it('Hangar guide line starts and completes once',()=>{
    const selection=vi.fn(); const guide=createHangarGuideLineController(config,selection);
    const route=guide.confirm({x:4,z:4},'AIR-1',{x:-4,z:-4});
    expect(route.status).toBe('route'); expect(route.line).toBe(true); expect(route.route.length).toBeGreaterThan(1);
    const arrival=guide.acceptedStep({x:3.5,z:4});
    expect(arrival.status).toBe('arrival'); expect(arrival.line).toBe(false); expect(arrival.route).toEqual([]);
    expect(arrival.liveText).toBe('Arrival'); expect(selection).toHaveBeenCalledTimes(1); expect(selection).toHaveBeenCalledWith('AIR-1');
    guide.acceptedStep({x:3.5,z:4}); expect(selection).toHaveBeenCalledTimes(1);
    const unreachable=createHangarGuideLineController({...config,aircraftFootprints:[{x:4,z:4,width:3,depth:3}]},selection);
    expect(unreachable.confirm({x:4,z:4},'AIR-X',{x:-4,z:-4})).toMatchObject({status:'unreachable',line:false,route:[],message:'No route: marker is unreachable'});
    expect(selection).toHaveBeenCalledTimes(1);
  });

  it('Hangar guide line live directions and replacement',async()=>{
    const selection=vi.fn(); const closeMenu=vi.fn();
    const rendered=render(<HangarGuideLineAdapter config={config} marker={{x:4,z:-4}} aircraftId="AIR-1" avatar={{x:-4,z:-4}} onSelect={selection} closeMenu={closeMenu}/>);
    expect(closeMenu).toHaveBeenCalledTimes(1);
    const initialLine=rendered.container.querySelector('polyline'); expect(initialLine).not.toBeNull();
    const svg=rendered.container.querySelector('svg'); expect(svg?.getAttribute('viewBox')).toBe('-4.8 -4.8 9.6 2.6');
    const walk=screen.getByRole('region',{name:'Walk region'});
    fireEvent.keyDown(walk,{key:'ArrowRight'});
    expect(rendered.container.querySelector('[data-hangar-guide-live]')?.textContent).toBe('right, 7.50 metres');
    fireEvent.keyDown(walk,{key:'Escape'});
    expect(rendered.container.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    expect(rendered.container.querySelector('[data-hangar-guide-live]')?.textContent).toBe('');
    rendered.rerender(<HangarGuideLineAdapter config={config} marker={{x:-4,z:4}} aircraftId="AIR-2" avatar={{x:-4,z:-4}} onSelect={selection} closeMenu={closeMenu}/>);
    expect(closeMenu).toHaveBeenCalledTimes(2);
    expect(rendered.container.querySelector('polyline')).not.toBeNull();
    rendered.rerender(<HangarGuideLineAdapter config={config} marker={null} aircraftId="AIR-2" avatar={{x:-4,z:-4}} onSelect={selection} closeMenu={closeMenu}/>);
    rendered.rerender(<HangarGuideLineAdapter config={config} marker={{x:-4,z:4}} aircraftId="AIR-2" avatar={{x:-4,z:-4}} onSelect={selection} closeMenu={closeMenu}/>);
    expect(closeMenu).toHaveBeenCalledTimes(3);

    cleanup();
    stubCaseApi();
    render(createElement(App as ComponentType<HangarEntryAppProps>,{
      hangarBrowserSupported:true,
      hangarWebGl2Probe:{webgl2:true},
      hangarAircraftSelectionHandler:selection,
    }));
    await signInAsReviewer();
    fireEvent.click(screen.getByRole('button',{name:'Enter Hangar'}));
    fireEvent.click(await screen.findByRole('button',{name:'Open data rack'}));
    fireEvent.click(screen.getByRole('menuitem',{name:'Air One'}));
    const appWalk=screen.getByRole('region',{name:'Walk region'});
    fireEvent.keyDown(appWalk,{key:'ArrowRight'});
    expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('right, 5.32 metres');
    fireEvent.keyDown(appWalk,{key:'Escape'});
    expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('');
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    fireEvent.click(screen.getByRole('button',{name:'Open data rack'}));
    fireEvent.click(screen.getByRole('menuitem',{name:'Air Two'}));
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
    expect(document.querySelector('polyline')?.getAttribute('points')?.trim().split(/\s+/).at(-1)).toBe('-4,4');
    for(let i=0;i<20;i+=1)fireEvent.keyDown(appWalk,{key:'ArrowLeft'});
    expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('');
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();

    cleanup();
    const guide=new HangarGuideLineController(config);
    const first=guide.confirm({x:4,z:-4},'AIR-1',{x:-4,z:-4});
    expect(first.liveText).toBe('right, 8.00 metres');
    const moved=guide.acceptedStep({x:-3.5,z:-4});
    expect(moved.liveText).toBe('right, 7.50 metres');
    expect(guide.step(false,{x:-3,z:-4}).liveText).toBeNull();
    const second=guide.confirm({x:-4,z:4},'AIR-2',{x:-3.5,z:-4});
    expect(second.status).toBe('route'); expect(second.liveText).not.toBe(first.liveText);
    expect(guide.escape()).toMatchObject({status:'cleared',line:false,liveText:null});
  });

  it('Hangar guide line connects Data Room confirmation to the shared route',()=>{
    const selection=vi.fn(); const closeMenu=vi.fn();
    const entries=[{caseId:'AIR-1',name:'Air One',category:'narrow-body',unavailable:false,sources:null}];
    const rendered=render(<HangarGuideLineAdapter config={config} entries={entries} markers={{'AIR-1':{x:4,z:-4}}} avatar={{x:-4,z:-4}} onSelect={selection} closeMenu={closeMenu}/>);
    fireEvent.click(screen.getByRole('button',{name:'Open data rack'}));
    fireEvent.click(screen.getByRole('menuitem',{name:'Air One'}));
    expect(closeMenu).toHaveBeenCalledTimes(1);
    expect(rendered.container.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
    const routePoints=rendered.container.querySelector('polyline')?.getAttribute('points')?.trim().split(/\s+/)??[];
    expect(routePoints.at(-1)).toBe('4,-4');
    expect(selection).toHaveBeenCalledTimes(0);
  });

  it('Hangar guide line reduced motion and transport isolation',async()=>{
    const transport=vi.fn(); const selection=vi.fn();
    const {container}=render(<HangarGuideLineAdapter config={config} marker={{x:4,z:4}} avatar={{x:-4,z:-4}} aircraftId="AIR-1" onSelect={selection} reducedMotion transport={transport}/>);
    expect(container.querySelector('[data-static-line="true"]')).toBeTruthy();
    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
    const walk=screen.getByRole('region',{name:'Walk region'}); fireEvent.keyDown(walk,{key:'ArrowRight'});
    expect(container.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
    expect(container.querySelector('[data-hangar-static-guide]')).not.toBeNull();
    expect(container.querySelector('polyline')).not.toBeNull();
    fireEvent.keyDown(walk,{key:'Escape'});
    expect(screen.queryByRole('alert')).toBeNull(); expect(container.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    expect(container.querySelector('[data-hangar-static-guide]')).toBeNull();
    expect(container.querySelector('polyline')).toBeNull();
    expect(transport).not.toHaveBeenCalled(); expect(selection).not.toHaveBeenCalled();
    expect(container.querySelector('[role="status"],[role="button"],[aria-current],[aria-readonly],[aria-disabled]')).toBeNull();

    cleanup();
    stubCaseApi();
    const productionSelection=vi.fn();
    render(createElement(App as ComponentType<HangarEntryAppProps>,{
      hangarBrowserSupported:true,hangarWebGl2Probe:{webgl2:true},
      hangarAircraftSelectionHandler:productionSelection,
    }));
    await signInAsReviewer();
    const requestsBeforeEnter=vi.mocked(fetch).mock.calls.length;
    fireEvent.click(screen.getByRole('button',{name:'Enter Hangar'}));
    const requestsAfterEnter=vi.mocked(fetch).mock.calls.length;
    expect(requestsAfterEnter).toBe(requestsBeforeEnter);
    const requestsBeforeRack=requestsAfterEnter;
    fireEvent.click(await screen.findByRole('button',{name:'Open data rack'}));
    fireEvent.click(screen.getByRole('menuitem',{name:'Air One'}));
    expect(vi.mocked(fetch).mock.calls.length).toBe(requestsBeforeRack);
    const requestsBeforeReduced=vi.mocked(fetch).mock.calls.length;
    fireEvent.click(screen.getByRole('button',{name:'Enable reduced motion'}));
    expect(vi.mocked(fetch).mock.calls.length).toBe(requestsBeforeReduced);
    expect(document.querySelector('[data-hangar-guide-line-visible="true"]')).not.toBeNull();
    expect(document.querySelector('[data-static-line="true"]')).not.toBeNull();
    const fetchBeforeGuide=vi.mocked(fetch).mock.calls.length;
    fireEvent.click(screen.getByRole('button',{name:'Pointer select AIR-1'}));
    expect(vi.mocked(fetch).mock.calls.length).toBe(fetchBeforeGuide);
    expect(productionSelection).toHaveBeenCalledTimes(1);
    expect(productionSelection).toHaveBeenCalledWith('AIR-1');
    fireEvent.keyDown(screen.getByRole('button',{name:'Keyboard select AIR-2'}),{key:'Enter'});
    expect(vi.mocked(fetch).mock.calls.length).toBe(fetchBeforeGuide);
    expect(productionSelection).toHaveBeenCalledTimes(2);
    expect(productionSelection).toHaveBeenLastCalledWith('AIR-2');
    const requestsBeforeWalk=vi.mocked(fetch).mock.calls.length;
    const productionWalk=screen.getByRole('region',{name:'Walk region'});
    fireEvent.keyDown(productionWalk,{key:'ArrowRight'});
    expect(vi.mocked(fetch).mock.calls.length).toBe(requestsBeforeWalk);
    fireEvent.keyDown(productionWalk,{key:'Escape'});
    expect(vi.mocked(fetch).mock.calls.length).toBe(requestsBeforeWalk);
    expect(document.querySelector('[data-hangar-guide-live]')?.textContent).toBe('');
  });
});
