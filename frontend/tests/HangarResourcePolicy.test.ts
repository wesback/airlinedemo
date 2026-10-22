import { describe, expect, it, vi } from 'vitest';
import { HANGAR_RESOURCE_REJECTION, validateHangarResourcePolicy } from '../src/hangar/assetPolicy';

describe('Hangar resource policy', () => {
  it('same_origin_resource_contract', () => {
    const transport = vi.fn();
    expect(validateHangarResourcePolicy({ path: 'assets/hangar/model.glb', type: 'model' })).toBe(true);
    expect(validateHangarResourcePolicy({ path: 'assets/hangar/model.glb' })).toBe(true);
    for (const resource of [
      'https://cdn.example/model.glb', '//cdn.example/model.glb', 'http://example/model.glb',
      'data:image/png;base64,AAAA', { uri: 'assets/model.bin' }, { path: 'assets/font.woff2', kind: 'font' },
      { url: 'https://cdn.example/texture.png', kind: 'texture' }, { url: 'model.glb', kind: 'model' },
    ]) {
      expect(() => validateHangarResourcePolicy(resource)).toThrow(HANGAR_RESOURCE_REJECTION);
      expect(transport).not.toHaveBeenCalled();
    }
  });
});
