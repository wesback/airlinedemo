export const HANGAR_JAVASCRIPT_GZIP_BUDGET_BYTES = 1_572_864;
export const HANGAR_ASSET_BUDGET_BYTES = 15_728_640;
export const HANGAR_BUILD_BUDGET_ERROR = 'Hangar build budget exceeded or build attribution is missing';

export type HangarBudgetChunk = {
  gzipBytes?: unknown; gzipSize?: unknown; bytes?: unknown; size?: unknown;
  hangarOnly?: unknown; attributableToHangar?: unknown; hangarOnlyAttribution?: unknown;
};
export type HangarBuildBudgetInput = {
  javascriptBytes?: unknown; jsBytes?: unknown; hangarJavaScriptBytes?: unknown; hangarOnlyGzipJavaScriptBytes?: unknown;
  gzipJavaScriptBytes?: unknown; javascriptGzipBytes?: unknown;
  assetBytes?: unknown; assetsBytes?: unknown; modelTextureBytes?: unknown;
  chunks?: unknown; javascriptChunks?: unknown; jsChunks?: unknown;
  assets?: unknown; assetChunks?: unknown; modelTextureAssets?: unknown;
  dependencyChunkMappings?: unknown; dependencyChunks?: unknown;
};

function count(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  return value;
}
function attributed(value: unknown): boolean | null {
  if (value === true || value === 'hangar-only' || value === 'hangar') return true;
  if (value === false || value === 'shared' || value === null) return false;
  return null;
}
function classifiedChunks(input: unknown, sizeKeys: string[], requireClassification = true): number {
  if (!Array.isArray(input)) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  let total = 0;
  for (const item of input) {
    if (!item || typeof item !== 'object') throw new Error(HANGAR_BUILD_BUDGET_ERROR);
    const chunk = item as Record<string, unknown>;
    const explicit = attributed(chunk.hangarOnly ?? chunk.hangarOnlyAttribution ?? chunk.attributableToHangar ?? chunk.attribution);
    if (requireClassification && explicit === null) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
    let size: unknown;
    for (const key of sizeKeys) if (chunk[key] !== undefined) { size = chunk[key]; break; }
    if (typeof size !== 'number') throw new Error(HANGAR_BUILD_BUDGET_ERROR);
    if (explicit !== false) total += count(size);
  }
  return total;
}

export function validateHangarBuildBudgets(input?: HangarBuildBudgetInput | null): true {
  if (!input || typeof input !== 'object') throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  const directJs = input.hangarOnlyGzipJavaScriptBytes ?? input.gzipJavaScriptBytes ?? input.javascriptGzipBytes ?? input.javascriptBytes ?? input.jsBytes ?? input.hangarJavaScriptBytes;
  const directAssets = input.assetBytes ?? input.assetsBytes ?? input.modelTextureBytes;
  const chunkInput = input.chunks ?? input.javascriptChunks ?? input.jsChunks;
  const assetInput = input.assets ?? input.assetChunks ?? input.modelTextureAssets;
  let javascriptBytes = directJs === undefined ? 0 : count(directJs);
  let assetBytes = directAssets === undefined ? 0 : count(directAssets);
  if (chunkInput !== undefined) javascriptBytes = classifiedChunks(chunkInput, ['gzipBytes', 'gzipSize', 'bytes', 'size']);
  if (assetInput !== undefined) assetBytes = classifiedChunks(assetInput, ['bytes', 'size', 'gzipBytes', 'gzipSize'], false);

  if (input.dependencyChunkMappings !== undefined || input.dependencyChunks !== undefined) {
    const mappings = input.dependencyChunkMappings ?? input.dependencyChunks;
    if (!mappings || typeof mappings !== 'object') throw new Error(HANGAR_BUILD_BUDGET_ERROR);
    if (!Array.isArray(mappings)) {
      const entries = Object.entries(mappings);
      if (entries.length === 0) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
      for (const [, value] of entries) {
        const classification = value && typeof value === 'object'
          ? (value as Record<string, unknown>).hangarOnly ?? (value as Record<string, unknown>).hangarOnlyAttribution ?? (value as Record<string, unknown>).attribution ?? (value as Record<string, unknown>).classification
          : value;
        if (attributed(classification) === null) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
      }
    } else {
      if (mappings.length === 0) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
      for (const value of mappings) {
        if (!value || typeof value !== 'object') throw new Error(HANGAR_BUILD_BUDGET_ERROR);
        const record = value as Record<string, unknown>;
        const classification = record.hangarOnly ?? record.hangarOnlyAttribution ?? record.attribution ?? record.classification;
        if (attributed(classification) === null) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
      }
    }
  }
  if (directJs === undefined && chunkInput === undefined) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  if (directAssets === undefined && assetInput === undefined) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  const mappings = input.dependencyChunkMappings ?? input.dependencyChunks;
  if (!mappings || typeof mappings !== 'object' || (Array.isArray(mappings) ? mappings.length === 0 : Object.keys(mappings).length === 0)) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  if (javascriptBytes > HANGAR_JAVASCRIPT_GZIP_BUDGET_BYTES || assetBytes > HANGAR_ASSET_BUDGET_BYTES) throw new Error(HANGAR_BUILD_BUDGET_ERROR);
  return true;
}
export const validateHangarBuildBudget = validateHangarBuildBudgets;
export const validateHangarBudget = validateHangarBuildBudgets;
export const validateHangarBudgets = validateHangarBuildBudgets;
