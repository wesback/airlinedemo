import { describe, expect, it, vi } from 'vitest';
import {
  HANGAR_ASSET_INVALID,
  validateHangarGlb,
  validateHangarGlbResult,
} from '../src/hangar/glbValidator';
import {
  assembleGlb,
  binChunk,
  buildGlb,
  jsonChunk,
  nestedDocument,
  text,
  validDocument,
  type GlbChunk,
} from './hangarGlbBuilder';

const INVALID = { valid: false, reason: 'asset_invalid' };

/** Runs the validator and counts what a renderer loader would have been given. */
function check(input: unknown, limits?: Parameters<typeof validateHangarGlbResult>[1]) {
  const loader = vi.fn();
  const result = validateHangarGlbResult(input, limits);
  if (result.valid) loader(input);
  return { result, loaderCalls: loader.mock.calls.length };
}

const withBytes = (bytes: Uint8Array, edit: (view: DataView) => void) => {
  const copy = new Uint8Array(bytes);
  edit(new DataView(copy.buffer));
  return copy;
};

describe('Hangar GLB structural validation', () => {
  it('GLB_valid_contract_accepts_embedded_v2_fixture', () => {
    const valid = buildGlb();
    const checked = check(valid);
    expect(checked.result).toEqual({ valid: true });
    expect(validateHangarGlb(valid)).toBe(true);
    expect(checked.loaderCalls).toBe(1);
  });

  it('GLB_structure_contract_table', () => {
    const valid = buildGlb();
    const json = jsonChunk(validDocument());
    const bin = binChunk();
    const stridedDocument = validDocument({
      buffers: [{ byteLength: 20 }],
      bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: 20, byteStride: 12 }],
      accessors: [{ bufferView: 0, componentType: 5126, count: 2, type: 'VEC2' }],
    });
    const tooLongStride = validDocument({
      buffers: [{ byteLength: 20 }],
      bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: 20, byteStride: 16 }],
      accessors: [{ bufferView: 0, componentType: 5126, count: 2, type: 'VEC2' }],
    });

    const rows: Array<{ name: string; input: Uint8Array; valid: boolean }> = [
      { name: 'valid embedded v2', input: valid, valid: true },
      { name: 'wrong magic', input: withBytes(valid, (v) => v.setUint32(0, 0, true)), valid: false },
      { name: 'version 1', input: withBytes(valid, (v) => v.setUint32(4, 1, true)), valid: false },
      { name: 'header shorter than 12 bytes', input: valid.slice(0, 11), valid: false },
      { name: 'chunk shorter than declared', input: valid.slice(0, -4), valid: false },
      { name: 'trailing byte', input: assembleGlb([json, bin], { trailing: 1 }), valid: false },
      { name: 'declared length off by one', input: assembleGlb([json, bin], { lengthDelta: 1 }), valid: false },
      { name: 'second JSON chunk', input: assembleGlb([json, json, bin]), valid: false },
      { name: 'BIN before JSON', input: assembleGlb([bin, json]), valid: false },
      { name: 'two BIN chunks', input: assembleGlb([json, bin, bin]), valid: false },
      { name: 'no BIN chunk', input: assembleGlb([json]), valid: false },
      { name: 'invalid UTF-8 JSON', input: assembleGlb([{ type: json.type, data: new Uint8Array([0xff, 0xfe, 0x20, 0x20]) }, bin]), valid: false },
      { name: 'malformed JSON', input: assembleGlb([jsonChunk('{"asset":'), bin]), valid: false },
      { name: 'JSON chunk of 1,048,580 bytes', input: assembleGlb([{ type: json.type, data: new Uint8Array(1_048_580).fill(0x20) }, bin]), valid: false },
      { name: 'file of 5,242,884 bytes', input: assembleGlb([json, { ...bin, data: new Uint8Array(5_242_884) }]), valid: false },
      { name: 'root uri', input: buildGlb(validDocument({ uri: 'a.bin' })), valid: false },
      { name: 'nested uri', input: buildGlb(validDocument({ buffers: [{ byteLength: 24, uri: 'a.bin' }] })), valid: false },
      { name: 'extensionsUsed', input: buildGlb(validDocument({ extensionsUsed: ['KHR_x'] })), valid: false },
      { name: 'extensionsRequired', input: buildGlb(validDocument({ extensionsRequired: ['KHR_x'] })), valid: false },
      { name: 'extensions object', input: buildGlb(validDocument({ extensions: {} })), valid: false },
      { name: 'image MIME image/gltf-binary', input: buildGlb(validDocument({ images: [{ bufferView: 0, mimeType: 'image/gltf-binary' }] })), valid: false },
      { name: 'bufferView beyond buffer', input: buildGlb(validDocument({ bufferViews: [{ buffer: 0, byteOffset: 8, byteLength: 24 }] })), valid: false },
      { name: 'accessor beyond bufferView', input: buildGlb(validDocument({ accessors: [{ bufferView: 0, componentType: 5126, count: 3, type: 'VEC3' }] })), valid: false },
      { name: 'strided accessor ending exactly at the view boundary', input: buildGlb(stridedDocument, binChunk(20)), valid: true },
      { name: 'strided accessor one stride too long', input: buildGlb(tooLongStride, binChunk(20)), valid: false },
      { name: 'sparse indices exceed bufferView', input: buildGlb(validDocument({
        bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: 24 }, { buffer: 0, byteOffset: 24, byteLength: 4 }],
        buffers: [{ byteLength: 28 }],
        accessors: [{ bufferView: 0, componentType: 5126, count: 2, type: 'VEC2', sparse: { count: 2, indices: { bufferView: 1, componentType: 5123, byteOffset: 1 }, values: { bufferView: 0, byteOffset: 8 } } }],
      }), binChunk(28)), valid: false },
      { name: 'sparse values exceed bufferView', input: buildGlb(validDocument({
        bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: 24 }],
        accessors: [{ bufferView: 0, componentType: 5126, count: 2, type: 'VEC2', sparse: { count: 2, indices: { bufferView: 0, componentType: 5123 }, values: { bufferView: 0, byteOffset: 20 } } }],
      })), valid: false },
      { name: 'JSON nested to depth 128', input: buildGlb(nestedDocument(128)), valid: true },
      { name: 'JSON nested to depth 129', input: buildGlb(nestedDocument(129)), valid: false },
    ];

    for (const row of rows) {
      const checked = check(row.input);
      if (row.valid) {
        expect(checked.result, row.name).toEqual({ valid: true });
        expect(checked.loaderCalls, row.name).toBe(1);
      } else {
        expect(checked.result, row.name).toEqual(INVALID);
        expect(checked.result, row.name).toHaveProperty('reason', HANGAR_ASSET_INVALID);
        expect(checked.loaderCalls, row.name).toBe(0);
      }
    }
  });

  it('GLB_deep_json_never_throws_out_of_the_validator', () => {
    const depth = 100_000;
    const deep = `{"asset":{"version":"2.0"},"extras":${'['.repeat(depth)}${']'.repeat(depth)}}`;
    expect(() => validateHangarGlbResult(buildGlb(deep))).not.toThrow();
    expect(validateHangarGlbResult(buildGlb(deep))).toEqual(INVALID);
  });

  it('GLB_input_and_limits_contract', () => {
    const valid = buildGlb();
    for (const input of [null, new ArrayBuffer(8), new Uint8Array()]) {
      const checked = check(input, { maxJsonBytes: 1_048_576, maxTotalBytes: 5_242_880 });
      expect(checked.result).toEqual(INVALID);
      expect(checked.loaderCalls).toBe(0);
    }
    for (const limit of [-1, 1.5, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
      for (const limits of [{ maxJsonBytes: limit }, { maxTotalBytes: limit }]) {
        const checked = check(valid, limits);
        expect(checked.result, JSON.stringify(limits)).toEqual(INVALID);
        expect(checked.loaderCalls).toBe(0);
      }
    }
    expect(check(valid, { maxJsonBytes: 1_048_576, maxTotalBytes: 5_242_880 }).result).toEqual({ valid: true });
  });
});
