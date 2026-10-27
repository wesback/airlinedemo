import { describe, expect, it } from 'vitest';
import manifest from '../package.json';
import lock from '../package-lock.json';
import { HANGAR_DEPENDENCY_PIN_ERROR, validateHangarDependencyPins } from '../src/hangar/dependencyPins';

declare global {
  interface ImportMeta {
    glob: (pattern: string, options: { eager: boolean; query: string; import: string }) => Record<string, string>;
  }
}

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

  it('Fluent_dependency_pins_match_lockfile', () => {
    expect(manifest.dependencies['@fluentui/react-components']).toBe('9.74.11');
    expect(manifest.dependencies['@fluentui/react-icons']).toBe('2.0.344');
    expect(lock.packages['node_modules/@fluentui/react-components'].version).toBe('9.74.11');
    expect(lock.packages['node_modules/@fluentui/react-icons'].version).toBe('2.0.344');
    expect(Object.keys(manifest.dependencies).sort()).toEqual(['@fluentui/react-components', '@fluentui/react-icons', '@react-three/fiber', 'react', 'react-dom', 'three']);
    const mismatch = structuredClone(manifest);
    mismatch.dependencies['@fluentui/react-components'] = '9.74.10';
    expect(mismatch.dependencies['@fluentui/react-components']).not.toBe(lock.packages['node_modules/@fluentui/react-components'].version);
    const extra = structuredClone(manifest);
    (extra.dependencies as Record<string, string>)['example-runtime'] = '1.0.0';
    expect(Object.keys(extra.dependencies).filter((name) => !['@fluentui/react-components', '@fluentui/react-icons', '@react-three/fiber', 'react', 'react-dom', 'three'].includes(name))).toEqual(['example-runtime']);
  });

  it('Colour_literals_and_fluent_tokens_contract', () => {
    const sourceFiles = import.meta.glob('../src/**/*.{ts,tsx,css}', { eager: true, query: '?raw', import: 'default' });
    const literalPattern = /#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/i;
    const violations = Object.entries(sourceFiles).flatMap(([file, source]) => {
      if (file.endsWith('/src/theme/altivaneTheme.ts')) return [];
      return source.split('\n').flatMap((line: string, index: number) => literalPattern.test(line) ? [`${file}:${index + 1}:${line.trim()}`] : []);
    });
    expect(violations).toEqual([]);

    const fluentImports = Object.entries(sourceFiles).filter(([, source]) => source.includes('@fluentui/react-components'));
    expect(fluentImports.length).toBeGreaterThan(0);
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
