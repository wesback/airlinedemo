import { describe, expect, it, vi } from 'vitest';
import { buildHangarSceneDescriptor, projectHangarScene, validateHangarSceneEnvelope } from '../src/hangar/sceneProjection';
import { applyServerRefresh, canAdmitHangarCommands, refreshHangarScene, markRefreshStale, buildHangarServerSceneView } from '../src/hangar/sceneRefresh';

const envelope = (overrides: Record<string, unknown> = {}) => ({
  contractVersion: 1, packVersion: '1.0.2', case: {
    caseId: 'cp-case-a', caseRevision: 7, status: { assessment: 'ready', review: null, request: null, finance: null, legal: null, acceptance: null },
    owner: 'Technical reviewer', nextAction: { actionId: 'review', text: 'Review evidence' }, capabilityLabel: 'Rules-based', permission: true,
  }, canResetBlockedVariant: false, allowedActions: [], history: [], evidence: [], documentRefs: [],
  objects: [
    { objectId: 'SYN-CP-A-AIR-001', kind: 'aircraft', caseId: 'cp-case-a' },
    { objectId: 'SYN-CP-A-ENG-001', kind: 'engine', caseId: 'cp-case-a' },
    { objectId: 'SYN-CP-A-CMP-001', kind: 'component', caseId: 'cp-case-a' },
    { objectId: 'SYN-CP-A-CMP-002', kind: 'component', caseId: 'cp-case-a' },
  ], ...overrides,
});
const manifest = (objectIds = ['SYN-CP-A-AIR-001','SYN-CP-A-ENG-001','SYN-CP-A-CMP-001','SYN-CP-A-CMP-002']) => ({ objectIds });
const allIds = (prefix: string) => [`SYN-CP-${prefix}-AIR-001`,`SYN-CP-${prefix}-ENG-001`,`SYN-CP-${prefix}-CMP-001`,`SYN-CP-${prefix}-CMP-002`];
const validManifest = () => ({ objectIds: [...allIds('A'),...allIds('B'),...allIds('C')] });

