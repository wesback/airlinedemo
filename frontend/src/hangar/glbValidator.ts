export const HANGAR_ASSET_INVALID = 'asset_invalid';
export const ASSET_INVALID_REASON = HANGAR_ASSET_INVALID;
export const GLB_ASSET_INVALID = HANGAR_ASSET_INVALID;
export const HANGAR_DEFAULT_JSON_LIMIT = 1_048_576;
export const HANGAR_DEFAULT_TOTAL_LIMIT = 5_242_880;
export const HANGAR_MAX_JSON_DEPTH = 128;

export type HangarGlbLimits = {
  maxJsonLength?: number;
  maxTotalLength?: number;
  maxJsonBytes?: number;
  maxTotalBytes?: number;
  jsonLimitBytes?: number;
  totalLimitBytes?: number;
  maxJson?: number;
  maxTotal?: number;
  maxJsonChunkBytes?: number;
  maxTotalBytesLimit?: number;
  jsonLimit?: number;
  totalLimit?: number;
};

function invalid(): never { throw new Error(HANGAR_ASSET_INVALID); }

function asSafeNumber(value: unknown, fallback: number): number {
  if (value === undefined) return fallback;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) invalid();
  if (!Number.isSafeInteger(value)) invalid();
  return value;
}

/** Deepest container nesting of JSON text (the root counts as 1), found by scanning so that no recursion is needed. */
function jsonDepth(text: string): number {
  let depth = 0;
  let deepest = 0;
  let inString = false;
  for (let index = 0; index < text.length; index += 1) {
    const char = text[index];
    if (inString) {
      if (char === '\\') index += 1;
      else if (char === '"') inString = false;
    } else if (char === '"') inString = true;
    else if (char === '{' || char === '[') {
      depth += 1;
      if (depth > deepest) deepest = depth;
    } else if (char === '}' || char === ']') depth -= 1;
  }
  return deepest;
}

function rangeInside(offset: unknown, length: unknown, containerLength: number): boolean {
  if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || (offset as number) < 0 || (length as number) < 0) return false;
  return (offset as number) <= containerLength && (length as number) <= containerLength - (offset as number);
}

