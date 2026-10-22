import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import HangarErrorBoundary, { HangarFallbackPanel, claimHangarCommand, createHangarCommandDeduper, isLabelledHangarSnapshot, preserveHangarServerSnapshot, resetHangarCommandDeduper } from '../src/hangar/errorBoundary';
import { applyServerRefresh, refreshHangarScene } from '../src/hangar/sceneRefresh';

afterEach(() => { cleanup(); resetHangarCommandDeduper(); });
const serverEnvelope = (revision = 7) => ({ contractVersion: 1, packVersion: '1.0.2', case: { caseId: 'cp-case-a', caseRevision: revision, status: { assessment: 'ready', review: 'pending' }, owner: 'Technical reviewer', nextAction: { actionId: 'review', text: 'Review evidence' }, capabilityLabel: 'Rules-based', permission: true }, canResetBlockedVariant: false, allowedActions: [], history: [], evidence: [], documentRefs: [], objects: [{ objectId: 'SYN-CP-A-AIR-001', kind: 'aircraft', caseId: 'cp-case-a' },{ objectId: 'SYN-CP-A-ENG-001', kind: 'engine', caseId: 'cp-case-a' },{ objectId: 'SYN-CP-A-CMP-001', kind: 'component', caseId: 'cp-case-a' },{ objectId: 'SYN-CP-A-CMP-002', kind: 'component', caseId: 'cp-case-a' }] });
const snapshot = applyServerRefresh(null, serverEnvelope()).snapshot!;
function Throws(): never { throw new Error('render failed'); }

describe('HangarErrorBoundary', () => {
  it('HangarErrorBoundary preserves state on render and asset failure', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const mutation = vi.fn(); const exact = { ...snapshot, status: { ...snapshot.status as object, assessment: 'ready' } };
    const { rerender } = render(<HangarErrorBoundary snapshot={exact} fallback={<HangarFallbackPanel snapshot={exact} />}><Throws /></HangarErrorBoundary>);
    expect(screen.getByRole('alert', { name: 'Hangar fallback' })).toBeTruthy();
    expect(screen.getByText(/Last labelled server state: Rules-based, revision 7/)).toBeTruthy();
    expect(exact.revision).toBe(7); expect(mutation).not.toHaveBeenCalled(); expect(screen.queryByLabelText(/3D/i)).toBeNull();

    rerender(<HangarErrorBoundary snapshot={null} lastSnapshot={exact} assetFailure="asset_failed"><div aria-label="Hangar 3D scene" /></HangarErrorBoundary>);
    expect(screen.getByRole('alert', { name: 'Hangar fallback' }).textContent).toContain('Rules-based, revision 7');
    expect(preserveHangarServerSnapshot(exact, { revision: 99, current: true })).toBe(exact);
    expect(isLabelledHangarSnapshot(exact)).toBe(true); expect(mutation).not.toHaveBeenCalled(); expect(screen.queryByLabelText('Hangar 3D scene')).toBeNull();

    render(<HangarFallbackPanel snapshot={null} reason="asset_failed" />);
    expect(screen.getAllByRole('alert').length).toBeGreaterThan(0);
  });

  it('HangarErrorBoundary sends zero mutations and retains command identity', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    resetHangarCommandDeduper();
    const mutation = vi.fn(); const commandCallback = vi.fn(); const arrCommand = vi.fn();
    const command = { commandId: 'cp-reset-7', action: 'reset', variant: 'blocked', expectedRevision: 7 };
    const { rerender } = render(<HangarErrorBoundary snapshot={snapshot} entryKey="enter-1" retryCommands={[command, 'ARR:review:cp-case-a:7']} onCommand={commandCallback} onMutation={mutation}><Throws /></HangarErrorBoundary>);
    expect(screen.getByRole('alert', { name: 'Hangar fallback' })).toBeTruthy();
    rerender(<HangarErrorBoundary snapshot={snapshot} entryKey="enter-2" retryCommands={[command, 'ARR:review:cp-case-a:7']} onCommand={commandCallback} onMutation={mutation}><Throws /></HangarErrorBoundary>);
    rerender(<HangarErrorBoundary snapshot={snapshot} entryKey="enter-3" assetFailure="asset_failed" retryCommands={[command, 'ARR:review:cp-case-a:7']} onCommand={commandCallback} onMutation={mutation}><div aria-label="Hangar 3D scene" /></HangarErrorBoundary>);

    expect(commandCallback.mock.calls.map(call => call[0])).toEqual([command, 'ARR:review:cp-case-a:7']);
    expect(mutation).not.toHaveBeenCalled(); expect(arrCommand).not.toHaveBeenCalled();
    expect(claimHangarCommand(command, snapshot)).toBe(false);

    resetHangarCommandDeduper(); const local = createHangarCommandDeduper();
    expect(local.claim('ARR:review:cp-case-a:7', snapshot)).toBe(true); expect(local.claim('ARR:review:cp-case-a:7', snapshot)).toBe(false);
    expect(mutation).not.toHaveBeenCalled(); expect(arrCommand).not.toHaveBeenCalled();
  });

  it('HangarErrorBoundary recovers after valid refresh', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const failed = await refreshHangarScene(() => { throw new Error('network'); }, snapshot);
    expect(failed.valid).toBe(false); expect(failed.snapshot?.revision).toBe(7); expect(failed.snapshot?.current).toBe(false);
    const staleClient = { ...snapshot, revision: 6, status: { assessment: 'draft' }, current: true };
    expect(preserveHangarServerSnapshot(failed.snapshot, staleClient)).toBe(failed.snapshot);

    const recovered = applyServerRefresh(failed.snapshot, serverEnvelope(8));
    expect(recovered.valid).toBe(true); expect(recovered.snapshot).toMatchObject({ revision: 8, current: true, status: serverEnvelope(8).case.status });
    const { rerender } = render(<HangarErrorBoundary snapshot={snapshot} assetFailure="asset_failed"><div>3D</div></HangarErrorBoundary>);
    rerender(<HangarErrorBoundary snapshot={recovered.snapshot} resetKey={8}><div>Recovered scene</div></HangarErrorBoundary>);
    expect(screen.getByText('Recovered scene')).toBeTruthy(); expect(screen.queryByRole('alert', { name: 'Hangar fallback' })).toBeNull();
  });
});
