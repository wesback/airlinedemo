import { describe, expect, it, vi } from 'vitest';
import {
  HangarWalkController,
  getHangarCameraRelativeStep,
  isHangarWalkStepAllowed,
  handleHangarWalkKeydown,
  shouldCaptureHangarWalkKey,
} from '../src/hangar/walkController';
import { HangarReducedMotion } from '../src/hangar/reducedMotion';

const floor = [{x:-5,z:-5},{x:5,z:-5},{x:5,z:5},{x:-5,z:5}];
const config = (extra = {}) => ({ walkablePolygon: floor, aircraftFootprints: [{x:1,z:1,width:1,depth:1}], rackFootprints: [{x:-3,z:3,width:1,depth:1}], ...extra });

describe('HangarWalkController', () => {
  it('HangarWalkController camera-relative key contract', () => {
    expect(getHangarCameraRelativeStep('ArrowUp', 0)).toEqual({x:0,z:-0.5});
    expect(getHangarCameraRelativeStep('ArrowUp', 90)).toEqual({x:0.5,z:0});
    const azimuth200 = getHangarCameraRelativeStep('ArrowRight', 200)!;
    expect(azimuth200.x).toBeCloseTo(-0.469846310393, 10);
    expect(azimuth200.z).toBeCloseTo(-0.171010071663, 10);
    const controller = new HangarWalkController({x:0,z:0}, config({azimuthDegrees:0}));
    expect(controller.step('ArrowUp').position).toEqual({x:0,z:-0.5});
    expect(controller.step('w').position).toEqual({x:0,z:-0.5});

    const outside = document.createElement('div');
    document.body.append(outside);
    expect(shouldCaptureHangarWalkKey({key:'ArrowUp',target:outside, currentTarget:outside})).toBe(false);
    const wallStart = {x:0,z:-5};
    expect(isHangarWalkStepAllowed(wallStart,{x:0,z:-5.5},config())).toBe(false);
  });

  it('HangarWalkController atomic step and repeat semantics', () => {
    let followCalls = 0;
    const controller = new HangarWalkController({x:0,z:-2}, config({azimuthDegrees:0, followCamera:true, onFollowCamera:()=>{followCalls++}}));
    const accepted = controller.step('ArrowUp');
    expect(accepted).toMatchObject({accepted:true, refusedStep:false, position:{x:0,z:-2.5}, followCameraUpdated:true});
    expect(followCalls).toBe(1);
    const repeat = controller.step('ArrowUp', true);
    expect(repeat).toMatchObject({accepted:false, refusedStep:false, position:{x:0,z:-2.5}});
    expect(followCalls).toBe(1);
    const blocked = new HangarWalkController({x:0.5,z:1}, config());
    const blockedBefore = blocked.getState();
    expect(blocked.step('ArrowRight')).toMatchObject({accepted:false,refusedStep:true,position:{x:0.5,z:1}});
    expect(blocked.getState()).toEqual(blockedBefore);
  });

  it('HangarWalkController keyboard and reduced motion', () => {
    const motion = new HangarReducedMotion(() => ({matches:true}), null);
    const controller = new HangarWalkController({x:0,z:0}, config(), motion);
    const target = controller.step('ArrowUp');
    expect(target.transition).toEqual({target:{x:0,z:-0.5},duration:0,immediate:true});
    expect(target.position).toEqual({x:0,z:-0.5});
    expect(shouldCaptureHangarWalkKey({key:'Tab',target:document.body,currentTarget:document.body})).toBe(false);
    expect(handleHangarWalkKeydown(controller,{key:'Tab',target:document.body,currentTarget:document.body})).toBeNull();

    const region = document.createElement('section');
    region.setAttribute('data-hangar-walk-region','true');
    const child = document.createElement('div'); region.append(child); document.body.append(region);
    const prevented = vi.fn();
    expect(handleHangarWalkKeydown(controller,{key:'ArrowDown',target:child,currentTarget:region,preventDefault:prevented})?.handled).toBe(true);
    expect(prevented).toHaveBeenCalledOnce();
    expect(controller.reducedMotion).toBe(true);
  });
});
