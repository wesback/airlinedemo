export const HANGAR_ROUTE_GRID_SIZE = 0.25;
export const HANGAR_GRID_SIZE = HANGAR_ROUTE_GRID_SIZE;
export const HANGAR_AVATAR_RADIUS = 0.3;
export const HANGAR_ROUTE_ARRIVAL_THRESHOLD = 1.5;

export type HangarRoutePoint = { x: number; z: number };
export type HangarRouteFootprint = HangarRoutePoint & {
  width?: number; depth?: number; height?: number; minX?: number; maxX?: number; minZ?: number; maxZ?: number; radius?: number;
};
export type HangarRouteConfig = {
  walkablePolygon?: HangarRoutePoint[]; floorPolygon?: HangarRoutePoint[]; walkableFloor?: HangarRoutePoint[]; walkable?: HangarRoutePoint[];
  footprints?: HangarRouteFootprint[]; aircraftFootprints?: HangarRouteFootprint[]; rackFootprints?: HangarRouteFootprint[];
  avatarRadius?: number; arrivalThreshold?: number; gridSize?: number; marker?: HangarRoutePoint; avatar?: HangarRoutePoint;
  transport?: (request: unknown) => unknown; onSelect?: (aircraftId: string) => unknown;
};
export type HangarRouteDirection = 'ahead' | 'behind' | 'left' | 'right';
export type HangarRouteResult = {
  status: 'route' | 'unreachable' | 'arrival' | 'cleared'; route: HangarRoutePoint[]; points: HangarRoutePoint[];
  distance: number; arrival: boolean; reachable: boolean; message: string | null; line: boolean;
  direction: HangarRouteDirection | null; distanceText: string | null;
};

const round=(n:number)=>Math.round(n*1e12)/1e12;
const gridSnap=(n:number,g:number)=>round(Math.round(n/g)*g);
const distance=(a:HangarRoutePoint,b:HangarRoutePoint)=>Math.hypot(a.x-b.x,a.z-b.z);
function inPolygon(p:HangarRoutePoint,poly:HangarRoutePoint[]) {let inside=false;for(let i=0,j=poly.length-1;i<poly.length;j=i++){const a=poly[i],b=poly[j];if(((a.z>p.z)!==(b.z>p.z))&&p.x<(b.x-a.x)*(p.z-a.z)/(b.z-a.z||1e-12)+a.x)inside=!inside;}return inside;}
function pointSegment(p:HangarRoutePoint,a:HangarRoutePoint,b:HangarRoutePoint) {const dx=b.x-a.x,dz=b.z-a.z,ls=dx*dx+dz*dz;if(!ls)return distance(p,a);const t=Math.max(0,Math.min(1,((p.x-a.x)*dx+(p.z-a.z)*dz)/ls));return distance(p,{x:a.x+t*dx,z:a.z+t*dz});}
function footprintRect(f:HangarRouteFootprint) {
  if(typeof f.minX==='number'&&typeof f.maxX==='number'&&typeof f.minZ==='number'&&typeof f.maxZ==='number')return {minX:f.minX,maxX:f.maxX,minZ:f.minZ,maxZ:f.maxZ};
  if(typeof f.width==='number'&&(typeof f.depth==='number'||typeof f.height==='number')){const d=typeof f.depth==='number'?f.depth:f.height!;return {minX:f.x-f.width/2,maxX:f.x+f.width/2,minZ:f.z-d/2,maxZ:f.z+d/2};}
  return null;
}
function inFootprint(p:HangarRoutePoint,f:HangarRouteFootprint,r:number) {const q=footprintRect(f);if(q)return p.x>=q.minX-r&&p.x<=q.maxX+r&&p.z>=q.minZ-r&&p.z<=q.maxZ+r;return distance(p,f)<=((typeof f.radius==='number'?f.radius:0)+r);}
function segmentsIntersect(a:HangarRoutePoint,b:HangarRoutePoint,c:HangarRoutePoint,d:HangarRoutePoint) {
  const cross=(p:HangarRoutePoint,q:HangarRoutePoint,r:HangarRoutePoint)=>(q.x-p.x)*(r.z-p.z)-(q.z-p.z)*(r.x-p.x);
  const abC=cross(a,b,c),abD=cross(a,b,d),cdA=cross(c,d,a),cdB=cross(c,d,b);
  if(((abC>1e-9&&abD<-1e-9)||(abC<-1e-9&&abD>1e-9))&&((cdA>1e-9&&cdB<-1e-9)||(cdA<-1e-9&&cdB>1e-9)))return true;
  return false;
}
function segmentCrosses(a:HangarRoutePoint,b:HangarRoutePoint,f:HangarRouteFootprint,r:number) {
  const q=footprintRect(f);if(q){const minX=q.minX-r,maxX=q.maxX+r,minZ=q.minZ-r,maxZ=q.maxZ+r;if(inFootprint(a,f,r)||inFootprint(b,f,r))return true;const corners=[{x:minX,z:minZ},{x:maxX,z:minZ},{x:maxX,z:maxZ},{x:minX,z:maxZ}];for(let i=0;i<4;i++)if(segmentsIntersect(a,b,corners[i],corners[(i+1)%4])||pointSegment(corners[i],a,b)<=1e-9)return true;return false;}return pointSegment(f,a,b)<=((typeof f.radius==='number'?f.radius:0)+r);}


