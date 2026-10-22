import type { HangarAssetRecord } from './assetPolicy';

/** Static allowlisted Hangar assets. Real assets must replace these records with approved bytes and checksums. */
export const HANGAR_ASSET_MANIFEST: { assets: HangarAssetRecord[] } = { assets: [] };
export default HANGAR_ASSET_MANIFEST;
