const MAGIC = 0x46546c67;
const JSON_CHUNK = 0x4e4f534a;
const BIN_CHUNK = 0x004e4942;

export type GlbChunk = { type: number; data: Uint8Array };

export const text = (value: string) => new TextEncoder().encode(value);

/** Pads JSON with spaces and binary data with zeros to the 4-byte chunk alignment. */
function pad(data: Uint8Array, fill: number): Uint8Array {
  const padded = new Uint8Array(Math.ceil(data.length / 4) * 4).fill(fill);
  padded.set(data);
  return padded;
}

export const jsonChunk = (document: unknown): GlbChunk => ({ type: JSON_CHUNK, data: pad(typeof document === 'string' ? text(document) : text(JSON.stringify(document)), 0x20) });
export const binChunk = (length = 24): GlbChunk => ({ type: BIN_CHUNK, data: new Uint8Array(length) });

/** The smallest valid embedded version-2 document: one buffer, one view, one accessor. */
export function validDocument(overrides: Record<string, unknown> = {}) {
  return {
    asset: { version: '2.0' },
    buffers: [{ byteLength: 24 }],
    bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: 24 }],
    accessors: [{ bufferView: 0, componentType: 5126, count: 2, type: 'VEC3' }],
    ...overrides,
  };
}

export function assembleGlb(chunks: GlbChunk[], { version = 2, magic = MAGIC, lengthDelta = 0, trailing = 0 } = {}): Uint8Array {
  const total = 12 + chunks.reduce((sum, chunk) => sum + 8 + chunk.data.length, 0) + trailing;
  const out = new Uint8Array(total);
  const view = new DataView(out.buffer);
  view.setUint32(0, magic, true);
  view.setUint32(4, version, true);
  view.setUint32(8, total + lengthDelta, true);
  let offset = 12;
  for (const chunk of chunks) {
    view.setUint32(offset, chunk.data.length, true);
    view.setUint32(offset + 4, chunk.type, true);
    out.set(chunk.data, offset + 8);
    offset += 8 + chunk.data.length;
  }
  return out;
}

export const buildGlb = (document: unknown = validDocument(), bin = binChunk()) => assembleGlb([jsonChunk(document), bin]);

/** A document whose JSON containers nest to exactly `depth` (the root object counts as 1). */
export function nestedDocument(depth: number) {
  let extras: unknown = {};
  for (let level = 2; level < depth; level += 1) extras = { child: extras };
  return validDocument(depth >= 2 ? { extras } : {});
}
