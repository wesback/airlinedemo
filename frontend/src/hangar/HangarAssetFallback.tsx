import type { HangarAssetFailure } from './hangarAssetLoader';

/** Shows the asset error as plain text; nothing from the asset itself is rendered. */
export default function HangarAssetFallback({ reason }: { reason: HangarAssetFailure | null }) {
  if (reason === null) return null;
  return <p role="alert">{`Unable to display this hangar asset (${reason}).`}</p>;
}
