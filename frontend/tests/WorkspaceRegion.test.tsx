import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import WorkspaceRegion, { buildReturnToPath, getRegionErrorMessage } from '../src/components/WorkspaceRegion';

beforeEach(() => cleanup());
afterEach(() => cleanup());

describe('workspace region contract', () => {
  it('workspace_region_loading_empty_and_retry_contract', () => {
    const { rerender } = render(<WorkspaceRegion status="loading" />);
    expect(screen.getByRole('status').textContent).toBe('Loading…');

    rerender(<WorkspaceRegion status="empty" />);
    expect(screen.getByTestId('workspace-region-empty').textContent).toBe('No data is available for this workspace.');

    rerender(<WorkspaceRegion status="unavailable" error={{ status: 503, message: 'Service temporarily unavailable' }} onRetry={vi.fn()} />);
    expect(screen.getByRole('alert').textContent).toContain('Service temporarily unavailable');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('retry_reloads_region_once_per_click', () => {
    const onRetry = vi.fn();
    const { getAllByRole } = render(<WorkspaceRegion status="unavailable" error={{ status: 503, message: 'Service temporarily unavailable' }} onRetry={onRetry} />);
    fireEvent.click(getAllByRole('button', { name: 'Retry' })[0]);
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('returnTo_and_error_text_contracts_are_exact', () => {
    expect(getRegionErrorMessage(401)).toBe('Sign in again to continue.');
    expect(getRegionErrorMessage(403)).toBe('You do not have permission for this action.');
    expect(getRegionErrorMessage(404)).toBe('Case not found or not in your scope.');
    expect(getRegionErrorMessage(503)).toBe('Service temporarily unavailable');

    const currentPath = '/cases/cp-case-a/findings?tab=review';
    const result = buildReturnToPath(currentPath);
    expect(result).toBe('/sign-in?returnTo=%2Fcases%2Fcp-case-a%2Ffindings%3Ftab%3Dreview');
  });
});
