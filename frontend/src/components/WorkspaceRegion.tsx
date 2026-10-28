import { useEffect, type ReactNode } from 'react';

export type WorkspaceRegionError = { status?: number; message?: string };

export type WorkspaceRegionProps = {
  status: 'loading' | 'empty' | 'error' | 'ready';
  error?: WorkspaceRegionError | null;
  emptyMessage?: string;
  loadingMessage?: string;
  currentPath?: string | null;
  onRetry?: () => void;
  children?: ReactNode;
};

// One text per status, shared by every data region. Response bodies are never shown.
export function getRegionErrorMessage(status: number | undefined, fallback = 'Unable to load this region.') {
  if (status === 401) return 'Sign in again to continue.';
  if (status === 403) return 'You do not have permission for this action.';
  if (status === 404) return 'Case not found or not in your scope.';
  if (status === 503) return 'Service temporarily unavailable';
  return fallback;
}

export function buildReturnToPath(currentPath: string): string {
  return `/sign-in?returnTo=${encodeURIComponent(currentPath.startsWith('/') ? currentPath : `/${currentPath}`)}`;
}

export default function WorkspaceRegion({
  status, error, emptyMessage = 'No data is available for this workspace.', loadingMessage = 'Loading…', currentPath, onRetry, children,
}: WorkspaceRegionProps) {
  const code = status === 'error' ? error?.status : undefined;
  useEffect(() => {
    // A 401 sends the person to sign-in, keeping the page they were on.
    if (code === 401) window.location.assign(buildReturnToPath(currentPath || `${window.location.pathname}${window.location.search}`));
  }, [code, currentPath]);

  if (status === 'loading') return <div role="status" aria-live="polite" data-testid="workspace-region-loading">{loadingMessage}</div>;
  if (status === 'empty') return <p data-testid="workspace-region-empty">{emptyMessage}</p>;
  if (status === 'error') {
    if (code === 401) return <div role="alert" data-testid="workspace-region-error">Signing in…</div>;
    return (
      <div role="alert" data-testid="workspace-region-error">
        <span>{error?.message?.trim() || getRegionErrorMessage(code)}</span>
        {onRetry && <> <button type="button" onClick={onRetry}>Retry</button></>}
      </div>
    );
  }
  return <>{children}</>;
}
