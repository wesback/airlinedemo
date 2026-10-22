import { Component, type ErrorInfo, type ReactNode } from 'react';
import type { ServerSceneSnapshot } from './sceneRefresh';

export const HANGAR_RENDER_FAILURE = 'render_failed';
export const HANGAR_ASSET_FAILURE = 'asset_failed';
export type HangarBoundaryFailure = typeof HANGAR_RENDER_FAILURE | typeof HANGAR_ASSET_FAILURE;
export type HangarErrorSnapshot = ServerSceneSnapshot | null;

const isObject = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
export function isLabelledHangarSnapshot(value: unknown): value is ServerSceneSnapshot {
  if (!isObject(value) || value.current !== true) return false;
  const label = value.capabilityLabel;
  return typeof value.caseId === 'string' && typeof value.revision === 'number' && Number.isInteger(value.revision) && value.revision >= 0 &&
    typeof value.status !== 'undefined' && ['AI agent','Rules-based','Simulated','Design-only','Unknown'].includes(String(label));
}
export function preserveLastLabelledServerSnapshot(previous: HangarErrorSnapshot, candidate: unknown): ServerSceneSnapshot | null {
  if (!isObject(candidate) || candidate.current !== true) return previous;
  const validShape = isLabelledHangarSnapshot(candidate);
  if (!validShape) return previous;
  if (!previous?.caseId || previous.revision === null) return candidate as ServerSceneSnapshot;
  if (typeof previous.caseId === 'string' && candidate.caseId === previous.caseId &&
      typeof previous.revision === 'number' && typeof candidate.revision === 'number') {
    if (candidate.revision < previous.revision) return previous;
  }
  return candidate as ServerSceneSnapshot;
}
export function preserveHangarServerSnapshot(previous: HangarErrorSnapshot, candidate: unknown): ServerSceneSnapshot | null {
  return preserveLastLabelledServerSnapshot(previous, candidate);
}

export type HangarCommandInput = string | Record<string, unknown>;
export function getHangarCommandIdentifier(command: HangarCommandInput, snapshot: unknown = null): string {
  if (typeof command === 'string') return command;
  if (!isObject(command)) return 'hangar:unknown';
  const explicit = command.commandId;
  if (typeof explicit === 'string' && explicit.length > 0) return explicit;
  const action = typeof command.action === 'string' ? command.action : typeof command.variant === 'string' ? command.variant : 'command';
  const s = isObject(snapshot) ? snapshot : {};
  const caseId = typeof s.caseId === 'string' ? s.caseId : typeof command.caseId === 'string' ? command.caseId : 'unknown-case';
  const revision = Number.isInteger(s.revision) ? String(s.revision) : Number.isInteger(command.expectedRevision) ? String(command.expectedRevision) : 'unknown-revision';
  return `hangar:${action}:${caseId}:${revision}`;
}
export const hangarCommandIdentifier = getHangarCommandIdentifier;

const claimedCommands = new Set<string>();
export function resetHangarCommandDeduper(): void { claimedCommands.clear(); }
export function dedupeHangarCommand(command: HangarCommandInput, snapshot: unknown = null): boolean {
  const identifier = getHangarCommandIdentifier(command, snapshot);
  if (claimedCommands.has(identifier)) return false;
  claimedCommands.add(identifier); return true;
}
export function isHangarCommandDuplicate(command: HangarCommandInput, snapshot: unknown = null): boolean {
  return !dedupeHangarCommand(command, snapshot);
}
export const claimHangarCommand = dedupeHangarCommand;
export function createHangarCommandDeduper() {
  const claimed = new Set<string>();
  return { claim(command: HangarCommandInput, snapshot: unknown = null) { const id = getHangarCommandIdentifier(command, snapshot); if (claimed.has(id)) return false; claimed.add(id); return true; }, has(command: HangarCommandInput, snapshot: unknown = null) { return claimed.has(getHangarCommandIdentifier(command, snapshot)); }, clear() { claimed.clear(); } };
}

