import { createElement, Children, type ReactNode } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  HANGAR_REDUCED_MOTION_STORAGE_KEY,
  HangarReducedMotion,
  createHangarAvatarController,
  applyHangarTransition,
  getHangarReducedMotionOverride,
  getHangarSystemReducedMotion,
  shouldReduceHangarMotion,
} from '../src/hangar/reducedMotion';
import * as THREE from 'three';
import { createHangarCameraController } from '../src/hangar/cameraController';
import { HangarCanvasAdapter, applyHangarOwnedCamera, createHangarOwnedPerspectiveCamera, getHangarDemandCanvasProps, createHangarDemandInvalidator } from '../src/hangar/HangarCanvasAdapter';
import { createHangarSceneMotionControllers, loadHangarScene } from '../src/hangar/entryController';
import manifest from '../src/hangar/sceneManifest.json';
import { buildHangarAircraftSceneGroups } from '../src/hangar/sceneBuilder';

const fiberHarness = vi.hoisted(() => ({ canvasProps: [] as any[], invalidate: vi.fn(), frames: [] as FrameRequestCallback[], builderGroups: [] as any[] }));
vi.mock('../src/hangar/sceneBuilder', async (importOriginal) => {
  const actual=await importOriginal<typeof import('../src/hangar/sceneBuilder')>();
  return {...actual,buildHangarAircraftSceneGroups:(...args:any[])=>{const group=actual.buildHangarAircraftSceneGroups(...args as [any,any,any?]);if(group)fiberHarness.builderGroups.push(group);return group;}};
});
vi.mock('@react-three/fiber', async () => {
  const React = await import('react');
  return {
    useFrame: () => undefined,
    useThree: (selector: (state: { invalidate: () => void }) => unknown) => selector({ invalidate: fiberHarness.invalidate }),
    Canvas: (props: any) => {
      fiberHarness.canvasProps.push(props);
      const childList=React.Children.toArray(props.children as ReactNode);
      const sceneMounted=childList.some((child:any)=>React.isValidElement(child)&&(child as any).props?.object instanceof THREE.Group);
      return React.createElement(
        'div',
        { 'data-hangar-rendered-camera-position': props.camera?.position?.toArray?.().join(',') ?? '', 'data-hangar-scene-mounted': sceneMounted?'true':'false' },
        props.children as ReactNode,
      );
    },
  };
});

const memoryStorage = () => {
  const values = new Map<string, string>();
  return {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value); },
    removeItem: (key: string) => { values.delete(key); },
  };
};

afterEach(() => { cleanup(); sessionStorage.clear(); });

