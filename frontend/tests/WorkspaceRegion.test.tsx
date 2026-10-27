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

    const onRetry = vi.fn();
    rerender(
      <WorkspaceRegion status="unavailable" error={{ status: 503, message: 'Service temporarily unavailable' }} onRetry={onRetry} />,
    );
    expect(screen.getByRole('alert').textContent).toBe('Service temporarily unavailable Retry');
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('retry_reloads_region_once_per_click', () => {
    const onRetry = vi.fn();
    const { rerender } = render(
      <WorkspaceRegion status="unavailable" error={{ status: 503, message: 'Service temporarily unavailable' }} onRetry={onRetry} />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(2);

    rerender(
      <WorkspaceRegion status="ready" error={null}>
        <div>Recovered region data</div>
      </WorkspaceRegion>,
    );
    expect(screen.getByText('Recovered region data')).toBeTruthy();
  });

  it('redirects_to_sign_in_with_the_current_path_for_401', () => {
    const originalLocation = window.location;
    const assignSpy = vi.fn();
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: {
        ...originalLocation,
        pathname: '/cases/case-001',
        search: '?tab=review',
        hash: '',
        assign: assignSpy,
      },
    });

    render(
      <WorkspaceRegion
        status="permission"
        error={{ status: 401, message: 'Sign in again to continue.' }}
        currentPath="/cases/cp-case-a/findings?tab=review"
      />,
    );

    expect(screen.getByRole('alert').textContent).toBe('Signing in…');
    expect(assignSpy).toHaveBeenCalledWith('/sign-in?returnTo=%2Fcases%2Fcp-case-a%2Ffindings%3Ftab%3Dreview');
    Object.defineProperty(window, 'location', { configurable: true, value: originalLocation });
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
