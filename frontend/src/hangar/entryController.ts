import { createElement, useEffect, useState, type ComponentType } from 'react';
import { createHangarCameraController } from './cameraController';
import manifest from './sceneManifest.json';
import { createHangarAvatarController, HangarReducedMotion } from './reducedMotion';
import { validateHangarRackAvatarManifest } from './dataRoomAdapter';
import { buildHangarAircraftSceneGroups } from './sceneBuilder';
import { HangarCanvasAdapter, applyHangarOwnedCamera, createHangarOwnedPerspectiveCamera } from './HangarCanvasAdapter';
import { HANGAR_SCENE_THEME } from './sceneTheme';

export const SUPPORTED_HANGAR_CONTRACT_VERSION = 1;
export const SUPPORTED_HANGAR_BROWSERS = ['Chrome', 'Edge'] as const;
export type HangarFallbackReason = 'unsupported_browser' | 'webgl_unavailable' | 'contract_invalid' | 'asset_invalid';
export type WebGl2Probe = { webgl2?: boolean; supported?: boolean } | object | null;
export type HangarEntryOptions = {
  browserSupported?: unknown;
  browserEligibility?: unknown;
  browser?: unknown;
  mode?: 'regular' | 'hangar' | '3d';
  userAgent?: string;
  webgl2?: WebGl2Probe;
  webgl2Probe?: WebGl2Probe;
  probe?: WebGl2Probe;
  contractVersion?: unknown;
};
export type HangarEntryDecision = {
  mode: '3d' | 'fallback';
  fallbackReason: HangarFallbackReason | 'asset_invalid' | null;
  browserSupported: boolean;
  contractValid: boolean;
  loaderCalled: boolean;
  loadedScene?: unknown;
};

export function isHangarBrowserSupported(browser: unknown, userAgent?: string): boolean {
  if (typeof browser === 'boolean') return browser;
  if (browser && typeof browser === 'object') {
    const candidate = browser as { supported?: unknown; eligible?: unknown; browserSupported?: unknown; eligibleBrowser?: unknown; major?: unknown; version?: unknown; name?: unknown };
    if (Object.keys(candidate).length === 0) return false;
    if ((candidate.name === 'Chrome' || candidate.name === 'Edge') && typeof candidate.version === 'number') return candidate.version >= 120;
    if (typeof candidate.supported === 'boolean') return candidate.supported;
    if (typeof candidate.eligibleBrowser === 'boolean') return candidate.eligibleBrowser;
    if (typeof candidate.name === 'string' && candidate.name !== 'Chrome' && candidate.name !== 'Edge') return false;
    if (typeof candidate.major === 'number') return candidate.major >= 120;
    if (typeof candidate.version === 'string' || typeof candidate.version === 'number') return Number(String(candidate.version).split('.')[0]) >= 120;
    if (typeof candidate.eligible === 'boolean') return candidate.eligible;
    if (typeof candidate.browserSupported === 'boolean') return candidate.browserSupported;
    return false;
  }
  const ua = typeof browser === 'string' ? browser : userAgent ?? (typeof navigator !== 'undefined' ? navigator.userAgent : '');
  if (/Firefox|Safari|FxiOS|CriOS|EdgiOS/i.test(ua) && !/EdgA?|Chrome|Chromium/i.test(ua)) return false;
  const match = ua.match(/(?:Chrome|Chromium|Edg(?:e|A|iOS)?|Edg)\/([0-9]+)/i);
  if (!match) return false;
  return Number(match[1]) >= 120;
}

export function probeWebGl2(input?: WebGl2Probe | true | false | unknown): WebGl2Probe | null {
  if (input !== undefined) {
    if (input === true) return { webgl2: true };
    if (typeof input !== 'object' || input === null || Array.isArray(input)) return null;
    const probe = input as { webgl2?: unknown; supported?: unknown };
    if ('webgl2' in probe && probe.webgl2 !== true) return null;
    if ('supported' in probe && probe.supported !== true) return null;
    return ('webgl2' in probe || 'supported' in probe) ? input as WebGl2Probe : null;
  }
  if (typeof document === 'undefined') return null;
  try {
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('webgl2');
    return context ? { webgl2: true } : null;
  } catch { return null; }
}