describe('HangarReducedMotion', () => {
  it('HangarReducedMotion applies system preference', () => {
    const storage = memoryStorage();
    const motion = new HangarReducedMotion(() => ({ matches: true }), storage);

    expect(getHangarSystemReducedMotion(() => ({ matches: true }))).toBe(true);
    expect(shouldReduceHangarMotion(() => ({ matches: true }), storage)).toBe(true);
    expect(motion.getDuration(480)).toBe(0);

    const cameraTransitionResult = motion.applyTransition({ radius: 20, azimuthDegrees: 10 }, 480);
    const avatarTransitionResult = motion.applyTransition({ x: 4, z: -2 }, 240);
    expect(cameraTransitionResult).toEqual({ target: { radius: 20, azimuthDegrees: 10 }, duration: 0, immediate: true });
    expect(avatarTransitionResult).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    expect(applyHangarTransition('final', 999, true).duration).toBe(0);

    const camera = createHangarCameraController(10, motion);
    const cameraTransition = camera.applyCameraTransition(camera.overview().state, 480);
    expect(cameraTransition).toEqual({ target: camera.getState(), duration: 0, immediate: true });
    expect(camera.overview().state.radius).toBe(30);
    expect(camera.transitionDuration).toBe(0);
    expect(motion.applyAvatarTransition({ x: 4, z: -2 }, 240)).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    const avatar = createHangarAvatarController({ x: 0, z: 0 }, motion);
    expect(avatar.move({ x: 4, z: -2 }, 240)).toEqual({ target: { x: 4, z: -2 }, duration: 0, immediate: true });
    expect(avatar.getState()).toEqual({ x: 4, z: -2 });
    expect(avatar.transitionDuration).toBe(0);
  });

  it('HangarReducedMotion renders camera and avatar targets immediately for system preference', async () => {
    const motion = new HangarReducedMotion(() => ({ matches: true }), memoryStorage());
    const controllers = createHangarSceneMotionControllers(motion);
    const Scene = await loadHangarScene(controllers);
    render(createElement(Scene));

    fireEvent.click(screen.getByRole('button', { name: 'View Case A' }));
    expect(controllers.camera.transitionDuration).toBe(0);
    const firstCaseRadius = 2 * Math.sqrt((22 / 2) ** 2 + (24 / 2) ** 2 + (7 / 2) ** 2);
    expect(controllers.camera.getState().radius).toBeCloseTo(firstCaseRadius, 12);
    fireEvent.click(screen.getByRole('button', { name: 'Overview' }));
    expect(controllers.camera.getState()).toMatchObject({ radius: 3 * Math.sqrt(2650), azimuthDegrees: 0, polarDegrees: 45 });
    const root=document.querySelector('[data-hangar-reduced-motion="true"]')!;
    expect(root.getAttribute('data-hangar-avatar-target')).toBe('-4,20');
    const rendered=render(createElement(Scene,{avatarTarget:{x:2,z:-3}}));
    expect(rendered.container.querySelector('[data-hangar-avatar-target]')?.getAttribute('data-hangar-avatar-target')).toBe('2,-3');

    const normalMotion = new HangarReducedMotion(() => ({ matches: false }), memoryStorage());
    const normalControllers = createHangarSceneMotionControllers(normalMotion);
    const NormalScene = await loadHangarScene(normalControllers);
    render(createElement(NormalScene));
    fireEvent.click(screen.getAllByRole('button', { name: 'View Case A' }).at(-1)!);
    expect(normalControllers.camera.transitionDuration).toBe(480);
    const caseARadius = 2 * Math.sqrt((22 / 2) ** 2 + (24 / 2) ** 2 + (7 / 2) ** 2);
    expect(normalControllers.camera.getState().radius).toBeCloseTo(caseARadius, 12);
  });

  it('HangarCamera mounts one owned perspective camera and exact presets', async () => {
    const camera = createHangarOwnedPerspectiveCamera();
    expect(camera).toBeInstanceOf(THREE.PerspectiveCamera);
    expect(camera.fov).toBe(40); expect(camera.near).toBe(0.5); expect(camera.far).toBe(1000);
    const formula=(R:number,a:number,p:number,t={x:0,y:0,z:0})=>{const az=a*Math.PI/180,polar=p*Math.PI/180,h=R*Math.sin(polar);return new THREE.Vector3(t.x-h*Math.sin(az),t.y+R*Math.cos(polar),t.z+h*Math.cos(az));};
    const overview=3*Math.sqrt(2650); expect(applyHangarOwnedCamera(camera,overview,0,45,{x:0,y:0,z:0})).toBe(true);
    expect(camera.position.distanceTo(formula(overview,0,45))).toBeLessThanOrEqual(1e-6);
    const last=camera.position.clone(); const look=camera.position.clone();
    for(const radius of [undefined,null,Number.NaN,Infinity,-Infinity,0,-1,'3']) expect(applyHangarOwnedCamera(camera,radius as number,0,35)).toBe(false);
    expect(camera.position.toArray()).toEqual(last.toArray());

    fiberHarness.canvasProps=[];
    const motion=new HangarReducedMotion(()=>({matches:true}),memoryStorage());
    const controllers=createHangarSceneMotionControllers(motion); const Scene=await loadHangarScene(controllers);
    fiberHarness.builderGroups=[];
    try {
      render(createElement(Scene));
      const props=fiberHarness.canvasProps.at(-1)!;
      expect(props.frameloop).toBe('demand'); expect(props.dpr).toEqual([1,2]); expect(props.gl).toEqual({antialias:true}); expect(props).not.toHaveProperty('shadows');
      expect(props.camera).toBeInstanceOf(THREE.PerspectiveCamera); expect(props.camera.fov).toBe(40); expect(props.camera.near).toBe(0.5); expect(props.camera.far).toBe(1000);
      const mounted=Children.toArray(props.children).find((child:any)=>child?.props?.object instanceof THREE.Group);
      expect(mounted).toBeDefined();
      const mountedGroup=(mounted as any).props.object as THREE.Group;
      expect(fiberHarness.builderGroups).toContain(mountedGroup);
      for(const row of manifest.layout.aircraft){const letter=row.caseId.endsWith('a')?'A':row.caseId.endsWith('b')?'B':'C'; fireEvent.click(screen.getByRole('button',{name:`View Case ${letter}`}));const R=2*Math.sqrt((row.dimensions.span/2)**2+(row.dimensions.length/2)**2+(row.dimensions.height/2)**2);expect(props.camera.position.distanceTo(formula(R,0,35,{x:row.centre.x,y:row.dimensions.height/2,z:row.centre.z}))).toBeLessThanOrEqual(1e-6);}
      fireEvent.click(screen.getByRole('button',{name:'View Case A'})); fireEvent.keyDown(screen.getByRole('region',{name:'Camera controls'}),{key:'ArrowLeft'});const row=manifest.layout.aircraft[0];const R=2*Math.sqrt((row.dimensions.span/2)**2+(row.dimensions.length/2)**2+(row.dimensions.height/2)**2);expect(props.camera.position.distanceTo(formula(R,350,35,{x:-28,y:3.5,z:0}))).toBeLessThanOrEqual(1e-6);
    } finally {}
  });

  it('Hangar demand canvas invalidates only on named changes', async () => {
    const camera=createHangarOwnedPerspectiveCamera(); const props=getHangarDemandCanvasProps(camera);
    expect(props).toEqual({frameloop:'demand',dpr:[1,2],gl:{antialias:true},camera}); expect(props).not.toHaveProperty('shadows');
    fiberHarness.invalidate.mockClear(); fiberHarness.canvasProps=[]; fiberHarness.frames=[];
    const raf=vi.spyOn(globalThis,'requestAnimationFrame').mockImplementation((callback)=>{fiberHarness.frames.push(callback);return fiberHarness.frames.length;});
    const normal=new HangarReducedMotion(()=>({matches:false}),memoryStorage()); const controllers=createHangarSceneMotionControllers(normal); const Scene=await loadHangarScene(controllers);
    const rendered=render(createElement(Scene));
    expect(fiberHarness.canvasProps.at(-1)?.frameloop).toBe('demand');
    fiberHarness.invalidate.mockClear();
    fireEvent.click(screen.getByRole('button',{name:'View Case A'}));
    expect(fiberHarness.invalidate.mock.calls.length).toBeGreaterThanOrEqual(1);
    expect(fiberHarness.frames.length).toBeGreaterThan(0);
    for(let i=0;i<10&&fiberHarness.frames.length>0;i++){const callback=fiberHarness.frames.shift()!;callback(performance.now()+16);}
    expect(fiberHarness.invalidate.mock.calls.length).toBeGreaterThanOrEqual(8);
    fireEvent.click(screen.getByRole('button',{name:'Overview'})); fiberHarness.invalidate.mockClear();
    rendered.rerender(createElement(Scene,{avatarTarget:{x:1,z:2}}));
    expect(fiberHarness.invalidate.mock.calls.length).toBeGreaterThanOrEqual(1);
    fireEvent.click(screen.getByRole('button',{name:'Pointer select AIR-1'}));expect(fiberHarness.invalidate.mock.calls.length).toBeGreaterThanOrEqual(1);
    const idleCalls=fiberHarness.invalidate.mock.calls.length; for(let i=0;i<10;i++){fiberHarness.frames.splice(0,0);}expect(fiberHarness.invalidate.mock.calls.length).toBe(idleCalls);
    raf.mockRestore();
  });

  it('HangarReducedMotion session override controls rendered transitions in both directions', async () => {
    const storage = memoryStorage();
    let systemReduce = false;
    const motion = new HangarReducedMotion(() => ({ matches: systemReduce }), storage);
    const controllers = createHangarSceneMotionControllers(motion);
    const Scene = await loadHangarScene(controllers);
    render(createElement(Scene));

    fireEvent.click(screen.getByRole('button', { name: 'Reduce motion' }));
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBe('reduce');
    expect(controllers.camera.transitionDuration).toBe(0);

    systemReduce = true;
    fireEvent.click(screen.getByRole('button', { name: 'Allow motion' }));
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBe('no-preference');
    expect(controllers.camera.transitionDuration).toBe(480);
  });

  it('HangarReducedMotion connects camera targets to the rendered scene', async () => {
    const motion = new HangarReducedMotion(() => ({ matches: true }), memoryStorage());
    const controllers = createHangarSceneMotionControllers(motion);
    const Scene = await loadHangarScene(controllers);
    render(createElement(Scene));

    fireEvent.click(screen.getByRole('button', { name: 'View Case A' }));
    const caseARadius = 2 * Math.sqrt((22 / 2) ** 2 + (24 / 2) ** 2 + (7 / 2) ** 2);
    expect(controllers.camera.getState().radius).toBeCloseTo(caseARadius, 12);
    expect(controllers.camera.transitionDuration).toBe(0);
    expect(document.querySelector('[data-hangar-camera-position]')?.getAttribute('data-hangar-camera-position')).toBe(`${caseARadius},0,35`);
    fireEvent.click(screen.getByRole('button', { name: 'Overview' }));
    expect(controllers.camera.getState()).toMatchObject({ radius:3*Math.sqrt(2650), azimuthDegrees:0, polarDegrees:45 });
  });

  it('Case A ArrowLeft preserves preset radius and exact camera pose', async () => {
    const motion=new HangarReducedMotion(()=>({matches:true}),memoryStorage());
    const controllers=createHangarSceneMotionControllers(motion); const Scene=await loadHangarScene(controllers);
    render(createElement(Scene)); fireEvent.click(screen.getByRole('button',{name:'View Case A'}));
    fireEvent.keyDown(screen.getByRole('region',{name:'Camera controls'}),{key:'ArrowLeft'});
    const R=2*Math.sqrt((22/2)**2+(24/2)**2+(7/2)**2);
    expect(controllers.camera.getState()).toMatchObject({targetRadius:Math.sqrt((11)**2+(12)**2+(3.5)**2),radius:R,azimuthDegrees:350,polarDegrees:35});
    const camera=createHangarOwnedPerspectiveCamera(); expect(applyHangarOwnedCamera(camera,R,350,35,{x:-28,y:3.5,z:0})).toBe(true);
    const polar=35*Math.PI/180,azimuth=350*Math.PI/180,horizontal=R*Math.sin(polar);
    const expected=new THREE.Vector3(-28-horizontal*Math.sin(azimuth),3.5+R*Math.cos(polar),0+horizontal*Math.cos(azimuth));
    expect(camera.position.distanceTo(expected)).toBeLessThanOrEqual(1e-6);
  });

  it('HangarReducedMotion session override', () => {
    const storage = memoryStorage();
    let systemReduce = false;
    const motion = new HangarReducedMotion(() => ({ matches: systemReduce }), storage);

    expect(motion.reduced).toBe(false);
    const updates: boolean[] = [];
    const unsubscribe = motion.subscribe((active) => updates.push(active));
    expect(motion.setOverride(true)).toBe(true);
    expect(updates).toEqual([true]);
    expect(getHangarReducedMotionOverride(storage)).toBe(true);
    expect(motion.reduced).toBe(true);
    expect(motion.applyTransition('camera-final', 600).duration).toBe(0);
    expect(motion.applyTransition('avatar-final', 300).duration).toBe(0);

    systemReduce = true;
    expect(motion.setOverride(false)).toBe(false);
    expect(updates).toEqual([true, false]);
    expect(motion.reduced).toBe(false);
    expect(motion.getDuration(600)).toBe(600);
    expect(motion.getDuration(300)).toBe(300);
    const normalCamera = createHangarCameraController(10, motion);
    expect(normalCamera.applyCameraTransition(normalCamera.overview().state, 600).duration).toBe(600);
    expect(normalCamera.applyCameraTransition(normalCamera.aircraft().state, 300).duration).toBe(300);

    motion.clearOverride();
    expect(motion.overrideValue).toBeNull();
    expect(motion.reduced).toBe(true);
    expect(storage.getItem(HANGAR_REDUCED_MOTION_STORAGE_KEY)).toBeNull();

    const controllerWithReduction = createHangarCameraController(10, motion);
    expect(controllerWithReduction.applyCameraTransition(controllerWithReduction.overview().state, 480)).toEqual({ target: controllerWithReduction.getState(), duration: 0, immediate: true });

    systemReduce = false;
    motion.setOverride(true);
    const controllerWithOverride = createHangarCameraController(10, motion);
    expect(controllerWithOverride.applyCameraTransition(controllerWithOverride.overview().state, 600).duration).toBe(0);
    expect(controllerWithOverride.applyCameraTransition(controllerWithOverride.aircraft().state, 300)).toEqual({ target: controllerWithOverride.getState(), duration: 0, immediate: true });
    unsubscribe();
  });
});
