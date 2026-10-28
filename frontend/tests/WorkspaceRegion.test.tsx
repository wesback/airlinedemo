import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import WorkspaceRegion, { buildReturnToPath, getRegionErrorMessage } from '../src/components/WorkspaceRegion';

const originalLocation = window.location;

afterEach(() => {
  cleanup();
  Object.defineProperty(window, 'location', { configurable: true, value: originalLocation });
});

describe('workspace region contract', () => {
  it('workspace_region_loading_empty_and_retry_contract', () => {
    const { rerender } = render(<WorkspaceRegion status="loading" />);
    expect(screen.getByRole('status').textContent).toBe('Loading…');

    rerender(<WorkspaceRegion status="empty" />);
    expect(screen.getByTestId('workspace-region-empty').textContent).toBe('No data is available for this workspace.');

    const onRetry = vi.fn();
    rerender(<WorkspaceRegion status="error" error={{ status: 503 }} onRetry={onRetry} />);
    expect(screen.getByRole('alert').textContent).toBe('Service temporarily unavailable Retry');
    expect(screen.getAllByRole('button', { name: 'Retry' })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(1);

    rerender(<WorkspaceRegion status="error" error={{ status: 503 }} />);
    expect(screen.getByRole('alert').textContent).toBe('Service temporarily unavailable');
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();

    rerender(<WorkspaceRegion status="ready"><p>Region content</p></WorkspaceRegion>);
    expect(screen.getByText('Region content')).toBeTruthy();
  });

  it('keeps_each_status_text_exact_and_never_shows_a_response_body', () => {
    expect(getRegionErrorMessage(401)).toBe('Sign in again to continue.');
    expect(getRegionErrorMessage(403)).toBe('You do not have permission for this action.');
    expect(getRegionErrorMessage(404)).toBe('Case not found or not in your scope.');
    expect(getRegionErrorMessage(503)).toBe('Service temporarily unavailable');
    expect(getRegionErrorMessage(500)).toBe('Unable to load this region.');

    render(<WorkspaceRegion status="error" error={{ status: 403 }} onRetry={() => {}} />);
    expect(screen.getByRole('alert').textContent).toBe('You do not have permission for this action. Retry');
  });

  it('redirects_to_sign_in_with_the_current_path_for_401', () => {
    const assign = vi.fn();
    Object.defineProperty(window, 'location', { configurable: true, value: { ...originalLocation, pathname: '/work', search: '', assign } });
    render(<WorkspaceRegion status="error" error={{ status: 401 }} currentPath="/cases/case-001?tab=review" />);
    expect(screen.getByRole('alert').textContent).toBe('Signing in…');
    expect(assign).toHaveBeenCalledTimes(1);
    expect(assign).toHaveBeenCalledWith('/sign-in?returnTo=%2Fcases%2Fcase-001%3Ftab%3Dreview');
    expect(buildReturnToPath('/cases/case-001?tab=review')).toBe('/sign-in?returnTo=%2Fcases%2Fcase-001%3Ftab%3Dreview');
  });
});
