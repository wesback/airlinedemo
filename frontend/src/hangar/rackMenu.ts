import { createElement, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { projectHangarSourceSummary, SOURCE_UNAVAILABLE_TEXT, type CaseListEntry, type HangarSourceSummaryEntry, type SourceSummaryReader } from './sourceSummary';

export const HANGAR_RACK_MENU_INVALID_ENTRY = 'Unknown or out-of-scope identifier';
export const HANGAR_RACK_MENU_OPEN_LABEL = 'Open data rack';
export const DATA_RACK_MENU_OPEN_LABEL = HANGAR_RACK_MENU_OPEN_LABEL;
export type HangarRackMenuEntry = HangarSourceSummaryEntry;
export type HangarRackSelectionHandler = (identifier: string) => unknown;
export type HangarRackMenuOptions = {
  entries?: HangarRackMenuEntry[]; onOpen?:()=>unknown; onClose?:()=>unknown;
  onSelect?:HangarRackSelectionHandler; onConfirm?:HangarRackSelectionHandler;
  onFocusWalk?:()=>unknown; walkRegion?:()=>unknown; transport?:((request:unknown)=>unknown);
};

export class HangarRackMenuController {
  private entries: HangarRackMenuEntry[]=[]; private opened=false; private highlighted=-1; private inside=false; private zoneLatched=false; private error='';
  private readonly options: HangarRackMenuOptions;
  constructor(entries:HangarRackMenuEntry[]=[],options:HangarRackMenuOptions={}){this.entries=entries;this.options={...options,...(entries.length?{}:{entries})};}
  getState(){return {open:this.opened,highlightedIndex:this.highlighted,highlightedCaseId:this.highlighted>=0?this.entries[this.highlighted]?.caseId:null,opened:this.opened,inside:this.inside,error:this.error};}
  getEntries(){return this.entries.map(e=>({...e,sources:e.sources?.map(s=>({...s}))??null}));}
  setEntries(entries:HangarRackMenuEntry[]){this.entries=entries;this.highlighted=entries.length?0:-1;this.error='';}
  open(){if(this.opened)return false;this.opened=true;if(this.highlighted<0&&this.entries.length)this.highlighted=0;this.options.onOpen?.();return true;}
  explicitOpen(){return this.open();}
  openFromControl(){return this.open();}
  close(reason:'escape'|'confirm'|'action'='escape'){if(!this.opened)return false;this.opened=false;this.highlighted=-1;this.options.onClose?.();if(reason==='escape')this.options.onFocusWalk?.();return true;}
  escape(){return this.close('escape');}
  stepInside(inside:boolean){if(inside){if(!this.inside&&!this.zoneLatched&&!this.opened)this.open();this.inside=true;this.zoneLatched=true;return;}this.inside=false;this.zoneLatched=false;}
  updateZone(inside:boolean){this.stepInside(inside);}
  enterZone(){this.stepInside(true);}
  leaveZone(){this.stepInside(false);}
  activateOpenControl(){this.open();}
  move(direction:-1|1){if(!this.opened||!this.entries.length)return null;if(this.highlighted<0)this.highlighted=0;else this.highlighted=(this.highlighted+direction+this.entries.length)%this.entries.length;this.error='';return this.entries[this.highlighted];}
  up(){return this.move(-1);} down(){return this.move(1);}
  first(){if(!this.opened||!this.entries.length)return null;this.highlighted=0;return this.entries[0];}
  last(){if(!this.opened||!this.entries.length)return null;this.highlighted=this.entries.length-1;return this.entries[this.highlighted];}
  home(){return this.first();} end(){return this.last();}
  confirm(identifier:unknown){if(!this.opened||typeof identifier!=='string')return false;const entry=this.entries.find(e=>e.caseId===identifier);if(!entry||entry.unavailable){this.error=HANGAR_RACK_MENU_INVALID_ENTRY;return false;}this.close('confirm');const handler=this.options.onConfirm??this.options.onSelect;if(typeof handler==='function')handler(entry.caseId);return true;}
  confirmHighlighted(){if(!this.opened||this.highlighted<0||!this.entries[this.highlighted])return false;return this.confirm(this.entries[this.highlighted].caseId);}
  key(key:unknown,repeat=false){if(!this.opened)return false;if(repeat&&key==='Enter')return true;if(key==='ArrowUp'||key==='Up')this.move(-1);else if(key==='ArrowDown'||key==='Down')this.move(1);else if(key==='Home')this.first();else if(key==='End')this.last();else if(key==='Enter')this.confirmHighlighted();else if(key==='Escape')this.escape();else return false;return true;}
  handleKeydown(event:{key?:unknown;repeat?:unknown;preventDefault?:()=>void}){const key=typeof event.key==='string'?event.key:'';if(!['ArrowUp','ArrowDown','Home','End','Enter','Escape'].includes(key))return null;event.preventDefault?.();this.key(key,event.repeat===true);return this.getState();}
}
export const createHangarRackMenuController=(entries:HangarRackMenuEntry[]=[],options?:HangarRackMenuOptions)=>new HangarRackMenuController(entries,options);
export const buildHangarRackMenu=(entries:HangarRackMenuEntry[]=[],options?:HangarRackMenuOptions)=>createHangarRackMenuController(entries,options);
export function isHangarRackMenuEntry(entries:HangarRackMenuEntry[],identifier:unknown):HangarRackMenuEntry|null {return typeof identifier==='string'?entries.find(e=>!e.unavailable&&e.caseId===identifier)??null:null;}
export function confirmHangarRackMenuEntry(entries: HangarRackMenuEntry[],identifier:unknown,handler?:HangarRackSelectionHandler):boolean {const entry=isHangarRackMenuEntry(entries,identifier);if(!entry)return false;if(typeof handler==='function')handler(entry.caseId);return true;}
export { SOURCE_UNAVAILABLE_TEXT };

export type HangarRackMenuProps={entries?:HangarRackMenuEntry[]|null;caseList?:unknown;scope?:unknown;summaryReader?:SourceSummaryReader;onSelect?:HangarRackSelectionHandler;onConfirm?:HangarRackSelectionHandler;transport?:((request:unknown)=>unknown);walkRegionLabel?:string};
export function HangarRackMenu({entries:initialEntries,caseList,scope=[],summaryReader,onSelect,onConfirm,transport,walkRegionLabel='Walk region'}:HangarRackMenuProps){
  const [entries,setEntries]=useState<HangarRackMenuEntry[]>(initialEntries??[]);const [loaded,setLoaded]=useState(initialEntries!==undefined);const menuRef=useRef<HTMLElement|null>(null);const walkRef=useRef<HTMLElement|null>(null);
  const controllerRef=useRef<HangarRackMenuController|null>(null);const [,render]=useState(0);const onSelection=onConfirm??onSelect;
  if(!controllerRef.current)controllerRef.current=new HangarRackMenuController(entries,{onSelect:onSelection,onConfirm:onSelection,onFocusWalk:()=>walkRef.current?.focus?.()});const controller=controllerRef.current;
  useEffect(()=>{controller.setEntries(entries);render(v=>v+1)},[entries]);
  useEffect(()=>{if(!loaded&&(Array.isArray(caseList)||(caseList&&typeof caseList==='object')))void projectHangarSourceSummary(caseList,scope,summaryReader).then(value=>{setEntries(value);setLoaded(true);});else if(!loaded)setLoaded(true);},[caseList,scope,summaryReader,loaded]);
  useEffect(()=>{const state=controller.getState();if(state.open)menuRef.current?.focus?.();else if(document.activeElement===menuRef.current||document.activeElement?.closest('[data-hangar-rack-menu]'))walkRef.current?.focus?.();},[controller.getState().open]);
  const state=controller.getState();const open=()=>{controller.open();render(v=>v+1)};  const key=(event:ReactKeyboardEvent)=>{controller.handleKeydown(event);render(v=>v+1);};
  return createElement('div',null,
    createElement('section',{ref:(node:HTMLElement|null)=>{walkRef.current=node},'data-hangar-walk-region':true,tabIndex:0,'aria-label':walkRegionLabel,onMouseEnter:()=>{controller.enterZone();render(v=>v+1)},onMouseLeave:()=>{controller.leaveZone()},onFocus:()=>{controller.enterZone();render(v=>v+1)}},'Hangar walk region'),
    createElement('button',{type:'button','aria-label':HANGAR_RACK_MENU_OPEN_LABEL,onClick:open},HANGAR_RACK_MENU_OPEN_LABEL),
    state.open?createElement('section',{ref:(node:HTMLElement|null)=>{menuRef.current=node},tabIndex:-1,'aria-label':'Data rack menu','data-hangar-rack-menu':true,role:'menu',onKeyDown:key},
      createElement('h2',null,'Data rack'),entries.map((entry,index)=>createElement('button',{type:'button',role:'menuitem',key:entry.caseId,'aria-label':entry.name,tabIndex:state.highlightedIndex===index?0:-1,'aria-current':state.highlightedIndex===index?'true':undefined,'data-highlighted':state.highlightedIndex===index?'true':'false',onClick:()=>{controller.confirm(entry.caseId);render(v=>v+1)}} ,
        createElement('span',null,`${entry.name} — ${entry.category}`),
        entry.unavailable?createElement('span',null,SOURCE_UNAVAILABLE_TEXT):createElement('span',null,(entry.sources??[]).map(s=>s.text).join('; '))
      )),state.error?createElement('p',{role:'alert'},state.error):null):null
  );
}
export default HangarRackMenu;
export { HangarRackMenu as DataRoomRackMenu, HangarRackMenuController as RackMenuController, createHangarRackMenuController as createRackMenuController };
