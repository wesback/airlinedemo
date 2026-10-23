import { describe, expect, it } from 'vitest';
import { HANGAR_ASSET_BUDGET_BYTES, HANGAR_JAVASCRIPT_GZIP_BUDGET_BYTES, validateHangarBuildBudgets } from '../src/hangar/buildBudgets';

describe('Hangar build budgets', () => {
  it('Hangar build budgets exact boundary contract', () => {
    expect(validateHangarBuildBudgets({
      hangarOnlyGzipJavaScriptBytes: HANGAR_JAVASCRIPT_GZIP_BUDGET_BYTES,
      assetBytes: HANGAR_ASSET_BUDGET_BYTES,
      dependencyChunkMappings: { 'hangar.js': { hangarOnly: true } },
    })).toBe(true);
    expect(() => validateHangarBuildBudgets({
      hangarOnlyGzipJavaScriptBytes: HANGAR_JAVASCRIPT_GZIP_BUDGET_BYTES + 1,
      assetBytes: HANGAR_ASSET_BUDGET_BYTES,
      dependencyChunkMappings: { 'hangar.js': { hangarOnly: true } },
    })).toThrow();
    expect(() => validateHangarBuildBudgets({
      hangarOnlyGzipJavaScriptBytes: HANGAR_JAVASCRIPT_GZIP_BUDGET_BYTES,
      assetBytes: HANGAR_ASSET_BUDGET_BYTES + 1,
      dependencyChunkMappings: { 'hangar.js': { hangarOnly: true } },
    })).toThrow();
    expect(() => validateHangarBuildBudgets({
      chunks: [{ file: 'hangar.js', gzipBytes: 100 }],
      assets: [],
      dependencyChunkMappings: { 'hangar.js': { classification: undefined } },
    })).toThrow();
  });

  it('Hangar build budgets sum all classified chunks', () => {
    expect(validateHangarBuildBudgets({
      chunks: [
        { file: 'hangar-a.js', gzipBytes: 786432, hangarOnly: true },
        { file: 'hangar-b.js', gzipBytes: 786432, hangarOnly: true },
      ],
      assets: [],
      dependencyChunkMappings: { 'hangar-a.js': 'hangar-only', 'hangar-b.js': 'hangar-only' },
    })).toBe(true);
    expect(() => validateHangarBuildBudgets({
      chunks: [
        { file: 'hangar-a.js', gzipBytes: 786433, hangarOnly: true },
        { file: 'hangar-b.js', gzipBytes: 786432, hangarOnly: true },
      ],
      assets: [],
      dependencyChunkMappings: { 'hangar-a.js': 'hangar-only', 'hangar-b.js': 'hangar-only' },
    })).toThrow();
    expect(() => validateHangarBuildBudgets({
      chunks: [],
      assets: [],
      dependencyChunkMappings: null,
    })).toThrow();
    expect(() => validateHangarBuildBudgets()).toThrow();
  });
});