export function HangarFallbackPanel({ snapshot, reason }: { snapshot: HangarErrorSnapshot; reason?: HangarBoundaryFailure | string | null }) {
  const label = snapshot && typeof snapshot.capabilityLabel === 'string' ? snapshot.capabilityLabel : 'Unknown';
  const revision = snapshot?.revision ?? null;
  return <section role="alert" aria-label="Hangar fallback" data-hangar-fallback="true" data-hangar-snapshot-revision={revision ?? undefined} data-hangar-capability-label={label}><h3>Hangar unavailable</h3><p>The 3D scene could not be displayed. Use the non-3D controls below.</p>{snapshot && <p data-hangar-last-label="server">Last labelled server state: {label}, revision {revision}.</p>}</section>;
}

export type HangarErrorBoundaryProps = {
  children: ReactNode; snapshot?: unknown; lastSnapshot?: unknown; fallback?: ReactNode; assetFailure?: HangarBoundaryFailure | string | null;
  resetKey?: unknown; entryKey?: unknown; onError?: (error: unknown, info?: ErrorInfo) => void;
  retryCommands?: readonly HangarCommandInput[];
  onCommand?: (command: HangarCommandInput, snapshot: ServerSceneSnapshot | null) => void;
  onMutation?: (command: HangarCommandInput, snapshot: ServerSceneSnapshot | null) => void;
};
type State = { failed: boolean; failure: HangarBoundaryFailure | string | null; lastSnapshot: ServerSceneSnapshot | null };
export class HangarErrorBoundary extends Component<HangarErrorBoundaryProps, State> {
  state: State;
  constructor(props: HangarErrorBoundaryProps) {
    super(props);
    const labelledCandidate = (value: unknown): ServerSceneSnapshot | null => {
      if (!isObject(value) || typeof value.caseId !== 'string' || typeof value.revision !== 'number' || !Number.isInteger(value.revision) || value.revision < 0 ||
          typeof value.status === 'undefined' || !['AI agent','Rules-based','Simulated','Design-only','Unknown'].includes(String(value.capabilityLabel))) return null;
      return value as ServerSceneSnapshot;
    };
    const snapshot = labelledCandidate(props.lastSnapshot) ?? labelledCandidate(props.snapshot);
    this.state = { failed: false, failure: null, lastSnapshot: snapshot };
  }
  public executeRetryCommands(): string[] {
    const executed: string[] = [];
    for (const command of this.props.retryCommands ?? []) {
      if (!dedupeHangarCommand(command, this.state.lastSnapshot)) continue;
      executed.push(getHangarCommandIdentifier(command, this.state.lastSnapshot));
      this.props.onCommand?.(command, this.state.lastSnapshot);
    }
    return executed;
  }
  static getDerivedStateFromError(error: unknown) { return { failed: true, failure: HANGAR_RENDER_FAILURE, error }; }
  componentDidCatch(error: unknown, info: ErrorInfo) {
    this.executeRetryCommands();
    this.props.onError?.(error, info);
  }
  componentDidMount() {
    if (this.props.assetFailure || this.props.entryKey !== undefined) this.executeRetryCommands();
  }
  componentDidUpdate(previous: HangarErrorBoundaryProps) {
    if (previous.assetFailure !== this.props.assetFailure && this.props.assetFailure) {
      this.setState({ failed: true, failure: this.props.assetFailure });
      this.executeRetryCommands();
    } else if (previous.resetKey !== this.props.resetKey) {
      const nextSnapshot = isLabelledHangarSnapshot(this.props.snapshot) ? this.props.snapshot : null;
      this.executeRetryCommands();
      if (this.props.assetFailure) this.setState({ failed: true, failure: this.props.assetFailure });
      else if (nextSnapshot?.current === true) this.setState({ failed: false, failure: null });
    } else if (previous.entryKey !== this.props.entryKey && this.props.entryKey !== undefined) {
      this.executeRetryCommands();
    }
    const next = preserveLastLabelledServerSnapshot(this.state.lastSnapshot, this.props.snapshot ?? this.props.lastSnapshot);
    if (next !== this.state.lastSnapshot) this.setState({ lastSnapshot: next });
  }
  render() {
    if (this.props.assetFailure || this.state.failed) {
      return this.props.fallback ?? <HangarFallbackPanel snapshot={this.state.lastSnapshot} reason={this.props.assetFailure ?? this.state.failure} />;
    }
    return this.props.children;
  }
}
export default HangarErrorBoundary;
