import { describe, expect, it, vi } from 'vitest';
import {
  HangarRoutePlanner,
  HANGAR_ROUTE_GRID_SIZE,
  getHangarRouteLiveDirection,
  planHangarRoute,
} from '../src/hangar/routePlanner';

const floor=[{x:-5,z:-5},{x:5,z:-5},{x:5,z:5},{x:-5,z:5}];
const footprints=[{x:1,z:1,width:2,depth:2},{x:-3,z:-3,width:1,depth:1}];
const config={walkablePolygon:floor,aircraftFootprints:footprints,avatarRadius:0.3,gridSize:0.25};

describe('HangarRoutePlanner',()=>{
  it('HangarRoutePlanner shortest grid route contract',()=>{
    const avatar={x:-4,z:-4},marker={x:4,z:4};
    const contractConfig={...config,aircraftFootprints:[{x:-1,z:2,width:1,depth:1}]};
    const route=planHangarRoute(avatar,marker,contractConfig);
    expect(route.status).toBe('route');expect(route.route.length).toBeGreaterThan(1);
    expect(route.distance).toBeGreaterThanOrEqual(distance(avatar,marker)-1e-9);
    expect(route.distance).toBeLessThanOrEqual(distance(avatar,marker)+HANGAR_ROUTE_GRID_SIZE);
    for(const point of route.route)expect(pointInExpanded(point,contractConfig.aircraftFootprints,0.3)).toBe(false);
    for(let i=1;i<route.route.length;i++){
      const a=route.route[i-1],b=route.route[i];
      for(let t=0;t<=1;t+=0.05)expect(pointInExpanded({x:a.x+(b.x-a.x)*t,z:a.z+(b.z-a.z)*t},contractConfig.aircraftFootprints,0.3)).toBe(false);
    }
    const unreachable=planHangarRoute({x:0,z:0},{x:4,z:4},{...config,aircraftFootprints:[{x:4,z:4,width:3,depth:3}]});
    expect(unreachable).toMatchObject({status:'unreachable',reachable:false,route:[],points:[],line:false});expect(unreachable.message).toContain('unreachable');
    expect(planHangarRoute(undefined,{x:0,z:0},config)).toMatchObject({status:'unreachable',route:[],line:false,message:'No route: marker is missing'});

    const selection=vi.fn();const planner=new HangarRoutePlanner({...config,avatar:{x:3,z:3},onSelect:selection});
    const arrival=planner.confirm({x:3.5,z:3.5}, {x:3,z:3}, 'AIR-1');
    expect(arrival).toMatchObject({status:'arrival',arrival:true,line:false,route:[]});
    expect(selection).toHaveBeenCalledTimes(1);expect(selection).toHaveBeenCalledWith('AIR-1');
    planner.confirm({x:3.5,z:3.5},{x:3,z:3},'AIR-1');expect(selection).toHaveBeenCalledTimes(1);
  });

  it('HangarRoutePlanner recomputes and replaces routes',()=>{
    const selection=vi.fn();const planner=new HangarRoutePlanner({...config,avatar:{x:-4,z:-4}},selection);
    const marker={x:4,z:4};const first=planner.confirm(marker,{x:-4,z:-4},'AIR-1');expect(first.status).toBe('route');
    const refused=planner.step(false,{x:-3.5,z:-4});expect(refused).toEqual(first);
    const acceptedAvatar={x:-3.5,z:-4};const moved=planner.step(true,acceptedAvatar);expect(moved.status).not.toBe('unreachable');expect(moved.route.length).toBeGreaterThan(0);
    expect(moved.route[0]).toEqual(acceptedAvatar);
    expect(moved.route).not.toEqual(first.route);
    expect(planner.escape()).toMatchObject({status:'cleared',line:false,route:[]});expect(selection).not.toHaveBeenCalled();
    planner.confirm(marker,{x:-4,z:-4},'AIR-1');const other={x:3,z:-4};const second=planner.confirm(other,{x:-4,z:-4},'AIR-2');expect(second.status).toBe('route');
    expect(planner.confirm(other,{x:-4,z:-4},'AIR-2')).toEqual(second);
    const beforeReplacement=planner.getRoute();planner.confirm(marker,{x:-4,z:-4},'AIR-1');expect(planner.getRoute()).not.toEqual(beforeReplacement);
  });

  it('HangarRoutePlanner live region and no transport',()=>{
    const transport=vi.fn();const selection=vi.fn();const planner=new HangarRoutePlanner({...config,avatar:{x:-4,z:-4},transport},selection);
    planner.confirm({x:4,z:4},{x:-4,z:-4},'AIR-1');
    expect(transport).not.toHaveBeenCalled();
    const live=getHangarRouteLiveDirection({x:-4,z:-4},{x:4,z:-4},0);
    expect(live.direction).toBe('right');expect(live.distanceText).toBe('8.00 metres');expect(live.liveText).toBe('right, 8.00 metres');
    expect(['ahead','behind','left','right']).toContain(live.direction);
    planner.step(true,{x:-3.5,z:-4});expect(transport).not.toHaveBeenCalled();
    const arrived=planner.recompute({x:3.75,z:4});
    expect(arrived.status).toBe('arrival');expect(arrived.line).toBe(false);
    expect(transport).toHaveBeenCalledTimes(0);
  });
});

function distance(a:{x:number,z:number},b:{x:number,z:number}){return Math.hypot(a.x-b.x,a.z-b.z)}
function pointInExpanded(point:{x:number,z:number},rects:{x:number,z:number,width?:number,depth?:number}[],radius:number){return rects.some(f=>{const width=f.width??0,depth=f.depth??0;return point.x>=f.x-width/2-radius&&point.x<=f.x+width/2+radius&&point.z>=f.z-depth/2-radius&&point.z<=f.z+depth/2+radius;});}