export function getHangarRouteLiveDirection(avatar:HangarRoutePoint,marker:HangarRoutePoint,azimuthDegrees=0) {
  const dx=marker.x-avatar.x,dz=marker.z-avatar.z,d=distance(avatar,marker);
  const distanceText=`${d.toFixed(2)} metres`;
  if(d<1e-12)return {direction:'ahead' as const,distance:d,distanceText,liveText:`ahead, ${distanceText}`};
  const angle=(Number.isFinite(azimuthDegrees)?azimuthDegrees:0)*Math.PI/180;const forward={x:Math.sin(angle),z:-Math.cos(angle)},right={x:Math.cos(angle),z:Math.sin(angle)};const nx=dx/d,nz=dz/d;const ahead=nx*forward.x+nz*forward.z,rightAmount=nx*right.x+nz*right.z;
  const direction:HangarRouteDirection=ahead>=-0.2?(rightAmount>=0?'right':'left'):(Math.abs(rightAmount)>Math.abs(ahead)?(rightAmount>=0?'right':'left'):'behind');return {direction,distance:d,distanceText,liveText:`${direction}, ${distanceText}`};
}
export const calculateHangarRouteDirection=getHangarRouteLiveDirection;export const getHangarLiveDirection=getHangarRouteLiveDirection;

export function planHangarRoute(avatar:HangarRoutePoint|null|undefined,marker:HangarRoutePoint|null|undefined,config:HangarRouteConfig={}):HangarRouteResult {
  const unreachable=(message='No route: marker is unreachable'):HangarRouteResult=>({status:'unreachable',route:[],points:[],distance:0,arrival:false,reachable:false,message,line:false,direction:null,distanceText:null});
  if(!avatar||!marker||!Number.isFinite(avatar.x)||!Number.isFinite(avatar.z)||!Number.isFinite(marker.x)||!Number.isFinite(marker.z))return unreachable('No route: marker is missing');
  const threshold=Number.isFinite(config.arrivalThreshold)?config.arrivalThreshold!:HANGAR_ROUTE_ARRIVAL_THRESHOLD;if(distance(avatar,marker)<=threshold)return {status:'arrival',route:[],points:[],distance:distance(avatar,marker),arrival:true,reachable:true,message:'Arrival',line:false,direction:null,distanceText:null};
  const polygon=config.walkablePolygon??config.floorPolygon??config.walkableFloor??config.walkable;if(!polygon||polygon.length<3)return unreachable('No route: walkable floor is missing');
  const grid=Number.isFinite(config.gridSize)?config.gridSize!:HANGAR_ROUTE_GRID_SIZE,radius=Number.isFinite(config.avatarRadius)?config.avatarRadius!:HANGAR_AVATAR_RADIUS;const footprints=[...(config.aircraftFootprints??[]),...(config.rackFootprints??[]),...(config.footprints??[])];
  const blocked=(p:HangarRoutePoint)=>!inPolygon(p,polygon)||footprints.some(f=>inFootprint(p,f,radius));const start={x:gridSnap(avatar.x,grid),z:gridSnap(avatar.z,grid)},goal={x:gridSnap(marker.x,grid),z:gridSnap(marker.z,grid)};
  if(blocked(start)||blocked(goal))return unreachable();
  let minX=Infinity,maxX=-Infinity,minZ=Infinity,maxZ=-Infinity;for(const p of polygon){minX=Math.min(minX,p.x);maxX=Math.max(maxX,p.x);minZ=Math.min(minZ,p.z);maxZ=Math.max(maxZ,p.z);}minX=gridSnap(minX,grid);maxX=gridSnap(maxX,grid);minZ=gridSnap(minZ,grid);maxZ=gridSnap(maxZ,grid);
  const key=(p:HangarRoutePoint)=>`${p.x},${p.z}`;const startKey=key(start),goalKey=key(goal);const open=new Map([[startKey,{p:start,g:0,f:distance(start,goal)}]]),came=new Map<string,string>(),cost=new Map([[startKey,0]]);
  while(open.size){let currentKey='',current:any=null;for(const [k,v] of open)if(!current||v.f<current.f){current=v;currentKey=k;}open.delete(currentKey);if(currentKey===goalKey)break;const dirs=[[1,0],[0,1],[-1,0],[0,-1],[1,1],[1,-1],[-1,1],[-1,-1]];for(const [dx,dz] of dirs){const next={x:gridSnap(current.p.x+dx*grid,grid),z:gridSnap(current.p.z+dz*grid,grid)};if(next.x<minX||next.x>maxX||next.z<minZ||next.z>maxZ||blocked(next))continue;if(footprints.some(f=>segmentCrosses(current.p,next,f,radius)))continue;const nk=key(next),ng=current.g+(dx&&dz?Math.SQRT2:1)*grid;if(ng>= (cost.get(nk)??Infinity))continue;came.set(nk,currentKey);cost.set(nk,ng);open.set(nk,{p:next,g:ng,f:ng+distance(next,goal)});}}
  if(startKey!==goalKey&&!came.has(goalKey))return unreachable();const path:HangarRoutePoint[]=[];let k=goalKey;if(startKey!==goalKey){path.unshift(goal);while(k!==startKey){const parent=came.get(k);if(!parent)break;const a=parent.split(',').map(Number);path.unshift({x:a[0],z:a[1]});k=parent;}if(path[0].x!==start.x||path[0].z!==start.z)path.unshift(start);}else path.push(start);
  if(path.length>1&&footprints.some(f=>path.some((p,i)=>i>0&&segmentCrosses(path[i-1],p,f,radius))))return unreachable();
  const route=path.map((p,i)=>i===path.length-1?{x:marker.x,z:marker.z}:i===0?avatar:p);
  if(footprints.some(f=>route.some((p,i)=>i>0&&segmentCrosses(route[i-1],p,f,radius))))return unreachable();
  const routeDistance=route.reduce((sum,p,i)=>sum+(i?distance(route[i-1],p):0),0);const live=getHangarRouteLiveDirection(avatar,marker);
  return {status:'route',route,points:route,distance:routeDistance,arrival:false,reachable:true,message:null,line:true,direction:live.direction,distanceText:live.distanceText};
}
export const planHangarShortestPath=planHangarRoute;export const buildHangarRoute=planHangarRoute;export const getHangarRouteDistance=getHangarRouteLiveDirection;

