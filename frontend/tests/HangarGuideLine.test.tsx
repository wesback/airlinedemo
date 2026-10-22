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
    const guide=new HangarGuideLineController(config);
    const first=guide.confirm({x:4,z:-4},'AIR-1',{x:-4,z:-4});
    expect(first.liveText).toBe('right, 8.00 metres');
    const moved=guide.acceptedStep({x:-3.5,z:-4});
    expect(moved.liveText).toBe('right, 7.50 metres');
    expect(guide.step(false,{x:-3,z:-4}).liveText).toBe('right, 7.50 metres');
    const second=guide.confirm({x:-4,z:4},'AIR-2',{x:-3.5,z:-4});
    expect(second.status).toBe('route'); expect(second.liveText).not.toBe(first.liveText);
    expect(guide.escape()).toMatchObject({status:'cleared',line:false,liveText:null});
  });

  it('Hangar guide line reduced motion and transport isolation',()=>{
    const transport=vi.fn(); const selection=vi.fn();
    const {container}=render(<HangarGuideLineAdapter config={config} marker={{x:4,z:4}} avatar={{x:-4,z:-4}} aircraftId="AIR-1" onSelect={selection} reducedMotion transport={transport}/>);
    expect(container.querySelector('[data-static-line="true"]')).toBeTruthy();
    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
    const walk=screen.getByRole('region',{name:'Walk region'}); fireEvent.keyDown(walk,{key:'ArrowRight'});
    fireEvent.keyDown(walk,{key:'Escape'});
    expect(screen.queryByRole('alert')).toBeNull(); expect(container.querySelector('[data-hangar-guide-line-visible="true"]')).toBeNull();
    expect(transport).not.toHaveBeenCalled(); expect(selection).not.toHaveBeenCalled();
    expect(container.querySelector('[role="status"],[role="button"],[aria-current],[aria-readonly],[aria-disabled]')).toBeNull();
  });
});