export function getHangarEntryDecision(options: HangarEntryOptions = {}): HangarEntryDecision {
  const browserSupported = options.browserSupported !== undefined
    ? isHangarBrowserSupported(options.browserSupported)
    : options.browserEligibility !== undefined
      ? isHangarBrowserSupported(options.browserEligibility)
      : options.browser !== undefined
        ? isHangarBrowserSupported(options.browser)
        : isHangarBrowserSupported(options.userAgent);
  const probeInput = options.webgl2Probe !== undefined
    ? options.webgl2Probe
    : options.webgl2 !== undefined
      ? options.webgl2
      : options.probe;
  const hasProbeInput = probeInput !== undefined;
  const probe = hasProbeInput ? probeWebGl2(probeInput) : probeWebGl2();
  const contractValid = options.contractVersion === SUPPORTED_HANGAR_CONTRACT_VERSION;
  let fallbackReason: HangarFallbackReason | null = null;
  if (!contractValid) fallbackReason = 'contract_invalid';
  else if (!validateHangarRackAvatarManifest().valid) fallbackReason = 'asset_invalid';
  else if (!browserSupported) fallbackReason = 'unsupported_browser';
  else if (probe === null) fallbackReason = 'webgl_unavailable';
  return { mode: fallbackReason ? 'fallback' : '3d', fallbackReason, browserSupported, contractValid, loaderCalled: false };
}

export async function selectHangarEntry<T>(options: HangarEntryOptions = {}, lazyLoader?: () => Promise<T> | T): Promise<HangarEntryDecision> {
  const decision = getHangarEntryDecision(options);
  if (decision.mode !== '3d' || !lazyLoader) return decision;
  const loadedScene = await lazyLoader();
  return { ...decision, loaderCalled: true, loadedScene };
}

export function createHangarSceneMotionControllers(targetRadius: number | HangarReducedMotion = 10, motion = new HangarReducedMotion()) {
  const resolvedMotion = targetRadius instanceof HangarReducedMotion ? targetRadius : motion;
  const resolvedTargetRadius = typeof targetRadius === 'number' ? targetRadius : 10;
  return {
    motion: resolvedMotion,
    camera: createHangarCameraController(resolvedTargetRadius, resolvedMotion),
    avatar: createHangarAvatarController({ x: manifest.layout.avatarStart.x, z: manifest.layout.avatarStart.z }, resolvedMotion),
  };
}

export type HangarSceneProps = { onSelect?: (aircraftId: string) => unknown };

