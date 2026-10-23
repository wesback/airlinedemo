declare module 'three' {
  export type Vector3Tuple = [number, number, number];
  export class Vector2 { constructor(x?:number,y?:number); x:number;y:number; }
  export class Vector3 { constructor(x?:number,y?:number,z?:number); x:number;y:number;z:number; set(x:number,y:number,z?:number):this; copy(v:Vector3):this; toArray():Vector3Tuple; clone():Vector3; distanceTo(v:Vector3):number; sub(v:Vector3):this; dot(v:Vector3):number; project(camera:PerspectiveCamera):this; }
  export class Object3D { name:string; visible:boolean; position:Vector3; scale:Vector3; userData:Record<string,any>; parent:Object3D|null; add(...objects:Object3D[]):this; traverse(callback:(object:Object3D)=>void):void; updateMatrixWorld(force?:boolean):void; }
  export class Group extends Object3D { constructor(); getObjectByName(name:string):Object3D|null; }
  export class PerspectiveCamera { constructor(fov?:number,aspect?:number,near?:number,far?:number); fov:number; aspect:number; near:number; far:number; position:Vector3; lookAt(x:number,y:number,z:number):void; updateProjectionMatrix():void; updateMatrixWorld(force?:boolean):void; getWorldDirection(target:Vector3):Vector3; isPerspectiveCamera:boolean; }
  export class Raycaster { setFromCamera(coords:Vector2,camera:PerspectiveCamera):void; intersectObject(object:Object3D,recursive?:boolean):Array<{distance:number;object:Object3D}>; }
  export class Mesh extends Object3D { constructor(geometry?:any,material?:any); geometry:any; material:any; rotation:{x:number;y:number;z:number}; }
  export class Material { type:string; map:any; visible:boolean; }
  export class MeshStandardMaterial extends Material { constructor(options?:Record<string,any>); color:{getHexString():string}; roughness:number; metalness:number; transparent:boolean; opacity:number; }
  export class MeshBasicMaterial extends Material { constructor(options?:Record<string,any>); color?:{getHexString():string}; transparent:boolean; opacity:number; }
  export class BoxGeometry { constructor(width:number,height:number,depth:number); parameters:{width:number;height:number;depth:number}; }
  export class CapsuleGeometry { constructor(radius:number,length:number,capSegments:number,radialSegments:number); boundingSphere?:{radius:number}; }
  export class CylinderGeometry { constructor(radiusTop:number,radiusBottom:number,height:number,radialSegments:number); }
  export class SphereGeometry { constructor(radius:number,widthSegments?:number,heightSegments?:number,phiStart?:number,phiLength?:number,thetaStart?:number,thetaLength?:number); parameters:{radius:number}; }
  export class CircleGeometry { constructor(radius:number,segments:number); }
  export class Curve<T> { getPoint(t:number,target?:T):T; }
  export class CatmullRomCurve3 extends Curve<Vector3> { constructor(points:Vector3[],closed?:boolean,curveType?:string,tension?:number); }
  export class TubeGeometry { constructor(curve:Curve<Vector3>,tubularSegments?:number,radius?:number,radialSegments?:number,closed?:boolean); parameters:{path:Curve<Vector3>; tubularSegments:number; radius:number; radialSegments:number; closed:boolean}; }
  export class TorusGeometry { constructor(radius:number,tube:number,radialSegments:number,tubularSegments:number,arc?:number); parameters:{radius:number;tube:number;radialSegments:number;tubularSegments:number;arc:number}; }
  export class Box3 { setFromObject(object:Object3D):this; getSize(target:Vector3):Vector3; getCenter(target:Vector3):Vector3; min:{y:number}; }
}
