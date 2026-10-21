import { describe, expect, it } from 'vitest';
import manifest from '../package.json';
import lock from '../package-lock.json';

describe('dependency audit versions', () => {
  it('keeps the manifest and lockfile on a Vite version outside the affected range', () => {
    const manifestVersion = manifest.devDependencies.vite;
    const lockVersion = lock.packages['node_modules/vite'].version;

    expect(manifestVersion).toBe(lockVersion);
    expect(['8.3.0', '8.3.1', '8.3.2']).not.toContain(lockVersion);
  });
});
