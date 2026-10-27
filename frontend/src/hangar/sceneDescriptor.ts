import manifestDefault from './sceneManifest.json';
import { HANGAR_ACTUAL_OBJECT_IDS } from './sceneProjection';
import { hangarSceneColors } from '../theme/altivaneTheme';
import { HANGAR_SCENE_THEME, hangarLabelContrastRatio, validateHangarSceneColor } from './sceneTheme';

export const HANGAR_DESCRIPTOR_ERROR = 'Visible mapping error';
export type HangarAircraftViewEntry = {caseId:string;name:string;category:string;status:Record<string,string|null>;capabilityLabel:string;objectId:string};
const names = {'cp-case-a':'Case A','cp-case-b':'Case B','cp-case-c':'Case C'} as const;
const categories = {'cp-case-a':'narrow-body','cp-case-b':'regional','cp-case-c':'wide-body'} as const;
const axes = ['assessment','review','request','finance','legal','acceptance'] as const;

export function projectHangarAircraftViewModel(caseList:unknown,scope:unknown):HangarAircraftViewEntry[] {
  const scoped=new Set(Array.isArray(scope)?scope.filter((v):v is string=>typeof v==='string'):[]);
  if(scope&&typeof scope==='object'&&Array.isArray((scope as any).caseScopes)) for(const v of (scope as any).caseScopes) if(typeof v==='string') scoped.add(v);
  const list=Array.isArray(caseList)?caseList:(caseList&&typeof caseList==='object'&&Array.isArray((caseList as any).cases)?(caseList as any).cases:[]);
  const seen=new Set<string>(); const result:HangarAircraftViewEntry[]=[];
  for(const raw of list){if(!raw||typeof raw!=='object')continue;const c=raw as any;const caseId=typeof c.caseId==='string'?c.caseId:typeof c.id==='string'?c.id:null;if(!caseId||!scoped.has(caseId)||seen.has(caseId)||!(caseId in names)||seen.size>=3)continue;const source=c.status&&typeof c.status==='object'?c.status:{};const status=Object.fromEntries(axes.map(k=>[k,typeof source[k]==='string'||source[k]===null?source[k]:null])) as Record<string,string|null>;seen.add(caseId);result.push({caseId,name:names[caseId as keyof typeof names],category:categories[caseId as keyof typeof categories],status,capabilityLabel:typeof c.capabilityLabel==='string'?c.capabilityLabel:'Unknown',objectId:`SYN-CP-${caseId.slice(-1).toUpperCase()}-AIR-001`});}
  return result;
}

