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

export type DependencyAuditInput = {
  installed?: Record<string, unknown>;
  vulnerabilities?: Record<string, unknown>;
};

export function validateHangarDependencyAudit(
  manifest: DependencyManifest | null | undefined,
  lockfile: DependencyLockfile | null | undefined,
  auditInput?: DependencyAuditInput | null,
): boolean {
  validateHangarDependencyPins(manifest, lockfile);

  const auditedNames = new Set([
    ...Object.keys(auditInput?.installed ?? {}),
    ...Object.keys(auditInput?.vulnerabilities ?? {}),
  ]);
  for (const name of Object.keys(HANGAR_DEPENDENCY_PINS)) {
    const lockName = lockfile?.packages?.[`node_modules/${name}`]?.version;
    if (!lockName || !auditedNames.has(name)) {
      throw new Error(`Hangar audit contract missing package: ${name}`);
    }
  }

  return true;
}