export function validateHangarGlb(input: unknown, limits: HangarGlbLimits | number = {}, maxJsonLengthArg?: number): true {
  const supplied = typeof limits === 'number'
    ? maxJsonLengthArg === undefined
      ? { maxTotalBytes: limits }
      : { maxJsonBytes: limits, maxTotalBytes: maxJsonLengthArg }
    : (limits ?? {});
  if (!(input instanceof Uint8Array) || input.length === 0) invalid();
  const maxJson = asSafeNumber(supplied.maxJsonBytes ?? supplied.jsonLimitBytes ?? supplied.maxJson ?? supplied.jsonLimit ?? supplied.maxJsonLength, HANGAR_DEFAULT_JSON_LIMIT);
  const maxTotal = asSafeNumber(supplied.maxTotalBytes ?? supplied.totalLimitBytes ?? supplied.maxTotal ?? supplied.totalLimit ?? supplied.maxTotalLength, HANGAR_DEFAULT_TOTAL_LIMIT);
  if (limits !== undefined && typeof limits === 'number' && limits < 0) invalid();
  if (input.length > maxTotal || input.length < 12) invalid();

  const view = new DataView(input.buffer, input.byteOffset, input.byteLength);
  if (view.getUint32(0, true) !== 0x46546C67 || view.getUint32(4, true) !== 2 || view.getUint32(8, true) !== input.length) invalid();

  let offset = 12;
  let jsonBytes: Uint8Array | null = null;
  let binLength: number | null = null;
  let jsonChunks = 0;
  let binChunks = 0;
  while (offset < input.length) {
    if (offset + 8 > input.length) invalid();
    const length = view.getUint32(offset, true);
    const type = view.getUint32(offset + 4, true);
    if (length % 4 !== 0 || offset + 8 + length > input.length) invalid();
    const start = offset + 8;
    if (type === 0x4E4F534A) {
      jsonChunks += 1;
      if (jsonChunks !== 1 || offset !== 12 || jsonBytes !== null || length > maxJson) invalid();
      jsonBytes = input.subarray(start, start + length);
    } else if (type === 0x004E4942) {
      binChunks += 1;
      if (binChunks !== 1 || jsonChunks !== 1 || binLength !== null) invalid();
      binLength = length;
    } else invalid();
    offset += 8 + length;
  }
  if (jsonBytes === null || binLength === null || jsonChunks !== 1 || binChunks !== 1 || offset !== input.length) invalid();

  let jsonText: string;
  try { jsonText = new TextDecoder('utf-8', { fatal: true }).decode(jsonBytes).replace(/[\u0020\n\r\t]*$/, ''); }
  catch { invalid(); }
  if (jsonDepth(jsonText) > HANGAR_MAX_JSON_DEPTH) invalid();
  let json: unknown;
  try { json = JSON.parse(jsonText); }
  catch { invalid(); }
  if (!json || typeof json !== 'object' || Array.isArray(json)) invalid();

  function containsForbidden(value: unknown): boolean {
    if (!value || typeof value !== 'object') return false;
    if (Array.isArray(value)) return value.some(containsForbidden);
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      if (key === 'uri' || key === 'extensions') return true;
      if (key === 'extensionsUsed' || key === 'extensionsRequired') {
        if (!Array.isArray(child) || child.length > 0) return true;
      }
      if (containsForbidden(child)) return true;
    }
    return false;
  }
  if (containsForbidden(json)) invalid();

  const document = json as Record<string, unknown>;
  if (!document.asset || typeof document.asset !== 'object') invalid();
  const asset = document.asset as Record<string, unknown>;
  if (asset.version !== '2.0') invalid();
  const buffers = Array.isArray(document.buffers) ? document.buffers : [];
  if (buffers.length !== 1 || buffers[0] == null || typeof buffers[0] !== 'object') invalid();
  const firstBuffer = buffers[0] as Record<string, unknown>;
  if (!Number.isSafeInteger(firstBuffer.byteLength) || (firstBuffer.byteLength as number) < 0 || (firstBuffer.byteLength as number) > binLength) invalid();

  const bufferViews = Array.isArray(document.bufferViews) ? document.bufferViews : [];
  for (const rawView of bufferViews) {
    if (!rawView || typeof rawView !== 'object') invalid();
    const viewDef = rawView as Record<string, unknown>;
    const bufferIndex = viewDef.buffer ?? 0;
    const byteOffset = viewDef.byteOffset ?? 0;
    const byteLength = viewDef.byteLength;
    if (bufferIndex !== 0 || !rangeInside(byteOffset, byteLength, firstBuffer.byteLength as number)) invalid();
  }

  const images = Array.isArray(document.images) ? document.images : [];
  for (const rawImage of images) {
    if (!rawImage || typeof rawImage !== 'object') invalid();
    const image = rawImage as Record<string, unknown>;
    if (image.mimeType !== 'image/png' && image.mimeType !== 'image/jpeg') invalid();
    if (!Number.isSafeInteger(image.bufferView) || !rangeInside(0, 0, bufferViews.length)) invalid();
    const imageView = bufferViews[image.bufferView as number] as Record<string, unknown> | undefined;
    if (!imageView || imageView.buffer !== undefined && imageView.buffer !== 0) invalid();
  }

  const componentSizes: Record<number, number> = { 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4 };
  const typeCounts: Record<string, number> = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT2: 4, MAT3: 9, MAT4: 16 };
  const accessors = Array.isArray(document.accessors) ? document.accessors : [];
  for (const rawAccessor of accessors) {
    if (!rawAccessor || typeof rawAccessor !== 'object') invalid();
    const accessor = rawAccessor as Record<string, unknown>;
    const count = accessor.count;
    const componentType = accessor.componentType;
    const type = accessor.type;
    if (!Number.isSafeInteger(count) || (count as number) < 0 || typeof componentType !== 'number' || typeof type !== 'string') invalid();
    const componentSize = componentSizes[componentType];
    const componentCount = typeCounts[type];
    if (!componentSize || !componentCount) invalid();
    const elementSize = componentCount * componentSize;
    if (accessor.bufferView !== undefined) {
      if (!Number.isSafeInteger(accessor.bufferView) || (accessor.bufferView as number) < 0 || (accessor.bufferView as number) >= bufferViews.length) invalid();
      const rawView = bufferViews[accessor.bufferView as number] as Record<string, unknown>;
      // A strided view spans (count - 1) strides plus the final element; a tightly packed one spans count elements.
      const stride = rawView.byteStride;
      if (stride !== undefined && (!Number.isSafeInteger(stride) || (stride as number) < elementSize)) invalid();
      const byteLength = stride !== undefined && (count as number) > 0
        ? ((count as number) - 1) * (stride as number) + elementSize
        : (count as number) * elementSize;
      if (!Number.isSafeInteger(byteLength) || byteLength < 0) invalid();
      const viewOffset = (rawView.byteOffset ?? 0) as number;
      const viewLength = rawView.byteLength as number;
      const accessorOffset = (accessor.byteOffset ?? 0) as number;
      if (!rangeInside(viewOffset + accessorOffset, byteLength, viewOffset + viewLength)) invalid();
      if (viewOffset + accessorOffset + byteLength > (firstBuffer.byteLength as number)) invalid();
    }
    if (accessor.sparse !== undefined) {
      if (!accessor.sparse || typeof accessor.sparse !== 'object' || Array.isArray(accessor.sparse)) invalid();
      const sparse = accessor.sparse as Record<string, unknown>;
      const sparseCount = sparse.count;
      if (!Number.isSafeInteger(sparseCount) || (sparseCount as number) <= 0 || (sparseCount as number) > (count as number)) invalid();

      const sparseIndices = sparse.indices;
      const sparseValues = sparse.values;
      if (!sparseIndices || typeof sparseIndices !== 'object' || Array.isArray(sparseIndices)) invalid();
      if (!sparseValues || typeof sparseValues !== 'object' || Array.isArray(sparseValues)) invalid();

      const indices = sparseIndices as Record<string, unknown>;
      const values = sparseValues as Record<string, unknown>;
      const indexComponentSize = indices.componentType === 5121 || indices.componentType === 5123 || indices.componentType === 5125
        ? componentSizes[indices.componentType as number]
        : undefined;
      if (!indexComponentSize) invalid();

      for (const sparsePart of [indices, values]) {
        const part = sparsePart as Record<string, unknown>;
        if (!Number.isSafeInteger(part.bufferView) || (part.bufferView as number) < 0 || (part.bufferView as number) >= bufferViews.length) invalid();
        const byteOffset = part.byteOffset === undefined ? 0 : part.byteOffset;
        if (!Number.isSafeInteger(byteOffset) || (byteOffset as number) < 0) invalid();
        const rawView = bufferViews[part.bufferView as number] as Record<string, unknown>;
        if (rawView.buffer !== undefined && rawView.buffer !== 0) invalid();
        const viewOffset = rawView.byteOffset ?? 0;
        if (!Number.isSafeInteger(viewOffset) || (viewOffset as number) < 0) invalid();
        const viewLength = rawView.byteLength;
        if (!rangeInside(viewOffset, viewLength, firstBuffer.byteLength as number)) invalid();

        const sparseByteLength = sparsePart === indices
          ? (sparseCount as number) * indexComponentSize
          : (sparseCount as number) * elementSize;
        if (!rangeInside(byteOffset as number, sparseByteLength, viewLength as number)) invalid();
      }
    }
  }
  return true;
}

export const validateGlb = validateHangarGlb;
export const validateGlbStructure = validateHangarGlb;
export const validateHangarGlbStructure = validateHangarGlb;
export const validateHangarGlbBytes = validateHangarGlb;
export const validateGlbAsset = validateHangarGlb;
export const isHangarGlbValid = (input: unknown, limits?: HangarGlbLimits): boolean => {
  try { validateHangarGlb(input, limits); return true; } catch { return false; }
};
export function validateHangarGlbResult(input: unknown, limits?: HangarGlbLimits): { valid: true } | { valid: false; reason: typeof HANGAR_ASSET_INVALID } {
  try { validateHangarGlb(input, limits); return { valid: true }; }
  catch { return { valid: false, reason: HANGAR_ASSET_INVALID }; }
}