export async function loadHangarScene(controllers = createHangarSceneMotionControllers()): Promise<ComponentType<HangarSceneProps>> {
  const fiber = await import('@react-three/fiber');
  const { Canvas } = fiber;
  const cameraTransition = controllers.camera.overview(); controllers.avatar.move({ x: manifest.layout.avatarStart.x, z: manifest.layout.avatarStart.z }, 240);
  const HangarScene: ComponentType<HangarSceneProps> = function HangarScene(props: HangarSceneProps | null = {}) {
    const onSelect = props?.onSelect;
    const [reduced, setReduced] = useState(controllers.motion.reduced);
    const [cameraState, setCameraState] = useState(cameraTransition.state);
    const [avatarState, setAvatarState] = useState(controllers.avatar.getState());
    const [selected, setSelected] = useState<string|null>(null);
    const [ownedCamera] = useState(()=>{const camera=createHangarOwnedPerspectiveCamera();applyHangarOwnedCamera(camera,3*Math.sqrt(2650),0,45,{x:0,y:0,z:0});return camera;});
    const [canvasInvalidate,setCanvasInvalidate]=useState<(() => unknown)|undefined>();
    const CaptureCanvasInvalidation: ComponentType = function CaptureCanvasInvalidation(){
      const useThree=(fiber as any).useThree;
      const invalidate=typeof useThree==='function'?useThree((state:any)=>state.invalidate):undefined;
      useEffect(()=>{if(typeof invalidate==='function')setCanvasInvalidate(()=>invalidate);},[invalidate]);
      return null;
    };
    useEffect(() => controllers.motion.subscribe((active) => {
      setReduced(active);
      setCameraState(controllers.camera.applyCameraTransition(controllers.camera.getState(), 480).target);
      const avatarTransition=controllers.avatar.move(controllers.avatar.getState(),240);
      setAvatarState(avatarTransition.target);
    }), []);
    const navigateCamera = (preset: 'overview'|'aircraft', letter?: 'A'|'B'|'C') => {
      setSelected(letter ? `SYN-CP-${letter}-AIR-001` : null);
      if(preset==='overview'){
        const R=Math.sqrt(2650);
        controllers.camera.setTargetRadius(R);
        const transition=controllers.camera.overview();
        setCameraState(transition.state);
      }else if(letter){
        const layout=(manifest.layout.aircraft as any[]).find(row=>row.caseId===`cp-case-${letter.toLowerCase()}`);
        if(layout){
          const d=layout.dimensions;
          const R=Math.sqrt((d.span/2)**2+(d.length/2)**2+(d.height/2)**2);
          controllers.camera.setTargetRadius(R);
          const transition=controllers.camera.aircraft();
          setCameraState(transition.state);
        }
      }
    };
    const setMotionOverride=(override:true|false)=>{controllers.motion.setOverride(override);setCameraState(controllers.camera.getState());setReduced(controllers.motion.reduced);};
    const handleCameraKey=(key:string)=>{const result=controllers.camera.key(key);if(result.handled)setCameraState(result.state);};
    const sceneView={contractVersion:1,cases:[{caseId:'cp-case-a',status:{assessment:'ready'},owner:'A',nextAction:{text:'Review'},capabilityLabel:'Rules-based'},{caseId:'cp-case-b',status:{assessment:'ready'},owner:'B',nextAction:{text:'Review'},capabilityLabel:'Rules-based'},{caseId:'cp-case-c',status:{assessment:'ready'},owner:'C',nextAction:{text:'Review'},capabilityLabel:'Rules-based'}],selectedObjectId:selected};
    const sceneGroup=buildHangarAircraftSceneGroups(undefined,sceneView,(id)=>{setSelected(id);onSelect?.(id);});
    return createElement('div',{'data-hangar-reduced-motion':reduced?'true':'false','data-hangar-camera-duration':String(controllers.camera.transitionDuration),'data-hangar-camera-position':`${cameraState.radius},${cameraState.azimuthDegrees},${cameraState.polarDegrees}`},
      createElement('section',{'data-hangar-camera-region':true,'aria-label':'Camera controls',tabIndex:0,onKeyDown:(event:any)=>{if(event.key)handleCameraKey(event.key);}},
        createElement('button',{type:'button',onClick:()=>navigateCamera('overview')},'Overview'),
        createElement('button',{type:'button',onClick:()=>navigateCamera('aircraft','A')},'View Case A'),
        createElement('button',{type:'button',onClick:()=>navigateCamera('aircraft','B')},'View Case B'),
        createElement('button',{type:'button',onClick:()=>navigateCamera('aircraft','C')},'View Case C'),
      ),
      createElement('section',{'aria-label':'Motion controls'},createElement('button',{type:'button',onClick:()=>setMotionOverride(true)},'Reduce motion'),createElement('button',{type:'button',onClick:()=>setMotionOverride(false)},'Allow motion')),
      createElement(HangarCanvasAdapter,{camera:ownedCamera,sceneGroup,sceneData:sceneView,selection:selected,avatar:avatarState,route:null,invalidate:canvasInvalidate,invalidationCapture:CaptureCanvasInvalidation,target:selected?(()=>{const letter=selected.match(/^SYN-CP-([ABC])-AIR-001$/)?.[1]?.toLowerCase();const layout=(manifest.layout.aircraft as any[]).find(row=>row.caseId===`cp-case-${letter}`);return layout?{x:layout.centre.x,y:layout.dimensions.height/2,z:layout.centre.z}:{x:0,y:0,z:0};})():{x:0,y:0,z:0},radius:cameraState.radius,reducedMotion:reduced,azimuthDegrees:cameraState.azimuthDegrees,polarDegrees:cameraState.polarDegrees,canvasComponent:Canvas}),
    );
  };
  return HangarScene;
}

export function isNon3dFallbackDecision(decision: HangarEntryDecision): boolean { return decision.mode === 'fallback'; }
export const decideHangarEntry = getHangarEntryDecision;
export const evaluateHangarEntry = getHangarEntryDecision;
export const canEnterHangar3d = (options: HangarEntryOptions = {}) => getHangarEntryDecision(options).mode === '3d';

export const HANGAR_NON_3D_ACTIONS = ['selection', 'status', 'evidence', 'journey', 'review'] as const;
export function getHangarNon3dActions(): readonly string[] { return [...HANGAR_NON_3D_ACTIONS]; }
export const isSupportedBrowser = isHangarBrowserSupported;
export const probeWebGl2Capability = probeWebGl2;
export function checkHangarContractEligibility(contractVersion: unknown): boolean { return contractVersion === SUPPORTED_HANGAR_CONTRACT_VERSION; }
export function getHangarFallbackReason(options: HangarEntryOptions = {}): HangarFallbackReason | null { return getHangarEntryDecision(options).fallbackReason; }
export const HANGAR_3D_ACTIONS = ['selection', 'status', 'evidence', 'journey', 'review'] as const;
