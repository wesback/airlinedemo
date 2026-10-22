export const HANGAR_DEPENDENCY_PINS = {
  three: '0.180.0',
  '@react-three/fiber': '9.8.1',
} as const;

export type DependencyManifest = {
  dependencies?: Record<string, string>;
  devDependencies?: Record<string, string>;
};

export type DependencyLockfile = {
  packages?: Record<string, { version?: string }>;
};

export const HANGAR_DEPENDENCY_PIN_ERROR = 'Hangar dependency pin mismatch';

export function validateHangarDependencyPins(
  manifest?: DependencyManifest | null,
  lockfile?: DependencyLockfile | null,
): boolean {
  if (!manifest || !lockfile?.packages) throw new Error(HANGAR_DEPENDENCY_PIN_ERROR);

  const dependencies = manifest.dependencies ?? {};
  for (const [name, expected] of Object.entries(HANGAR_DEPENDENCY_PINS)) {
    const pinned = dependencies[name];
    if (typeof pinned !== 'string' || pinned !== expected) {
      throw new Error(HANGAR_DEPENDENCY_PIN_ERROR);
    }

    const resolved = lockfile.packages[`node_modules/${name}`]?.version;
    if (typeof resolved !== 'string' || resolved !== expected) {
      throw new Error(HANGAR_DEPENDENCY_PIN_ERROR);
    }
  }

  return true;
}

export const validateDependencyPins = validateHangarDependencyPins;
