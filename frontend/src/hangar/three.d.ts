declare module 'three' {
  export type Vector3Tuple = [number, number, number];
  export class Vector3 { constructor(x?:number,y?:number,z?:number); x:number;y:number;z:number; set(x:number,y:number,z?:number):this; copy(v:Vector3):this; toArray():Vector3Tuple; }
  export class Object3D { name:string; visible:boolean; position:Vector3; scale:Vector3; userData:Record<string,any>; add(...objects:Object3D[]):this; traverse(callback:(object:Object3D)=>void):void; updateMatrixWorld(force?:boolean):void; }
  export class Group extends Object3D { constructor(); }
  export class Mesh extends Object3D { constructor(geometry?:any,material?:any); geometry:any; material:any; rotation:{x:number;y:number;z:number}; }
  export class Material { type:string; map:any; visible:boolean; }
  export class MeshStandardMaterial extends Material { constructor(options?:Record<string,any>); color:{getHexString():string}; roughness:number; metalness:number; transparent:boolean; opacity:number; }
  export class MeshBasicMaterial extends Material { constructor(options?:Record<string,any>); color?:{getHexString():string}; transparent:boolean; opacity:number; }
  export class BoxGeometry { constructor(width:number,height:number,depth:number); }
  export class CapsuleGeometry { constructor(radius:number,length:number,capSegments:number,radialSegments:number); boundingSphere?:{radius:number}; }
  export class CylinderGeometry { constructor(radiusTop:number,radiusBottom:number,height:number,radialSegments:number); }
  export class SphereGeometry { constructor(radius:number,widthSegments:number,heightSegments:number); }
  export class CircleGeometry { constructor(radius:number,segments:number); }
  export class Box3 { setFromObject(object:Object3D):this; getSize(target:Vector3):Vector3; min:{y:number}; }
}