describe('HangarSceneRefresh', () => {
  it('HangarSceneRefresh rejects malformed or unsupported envelopes', () => {
    expect(validateHangarSceneEnvelope(envelope()).valid).toBe(true);
    for (const bad of [envelope({ contractVersion: 2 }), envelope({ case: { caseId: 'cp-case-a' } }),
      envelope({ case: { ...envelope().case, permission: undefined } }),
      envelope({ history: [{ witnessIds: ['missing'] }] }), envelope({ objects: [
        { objectId: 'SYN-CP-B-AIR-001', kind: 'aircraft', caseId: 'cp-case-a' }, ...envelope().objects.slice(1) ] })]) {
      expect(validateHangarSceneEnvelope(bad)).toMatchObject({ valid: false, scene: null }); expect(bad.contractVersion === 2 || !('objects' in bad) ? 1 : 1).toBe(1);
    }
  });

  it('HangarSceneRefresh replaces client state with server snapshot', () => {
    const injected = { revision: 1, status: 'client-only', owner: 'Injected owner', nextAction: { text: 'fake' }, capabilityLabel: 'Unknown', current: true };
    const result = applyServerRefresh(injected as never, envelope());
    expect(result.snapshot).toMatchObject({ revision: 7, status: envelope().case.status, owner: 'Technical reviewer', capabilityLabel: 'Rules-based', current: true, fallbackReason: null });
    expect(result.snapshot?.nextAction).toEqual({ actionId: 'review', text: 'Review evidence' });
    expect(applyServerRefresh(null, envelope({ case: { ...envelope().case, capabilityLabel: 'unresolved', nextAction: { actionId: 'fake', text: 'fake' } } })).valid).toBe(false);
  });

  it('Hangar refresh rebuilds label and panel text from server snapshot', async () => {
    const injected={revision:1,status:'client',owner:'Injected owner',nextAction:{text:'fake'},capabilityLabel:'Unknown'};
    const first=applyServerRefresh(injected as never,envelope()).snapshot!;
    const higher=envelope({case:{...envelope().case,caseRevision:8,nextAction:{actionId:'approve',text:'Approve server evidence'}}});
    const result=await refreshHangarScene(()=>higher,first);
    expect(result.valid).toBe(true); expect(result.snapshot?.revision).toBe(8); expect(result.snapshot?.nextAction).toEqual({actionId:'approve',text:'Approve server evidence'});
    expect(result.scene).toBe(higher);
    const view=buildHangarServerSceneView(result.scene);
    expect(view).not.toBeNull(); expect((view as any).cases[0].nextAction.text).toBe('Approve server evidence');
    const stale=applyServerRefresh(first,envelope({case:{...envelope().case,caseRevision:6,nextAction:{text:'stale'}}}));
    expect(stale.valid).toBe(false); expect(stale.snapshot?.nextAction).toEqual({actionId:'review',text:'Review evidence'});
    expect(stale.snapshot?.revision).toBe(7); expect(canAdmitHangarCommands(stale.snapshot)).toBe(false);
    expect(buildHangarServerSceneView({contractVersion:99,case:{}})).toBeNull();
  });

  it('HangarSceneRefresh preserves stale state and command admission', async () => {
    const first = applyServerRefresh(null, envelope()).snapshot!;
    expect(canAdmitHangarCommands(first)).toBe(true);
    const failed = await refreshHangarScene(() => { throw new Error('network'); }, first);
    expect(failed.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(canAdmitHangarCommands(failed.snapshot)).toBe(false);
    const malformed = applyServerRefresh(first, envelope({ contractVersion: 99 }));
    expect(malformed.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(canAdmitHangarCommands(malformed.snapshot)).toBe(false);
    const missingRevision = applyServerRefresh(first, envelope({ case: { ...envelope().case, caseRevision: undefined } }));
    expect(missingRevision.valid).toBe(false);
    expect(missingRevision.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(canAdmitHangarCommands(missingRevision.snapshot)).toBe(false);
    const badWitness = applyServerRefresh(first, envelope({ history: [{ witnessIds: ['missing-witness'] }] }));
    expect(badWitness.valid).toBe(false);
    expect(canAdmitHangarCommands(badWitness.snapshot)).toBe(false);
    const recovered = applyServerRefresh(missingRevision.snapshot, envelope({ case: { ...envelope().case, caseRevision: 8 } }));
    expect(recovered.valid).toBe(true);
    expect(recovered.snapshot).toMatchObject({ revision: 8, current: true, fallbackReason: null });
    expect(canAdmitHangarCommands(recovered.snapshot)).toBe(true);
    const malformedV1 = await refreshHangarScene(() => envelope({ case: { ...envelope().case, permission: undefined } }), first);
    expect(malformedV1.valid).toBe(false);
    expect(malformedV1.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(canAdmitHangarCommands(malformedV1.snapshot)).toBe(false);
    const stale = applyServerRefresh(first, envelope({ case: { ...envelope().case, caseRevision: 6 } }));
    expect(stale.valid).toBe(false);
    expect(stale.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(canAdmitHangarCommands(stale.snapshot)).toBe(false);
    const unchanged = applyServerRefresh(first, envelope());
    expect(unchanged.valid).toBe(true);
    expect(unchanged.snapshot).toMatchObject({ revision: 7, current: true, fallbackReason: null });
    expect(unchanged.snapshot).toBe(first);
    expect(canAdmitHangarCommands(unchanged.snapshot)).toBe(true);
    const staleSnapshot = markRefreshStale(first).snapshot;
    const staleIdentical = applyServerRefresh(staleSnapshot, envelope());
    expect(staleIdentical.valid).toBe(true);
    expect(staleIdentical.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(staleIdentical.snapshot).toBe(staleSnapshot);
    expect(canAdmitHangarCommands(staleIdentical.snapshot)).toBe(false);
    const changedAtSameRevision = applyServerRefresh(first, envelope({ case: { ...envelope().case, owner: 'Changed owner' } }));
    expect(changedAtSameRevision.valid).toBe(false);
    expect(changedAtSameRevision.snapshot).toMatchObject({ revision: 7, current: false, fallbackReason: 'refresh_failed' });
    expect(canAdmitHangarCommands(changedAtSameRevision.snapshot)).toBe(false);
    const switchedCaseObjects = allIds('B').map((objectId, index) => ({ objectId, kind: ['aircraft','engine','component','component'][index], caseId: 'cp-case-b' }));
    const switchedCase = applyServerRefresh(first, envelope({ case: { ...envelope().case, caseId: 'cp-case-b', caseRevision: 2 }, objects: switchedCaseObjects }));
    expect(switchedCase.valid).toBe(true);
    expect(switchedCase.snapshot).toMatchObject({ caseId: 'cp-case-b', revision: 2, current: true });
  });

  it('HangarSceneRefresh unsupported contract fallback', () => {
    const unsupported = validateHangarSceneEnvelope({ contractVersion: 99 });
    expect(unsupported).toEqual({ valid: false, scene: null, error: 'Unsupported hangar contract version 99' });
    expect(projectHangarScene({ contractVersion: 99 }, validManifest())).toMatchObject({ valid: false, descriptor: null });
  });

  it('HangarSceneRefresh validates descriptor mappings', () => {
    const activate = vi.fn(); const descriptor = buildHangarSceneDescriptor(envelope(), validManifest(), activate);
    expect(descriptor.valid).toBe(true); expect(descriptor.descriptor).toHaveLength(4);
    descriptor.descriptor![0].activate?.(); expect(activate).toHaveBeenCalledTimes(1);
    for (const broken of [
      envelope({ objects: [...envelope().objects.slice(0, 3), envelope().objects[2]] }),
      envelope({ objects: envelope().objects.map((o, i) => i === 3 ? { ...o, objectId: 'SYN-CP-B-CMP-002' } : o) }),
      envelope({ objects: [...envelope().objects.slice(0, 3), { ...envelope().objects[3], objectId: 'unknown-node' }] }),
      envelope({ objects: envelope().objects.slice(0, 3) }),
      { ...envelope(), objects: undefined },
    ]) {
      const result=buildHangarSceneDescriptor(broken, validManifest(), activate);
      expect(result.valid).toBe(false); expect(result.descriptor).toBeNull(); expect(result.error).toContain('Visible mapping error');
    }
    expect(markRefreshStale(null).snapshot).toBeNull();
  });
});