export function validateHangarAircraftDescriptor(input:unknown=manifestDefault):{valid:boolean;descriptor?:Array<{objectId:string;caseId:string;kind:string;node:string}>;error?:string} {
  if(!input||typeof input!=='object') return {valid:false,error:HANGAR_DESCRIPTOR_ERROR}; const m=input as any;
  const approvedTheme=HANGAR_SCENE_THEME;
  if(Object.keys(approvedTheme).some(key=>(HANGAR_SCENE_THEME as any)[key]!==approvedTheme[key as keyof typeof approvedTheme])||hangarLabelContrastRatio(HANGAR_SCENE_THEME.labelText)<4.5||hangarLabelContrastRatio(HANGAR_SCENE_THEME.labelMuted)<4.5) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  const validateColors=(value:unknown,path:string=''):boolean=>{if(!value||typeof value!== 'object')return true;if(Array.isArray(value))return value.every((item,index)=>validateColors(item,path+`[${index}]`));for(const [key,child] of Object.entries(value as Record<string,unknown>)){const childPath=path?`${path}.${key}`:key;if(/color/i.test(key)){if(childPath==='avatar.hardHat.color'){if(child!==hangarSceneColors.hardHat)return false;}else if(!validateHangarSceneColor(child).valid)return false;}if(!validateColors(child,childPath))return false;}return true};
  if(!validateColors(m)) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  if(m.contractVersion!==1||m.packVersion!=='0.4.0'||!m.layout||m.layout.floorPolygon?.length!==4||!Array.isArray(m.aircraft?.models)||m.aircraft.models.length!==3||!Array.isArray(m.objectIds)||m.objectIds.length!==12) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  const floor=[[-45,-25],[45,-25],[45,25],[-45,25]];
  if(!floor.every((p,i)=>m.layout.floorPolygon[i]?.x===p[0]&&m.layout.floorPolygon[i]?.z===p[1])) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  const slab=m.layout.floorSlab;if(!slab||slab.width!==90||slab.height!==0.2||slab.depth!==50||slab.topY!==0) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  const specs=[
    ['cp-case-a','narrow-body','a',-28,0,24,22,7,-39,-17,-12,12,-28,14],['cp-case-b','regional','b',-4,-2,18,17,6,-12.5,4.5,-11,7,-4,9],['cp-case-c','wide-body','c',25,0,32,30,10,10,40,-16,16,25,18]
  ] as const;
  const seenNodes=new Set<string>(),seenIds=new Set<string>(),descriptor:any[]=[];
  const rack=m.layout.rack; const interaction=m.layout.interactionCircle; const avatar=m.layout.avatarStart; const box=m.layout.cameraBox;
  if(!rack||rack.height!==2.2||rack.frontZ!==-21||rack.footprint?.minX!==-5||rack.footprint?.maxX!==-3||rack.footprint?.minZ!==-22||rack.footprint?.maxZ!==-21) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  if(!interaction||interaction.centre?.x!==-4||interaction.centre?.z!==-19.5||interaction.radius!==1.5||avatar?.x!==-4||avatar?.z!==20) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  if(!box||box.minX!==-60||box.maxX!==60||box.minY!==0.5||box.maxY!==40||box.minZ!==-45||box.maxZ!==45) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  if(m.layout.aircraft?.length!==3||!m.layout.aircraft.every((row:any,i:number)=>row?.caseId===specs[i][0])) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  for(let i=0;i<3;i++){
    const s=specs[i],model=m.aircraft.models[i],layout=m.layout.aircraft[i],p=s[2].toUpperCase(),caseId=s[0];
    if(!model||model.caseId!==caseId||model.category!==s[1]||model.path!==undefined||model.type!==undefined||model.synthetic!==true||model.realAircraftType!==false||model.realPerson!==false||model.brandMarks!==false) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
    if(layout.centre?.x!==s[3]||layout.centre?.z!==s[4]||layout.dimensions?.length!==s[5]||layout.dimensions?.span!==s[6]||layout.dimensions?.height!==s[7]||layout.footprint?.minX!==s[8]||layout.footprint?.maxX!==s[9]||layout.footprint?.minZ!==s[10]||layout.footprint?.maxZ!==s[11]||layout.marker?.x!==s[12]||layout.marker?.z!==s[13]) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
    const nodes=model.nodes,ids=[`SYN-CP-${p}-AIR-001`,`SYN-CP-${p}-ENG-001`,`SYN-CP-${p}-CMP-001`,`SYN-CP-${p}-CMP-002`],nodeNames=[`Aircraft${p}`,`Engine${p}`,`Component${p}1`,`Component${p}2`],keys=['aircraft','engine','component1','component2'],kinds=['aircraft','engine','component','component'];
    if(!nodes||!Array.isArray(nodes.objectIds)||nodes.objectIds.join('|')!==ids.join('|')) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
    for(let j=0;j<4;j++){if(nodes[keys[j]]!==nodeNames[j]||seenIds.has(ids[j])||seenNodes.has(nodeNames[j])||!m.objectIds.includes(ids[j])||!HANGAR_ACTUAL_OBJECT_IDS.includes(ids[j] as any)) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};seenIds.add(ids[j]);seenNodes.add(nodeNames[j]);descriptor.push({objectId:ids[j],caseId,kind:kinds[j],node:nodeNames[j]});}
  }
  const rackManifest=m.rack; if(!rackManifest||rackManifest.path!==undefined||rackManifest.type!==undefined||rackManifest.synthetic!==true||rackManifest.realAircraftType!==false||rackManifest.realPerson!==false||rackManifest.brandMarks!==false) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR}; const rackNodes=rackManifest.units;if(!Array.isArray(rackNodes)||rackNodes.length!==5) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  const approvedRack=[['technical','RackUnitTechnical'],['maintenance','RackUnitMaintenance'],['finance','RackUnitFinance'],['records','RackUnitRecords'],['contracts','RackUnitContracts']] as const;
  const avatarNode=m.avatar?.hardHat;if(!avatarNode||avatarNode.node!=='HardHat'||avatarNode.color!==hangarSceneColors.hardHat||m.avatar.path!==undefined||m.avatar.type!==undefined||m.avatar.synthetic!==true||m.avatar.realPerson!==false||m.avatar.brandMarks!==false) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  for(let i=0;i<approvedRack.length;i++){const unit=rackNodes[i];const approved=approvedRack[i];if(!unit||unit.source!==approved[0]||unit.node!==approved[1]||seenNodes.has(unit.node))return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};seenNodes.add(unit.node);}
  if(seenIds.size!==12||descriptor.length!==12||new Set(m.objectIds).size!==12||m.objectIds.some((id:any)=>!HANGAR_ACTUAL_OBJECT_IDS.includes(id))) return {valid:false,error:HANGAR_DESCRIPTOR_ERROR};
  return {valid:true,descriptor};
}
export const buildHangarSceneDescriptor=validateHangarAircraftDescriptor;
export const buildHangarAircraftViewModel=projectHangarAircraftViewModel; export const projectHangarAircraftViewModelList=projectHangarAircraftViewModel;
export function activateHangarSceneObject(descriptorInput:unknown,objectId:unknown,handler?:((objectId:string,entry:{objectId:string;caseId:string;kind:string;node:string})=>unknown)|null):{activated:boolean;value?:unknown;error?:string}{if(typeof objectId!== 'string'||typeof handler!== 'function')return{activated:false,error:HANGAR_DESCRIPTOR_ERROR};const result=validateHangarAircraftDescriptor(descriptorInput);if(!result.valid||!result.descriptor)return{activated:false,error:HANGAR_DESCRIPTOR_ERROR};const entry=result.descriptor.find(item=>item.objectId===objectId);if(!entry)return{activated:false,error:HANGAR_DESCRIPTOR_ERROR};return{activated:true,value:handler(objectId,entry)};}
export const validateHangarSceneDescriptor=validateHangarAircraftDescriptor; export const validateHangarAircraftSceneDescriptor=validateHangarAircraftDescriptor; export const buildHangarAircraftSceneDescriptor=validateHangarAircraftDescriptor; export const projectHangarAircraftScene=projectHangarAircraftViewModel;
