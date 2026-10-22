import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, fireEvent, screen, cleanup } from '@testing-library/react';
import React from 'react';
import {
  HangarFollowCamera, createHangarFollowCamera, hangarAvatarTarget,
  HANGAR_FOLLOW_DEFAULT_RADIUS, HANGAR_FOLLOW_DEFAULT_POLAR_DEGREES,
} from '../src/hangar/followCamera';
import { HangarReducedMotion } from '../src/hangar/reducedMotion';
import { FollowCameraControl } from '../src/hangar/FollowCameraControl';

afterEach(() => cleanup());

describe('HangarFollowCamera', () => {
  it('HangarFollowCamera clamp and default contract', () => {
    const camera = createHangarFollowCamera({x:1,y:0,z:2});
    expect(camera.getState().target).toEqual({x:1,y:1,z:2});
    expect(camera.getState()).toMatchObject({radius:6,polarDegrees:55,follow:true});
    camera.key('ArrowRight'); camera.key('ArrowUp'); camera.key('-'); camera.key('ArrowDown');
    expect(camera.getState().azimuthDegrees).toBe(10);
    expect(camera.getState().polarDegrees).toBeGreaterThanOrEqual(15);
    expect(camera.getState().radius).toBeLessThanOrEqual(12);
    for(let i=0;i<30;i++){camera.key('+');camera.key('ArrowUp')}
    expect(camera.getState().radius).toBeGreaterThanOrEqual(4);
    expect(camera.getState().polarDegrees).toBeGreaterThanOrEqual(15);
    for(let i=0;i<30;i++){camera.key('-');camera.key('ArrowDown')}
    expect(camera.getState().radius).toBeLessThanOrEqual(12);
    expect(camera.getState().polarDegrees).toBeLessThanOrEqual(85);

    const before = camera.getState();
    expect(camera.applyAvatarStep({x:2,y:0,z:2}, false).target).toEqual(before);
    expect(camera.getState()).toEqual(before);
  });

  it('HangarFollowCamera preset and toggle behavior', () => {
    const camera = createHangarFollowCamera();
    camera.key('ArrowLeft');
    expect(camera.getState().azimuthDegrees).toBe(350);
    const before = camera.getState();
    expect(camera.applyAvatarStep(null, false).target).toEqual(before);
    const overview = camera.applyPreset('overview');
    expect(overview.follow).toBe(false);
    expect(overview.radius).toBe(30);
    expect(overview.polarDegrees).toBe(45);
    expect(overview.position.x).toBeCloseTo(30 * Math.sin(45 * Math.PI / 180) * Math.sin(350 * Math.PI / 180), 10);
    expect(overview.position.y).toBeCloseTo(1 + 30 * Math.cos(45 * Math.PI / 180), 10);
    expect(overview.position.z).toBeCloseTo(30 * Math.sin(45 * Math.PI / 180) * Math.cos(350 * Math.PI / 180), 10);
    const aircraft = camera.applyPreset('aircraft');
    expect(aircraft.follow).toBe(false);
    expect(aircraft.radius).toBe(20);
    expect(aircraft.polarDegrees).toBe(35);
    expect(aircraft.position.x).toBeCloseTo(20 * Math.sin(35 * Math.PI / 180) * Math.sin(350 * Math.PI / 180), 10);
    expect(aircraft.position.y).toBeCloseTo(1 + 20 * Math.cos(35 * Math.PI / 180), 10);
    expect(aircraft.position.z).toBeCloseTo(20 * Math.sin(35 * Math.PI / 180) * Math.cos(350 * Math.PI / 180), 10);
    camera.turnOn();
    expect(camera.getState()).toMatchObject({azimuthDegrees:350,radius:6,polarDegrees:55,follow:true});
    for(let i=0;i<10;i++) camera.orbit(370,90);
    expect(camera.getState().polarDegrees).toBeLessThanOrEqual(85);

    render(React.createElement(FollowCameraControl,{controller:camera,onFollowChange:vi.fn()}));
    const orbitButton = screen.getByRole('button',{name:'Orbit camera'});
    const azimuthBeforeKey = camera.getState().azimuthDegrees;
    fireEvent.keyDown(orbitButton,{key:'ArrowLeft'});
    expect(camera.getState().azimuthDegrees).toBe((azimuthBeforeKey - 10 + 360) % 360);
    expect(camera.getState().polarDegrees).toBe(85);
    fireEvent.click(screen.getByRole('button',{name:'Follow avatar: on'}));
    expect(camera.getState().follow).toBe(false);
    cleanup();
  });

  it('HangarFollowCamera easing box and reduced motion', () => {
    let now = 1000;
    const camera = createHangarFollowCamera({x:0,y:0,z:0},{clock:()=>now,box:{min:{x:-20,y:-20,z:-20},max:{x:20,y:20,z:20}}});
    camera.applyAvatarStep({x:10,y:0,z:10});
    now += 400;
    camera.update(now);
    const easedTarget = hangarAvatarTarget({x:10,y:0,z:10});
    const easedState = camera.getState().target;
    const targetDistance = Math.hypot(
      easedTarget.x - easedState.x,
      easedTarget.y - easedState.y,
      easedTarget.z - easedState.z,
    );
    expect(targetDistance).toBeLessThanOrEqual(0.05);
    now = 1400;
    camera.applyAvatarStep({x:20,y:0,z:20});
    now += 401;
    camera.update(now);
    expect(camera.getState().target.x).toBeCloseTo(20,0);

    const motion = new HangarReducedMotion(()=>({matches:true}),null);
    const reduced = createHangarFollowCamera({x:0,y:0,z:0},{reducedMotion:motion});
    const transition = reduced.applyAvatarStep({x:3,y:0,z:4});
    expect(transition).toMatchObject({duration:0,immediate:true,target:{target:{x:3,y:1,z:4}}});

    const boxed = createHangarFollowCamera({x:0,y:0,z:0},{box:{minX:-3,maxX:3,minY:-3,maxY:5,minZ:-3,maxZ:3}});
    expect(boxed.getState().position.x).toBeGreaterThanOrEqual(-3);
    expect(boxed.getState().position.x).toBeLessThanOrEqual(3);
    expect(boxed.getState().position.z).toBeGreaterThanOrEqual(-3);
    expect(boxed.getState().position.z).toBeLessThanOrEqual(3);
    const boxDistance = Math.hypot(boxed.getState().position.x-boxed.getState().target.x,boxed.getState().position.y-boxed.getState().target.y,boxed.getState().position.z-boxed.getState().target.z);
    expect(boxDistance).toBeGreaterThanOrEqual(2);
  });
});