export class HangarRoutePlanner {
  private marker:HangarRoutePoint|null=null;private markerKey='';private route:HangarRouteResult={status:'cleared',route:[],points:[],distance:0,arrival:false,reachable:false,message:null,line:false,direction:null,distanceText:null};private arrival=false;private selectedAircraft:string|null=null;
  constructor(private readonly config:HangarRouteConfig={},private readonly selector=(config.onSelect??((_id:string)=>{})),private readonly _transport=config.transport){}
  confirm(marker:HangarRoutePoint|null,avatar=this.config.avatar,aircraftId='selected-aircraft'):HangarRouteResult{if(!marker)return this.clear();const key=`${marker.x},${marker.z}`;if(key===this.markerKey)return this.route;if(distance(avatar??{x:0,z:0},marker)<= (this.config.arrivalThreshold??HANGAR_ROUTE_ARRIVAL_THRESHOLD)&&(!avatar))return this.clear();this.marker=marker;this.markerKey=key;this.arrival=false;this.selectedAircraft=aircraftId;this.route=planHangarRoute(avatar,marker,this.config);if(this.route.arrival&&!this.arrival){this.arrival=true;this.selector(aircraftId);}return this.route;}
  confirmEntry(marker:HangarRoutePoint|null,aircraftId='selected-aircraft',avatar=this.config.avatar):HangarRouteResult{return this.confirm(marker,avatar,aircraftId);}
  recompute(avatar:HangarRoutePoint):HangarRouteResult{if(!this.marker)return this.clear();const threshold=this.config.arrivalThreshold??HANGAR_ROUTE_ARRIVAL_THRESHOLD;if(distance(avatar,this.marker)<=threshold&&!this.arrival){this.arrival=true;const id=this.selectedAircraft??'selected-aircraft';this.route={status:'arrival',route:[],points:[],distance:distance(avatar,this.marker),arrival:true,reachable:true,message:'Arrival',line:false,direction:null,distanceText:null};this.selector(id);return this.route;}if(distance(avatar,this.marker)<=threshold)return this.route;if(this.arrival)return this.route;this.route=planHangarRoute(avatar,this.marker,this.config);return this.route;}
  acceptedStep(avatar:HangarRoutePoint):HangarRouteResult{return this.recompute(avatar);}afterStep=this.acceptedStep;
  step(accepted:boolean,avatar:HangarRoutePoint):HangarRouteResult{return accepted?this.recompute(avatar):this.route;}escape():HangarRouteResult{return this.clear();}
  clear():HangarRouteResult{this.marker=null;this.markerKey='';this.arrival=false;this.selectedAircraft=null;this.route={status:'cleared',route:[],points:[],distance:0,arrival:false,reachable:false,message:null,line:false,direction:null,distanceText:null};return this.route;}getRoute(){return {...this.route,route:[...this.route.route],points:[...this.route.points]}}getState(){return this.getRoute();}
}
export function createHangarRoutePlanner(config:HangarRouteConfig={},onSelect?:((aircraftId:string)=>unknown),transport?:((request:unknown)=>unknown)){return new HangarRoutePlanner({...config,onSelect:onSelect??config.onSelect},onSelect??config.onSelect,transport??config.transport);}
