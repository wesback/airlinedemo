import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { HangarGuideLineController, createHangarGuideLineController } from '../src/hangar/guideLine';
import HangarGuideLineAdapter from '../src/hangar/GuideLineAdapter';

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

  it('Hangar guide line live directions and replacement',()=>{
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
    expect(selection).toHaveBeenCalledTimes(0);
  });

  it('Hangar guide line reduced motion and transport isolation',()=>{
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
  });
});
