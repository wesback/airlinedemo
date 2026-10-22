import { describe, expect, it } from 'vitest';
import manifest from '../package.json';
import lock from '../package-lock.json';
import { HANGAR_DEPENDENCY_PIN_ERROR, validateHangarDependencyPins } from '../src/hangar/dependencyPins';

describe('dependency audit versions', () => {
  it('keeps the manifest and lockfile on a Vite version outside the affected range', () => {
    const manifestVersion = manifest.devDependencies.vite;
    const lockVersion = lock.packages['node_modules/vite'].version;

    expect(manifestVersion).toBe(lockVersion);
    expect(['8.3.0', '8.3.1', '8.3.2']).not.toContain(lockVersion);
  });

  it('hangar dependency pins reject ranges and version mismatch', () => {
    expect(validateHangarDependencyPins(manifest, lock)).toBe(true);

    const ranges: Array<{ dependencies: Record<string, string> }> = [
      { dependencies: { three: '^0.180.0', '@react-three/fiber': '9.8.1' } },
      { dependencies: { three: '~0.180.0', '@react-three/fiber': '9.8.1' } },
      { dependencies: { three: '0.180.0' } },
    ];
    for (const badManifest of ranges) {
      expect(() => validateHangarDependencyPins(badManifest, lock)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);
    }

    const mismatch = structuredClone(lock);
    mismatch.packages['node_modules/three'].version = '0.179.1';
    expect(() => validateHangarDependencyPins(manifest, mismatch)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);

    const missing = structuredClone(lock);
    Reflect.deleteProperty(missing.packages, 'node_modules/@react-three/fiber');
    expect(() => validateHangarDependencyPins(manifest, missing)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);
  });

  it('hangar pin contract cases list exact inputs and results', () => {
    const exactManifest = {
      dependencies: { three: '0.180.0', '@react-three/fiber': '9.8.1' },
    };
    const exactLock = {
      packages: {
        'node_modules/three': { version: '0.180.0' },
        'node_modules/@react-three/fiber': { version: '9.8.1' },
      },
    };

    const cases: Array<{ manifest: unknown; lockfile: unknown; valid: boolean }> = [
      { manifest: exactManifest, lockfile: exactLock, valid: true },
      { manifest: { dependencies: { three: '^0.180.0', '@react-three/fiber': '9.8.1' } }, lockfile: exactLock, valid: false },
      { manifest: { dependencies: { three: '~0.180.0', '@react-three/fiber': '9.8.1' } }, lockfile: exactLock, valid: false },
      { manifest: exactManifest, lockfile: { packages: { 'node_modules/three': { version: '0.179.1' }, 'node_modules/@react-three/fiber': { version: '9.8.1' } } }, valid: false },
      { manifest: exactManifest, lockfile: { packages: { 'node_modules/three': { version: '0.180.0' } } }, valid: false },
      { manifest: undefined, lockfile: exactLock, valid: false },
      { manifest: exactManifest, lockfile: undefined, valid: false },
    ];

    for (const contractCase of cases) {
      if (contractCase.valid) {
        expect(validateHangarDependencyPins(contractCase.manifest as never, contractCase.lockfile as never)).toBe(true);
      } else {
        expect(() => validateHangarDependencyPins(contractCase.manifest as never, contractCase.lockfile as never)).toThrow(HANGAR_DEPENDENCY_PIN_ERROR);
      }
    }
  });
});
