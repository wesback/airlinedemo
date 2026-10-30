import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
// @ts-ignore -- Vitest loads node:fs at runtime; this package intentionally has no Node declaration dependency.
import { readFileSync } from 'node:fs';
import HangarSemanticControls, {
  HANGAR_CONTROL_DEFICIENCY_ERROR, HANGAR_FOCUS_STYLE,
  activateHangarSemanticControl, buildHangarSemanticControls, handleHangarSemanticKeydown,
} from '../src/hangar/semanticControls';

const scene=(overrides:Record<string,unknown>={})=>({case:{caseId:'cp-case-a',capabilityLabel:'Rules-based',permission:true,allowedActions:['selection','review'],status:{assessment:'ready',review:'pending'},...((overrides.case as Record<string,unknown>)??{})},objects:[{objectId:'SYN-CP-A-AIR-001',kind:'aircraft',label:'Case A aircraft',caseId:'cp-case-a'}],history:[{sequence:1,label:'Finding recorded',outcome:'complete'}],allowedActions:['selection','review'],...overrides});

afterEach(()=>cleanup());

describe('HangarSemanticControls',()=>{
  it('HangarSemanticControls exposes role names and keyboard activation',async()=>{
    const activate=vi.fn(); const activateJourney=vi.fn(); const descriptor=[{objectId:'SYN-CP-A-AIR-001',caseId:'cp-case-a',kind:'aircraft',activate,handler:activate}];
    const command=vi.fn(); const user=userEvent.setup();
    render(<HangarSemanticControls scene={scene()} descriptor={descriptor} onActivate={activateJourney} onCommand={command}/>);
    const object=screen.getByRole('button',{name:'Case A aircraft'});
    expect(screen.getByRole('button',{name:'Select case'})).toBeTruthy();
    expect(screen.getByRole('button',{name:'Open review'})).toBeTruthy();
    expect(screen.getByRole('button',{name:'Finding recorded'})).toBeTruthy();
    expect(screen.getByRole('status').textContent).toBe('Hangar status: assessment: ready; review: pending');
    await user.click(object); expect(activate).toHaveBeenCalledTimes(1);
    object.focus(); await user.keyboard('{Enter}'); expect(activate).toHaveBeenCalledTimes(2);
    await user.keyboard(' '); expect(activate).toHaveBeenCalledTimes(3);
    const journey=screen.getByRole('button',{name:'Finding recorded'});
    await user.click(journey); expect(activateJourney).toHaveBeenCalledTimes(1);
    journey.focus(); await user.keyboard('{Enter}'); expect(activateJourney).toHaveBeenCalledTimes(2);
    const action=screen.getByRole('button',{name:'Open review'});
    await user.click(action); expect(command).toHaveBeenCalledTimes(1); expect(command).toHaveBeenLastCalledWith('review','review');
    action.focus(); await user.keyboard('{Enter}'); expect(command).toHaveBeenCalledTimes(2); expect(command).toHaveBeenLastCalledWith('review','review');
    expect(HANGAR_FOCUS_STYLE).toContain('outline'); expect(HANGAR_FOCUS_STYLE).not.toContain('none');
  });

  it('HangarSemanticControls rejects a journey step without activation',()=>{
    const objectActivate=vi.fn();
    const descriptor=[{objectId:'SYN-CP-A-AIR-001',caseId:'cp-case-a',kind:'aircraft',activate:objectActivate}];
    const withoutJourneyHandler=buildHangarSemanticControls(scene(), descriptor);
    expect(withoutJourneyHandler.error).toBe(`${HANGAR_CONTROL_DEFICIENCY_ERROR}: pointer-only or unlabelled control`);
    expect(withoutJourneyHandler.controls).toEqual([]);
    expect(objectActivate).not.toHaveBeenCalled();
  });

  it('HangarSemanticControls preserves focus and Tab navigation',async()=>{
    const descriptor=[{objectId:'SYN-CP-A-AIR-001',caseId:'cp-case-a',kind:'aircraft',activate:vi.fn()}];
    const {container}=render(<><input aria-label="Evidence text"/><aside aria-label="Evidence panel"><button>Evidence</button></aside><section data-hangar-camera-region aria-label="Camera controls" tabIndex={0}><input aria-label="Camera text"/></section><HangarSemanticControls scene={scene()} descriptor={descriptor} onActivate={vi.fn()}/></>);
    // CSP forbids inline <style>; the focus rule ships in the hangar stylesheet instead.
    expect(container.querySelector('style')).toBeNull();
    const css=readFileSync('src/hangar/hangarMode.css','utf8');
    const rule=css.match(/\.hangar-semantic-control:focus-visible\s*\{([^}]*)\}/)?.[1]??'';
    expect(rule).toContain('outline: 2px solid'); expect(rule).not.toContain('outline: none');
    const user=userEvent.setup(); const evidenceText=screen.getByLabelText('Evidence text');
    evidenceText.focus(); await user.tab();
    expect(document.activeElement).toBe(screen.getByRole('button',{name:'Evidence'}));
    const cameraRegion=screen.getByRole('region',{name:'Camera controls'});
    cameraRegion.focus(); await user.tab();
    expect(document.activeElement).toBe(screen.getByLabelText('Camera text'));
    await user.tab(); expect(document.activeElement).toBe(screen.getByRole('button',{name:'Case A aircraft'}));
    const control=screen.getByRole('button',{name:'Case A aircraft'});
    control.focus(); await user.tab(); expect(document.activeElement).not.toBe(control);
    const prevented=vi.fn(); handleHangarSemanticKeydown({id:'x',role:'button',name:'X',kind:'object',activate:vi.fn()}, {key:'Tab',preventDefault:prevented});
    expect(prevented).not.toHaveBeenCalled();
  });

  it('HangarSemanticControls enforces projected permissions only',()=>{
    const command=vi.fn(); const falsePermission=scene({case:{caseId:'cp-case-a',capabilityLabel:'Rules-based',permission:false,allowedActions:['reset'],status:{assessment:'ready'}},allowedActions:['reset']});
    const {rerender}=render(<HangarSemanticControls scene={falsePermission} onCommand={command}/>);
    expect(screen.queryByRole('button',{name:'Reset blocked variant'})).toBeNull(); expect(command).not.toHaveBeenCalled();
    const resetOnly=scene({case:{caseId:'cp-case-a',capabilityLabel:'Rules-based',permission:{canResetBlockedVariant:true},allowedActions:['selection','review','reset'],status:{assessment:'ready'}},allowedActions:['selection','review','reset']});
    rerender(<HangarSemanticControls scene={resetOnly} onActivate={vi.fn()} onCommand={command}/>);
    expect(screen.getByRole('button',{name:'Reset blocked variant'})).toBeTruthy();
    expect(screen.queryByRole('button',{name:'Select case'})).toBeNull();
    expect(screen.queryByRole('button',{name:'Open review'})).toBeNull();
    rerender(<HangarSemanticControls scene={scene({case:{caseId:'cp-case-a',capabilityLabel:'Unknown',permission:true,allowedActions:['reset','selection']},allowedActions:['reset','selection']})} onCommand={command}/>);
    expect(screen.queryByRole('button',{name:'Reset blocked variant'})).toBeNull();     expect(screen.queryByRole('button',{name:'Select case'})).toBeNull(); expect(command).not.toHaveBeenCalled();
    const unknown=buildHangarSemanticControls(scene({case:{caseId:'cp-case-a',capabilityLabel:'Rules-based',permission:true,allowedActions:['unknown-capability'],status:{assessment:'ready'}},allowedActions:['unknown-capability']}));
    expect(unknown.controls.filter((c)=>c.kind==='action')).toEqual([]);
    expect(activateHangarSemanticControl({id:'missing',role:'button',name:'Missing',kind:'action'})).toBe(`${HANGAR_CONTROL_DEFICIENCY_ERROR}: control has no activation handler`);
  });
});
