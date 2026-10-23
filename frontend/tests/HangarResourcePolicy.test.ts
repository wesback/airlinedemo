import { describe, expect, it, vi } from 'vitest';
import { HANGAR_RESOURCE_REJECTION, validateHangarResourcePolicy } from '../src/hangar/assetPolicy';

describe('Hangar resource policy', () => {
  it('same_origin_resource_contract', () => {
    const transport = vi.fn();
    for (const resource of [
      'https://cdn.example/asset.bin', '//cdn.example/asset.bin', 'http://example/asset.bin',
      'data:image/png;base64,AAAA', { uri: 'assets/model.bin' }, { path: 'assets/font.woff2', kind: 'font' },
      { url: 'https://cdn.example/texture.png', kind: 'texture' },
      { path: 'assets/hangar/model.bin', nested: { location: 'assets\\external\\model.bin' } },
    ]) {
      expect(() => validateHangarResourcePolicy(resource)).toThrow(HANGAR_RESOURCE_REJECTION);
      expect(transport).not.toHaveBeenCalled();
    }
    expect(validateHangarResourcePolicy({ path: 'assets/hangar/synthetic.bin', type: 'resource' })).toBe(true);
  });
});
