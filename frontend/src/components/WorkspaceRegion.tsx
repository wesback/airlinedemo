import { useEffect, type ReactNode } from 'react';

export type WorkspaceRegionStatus = 'loading' | 'empty' | 'ready' | 'error' | 'permission' | 'missing' | 'unavailable';

export type WorkspaceRegionError = {
  status?: number;
  message?: string;
  retryable?: boolean;
};

export type WorkspaceRegionProps = {
  status?: WorkspaceRegionStatus | 'pending';
  loading?: boolean;
  empty?: boolean;
  error?: WorkspaceRegionError | Error | number | null;
  emptyMessage?: string;
  loadingMessage?: string;
  onRetry?: () => void;
  children?: ReactNode;
};

function toStatusCode(value: unknown): number | undefined {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (value && typeof value === 'object' && 'status' in value && typeof (value as { status?: unknown }).status === 'number') {
    return (value as { status: number }).status;
  }
  return undefined;
}

export function getRegionErrorMessage(status: number | undefined, fallback = 'Unable to load this region.') {
  if (status === 401) return 'Sign in again to continue.';
  if (status === 403) return 'You do not have permission for this action.';
  if (status === 404) return 'Case not found or not in your scope.';
  if (status === 503) return 'Service temporarily unavailable';
  return fallback;
}

export function buildReturnToPath(currentPath?: string | null): string {
  if (typeof window === 'undefined') return '/work';
  const path = typeof currentPath === 'string' && currentPath.trim() ? currentPath : `${window.location.pathname}${window.location.search}${window.location.hash}`;
  const safePath = path.startsWith('/') ? path : `/${path}`;
  return `/sign-in?returnTo=${encodeURIComponent(safePath)}`;
}

export default function WorkspaceRegion({
  status,
  loading,
  empty,
  error,
  emptyMessage = 'No data is available for this workspace.',
  loadingMessage = 'Loading…',
  onRetry,
  children,
}: WorkspaceRegionProps) {
  const effectiveStatus = status === 'pending' ? 'loading' : (status ?? (loading ? 'loading' : empty ? 'empty' : error ? 'error' : 'ready'));
  const regionError = error instanceof Error ? { status: toStatusCode(error), message: error.message } : typeof error === 'number' ? { status: error } : error ?? null;
  const statusCode = toStatusCode(regionError) ?? (effectiveStatus === 'permission' ? 403 : effectiveStatus === 'missing' ? 404 : effectiveStatus === 'unavailable' ? 503 : undefined);
  const message = regionError?.message ?? getRegionErrorMessage(statusCode, 'Unable to load this region.');

  useEffect(() => {
    if (statusCode === 401 && typeof window !== 'undefined') {
      window.location.assign(buildReturnToPath(`${window.location.pathname}${window.location.search}${window.location.hash}`));
    }
  }, [statusCode]);

  if (effectiveStatus === 'loading' || loading) {
    return <div role="status" aria-live="polite" data-testid="workspace-region-loading">{loadingMessage}</div>;
  }

  if (effectiveStatus === 'empty' || empty) {
    return <p data-testid="workspace-region-empty">{emptyMessage}</p>;
  }

  if (statusCode === 401) {
    return <div role="alert" data-testid="workspace-region-error">Signing in…</div>;
  }

  if (effectiveStatus === 'error' || effectiveStatus === 'permission' || effectiveStatus === 'missing' || effectiveStatus === 'unavailable' || statusCode !== undefined) {
    if (statusCode === 503) {
      return (
        <div role="alert" data-testid="workspace-region-error">
          <span>{message}</span>
          <button type="button" onClick={onRetry} aria-label="Retry" style={{ marginLeft: '0.5rem' }}>Retry</button>
        </div>
      );
    }
    return <div role="alert" data-testid="workspace-region-error">{message}</div>;
  }

  return <>{children}</>;
}
