import { hangarSceneColors } from '../theme/altivaneTheme';

export const HANGAR_SCENE_THEME = {
  background: hangarSceneColors.background,
  floor: hangarSceneColors.floor,
  floorMarking: hangarSceneColors.floorMarking,
  aircraftBody: hangarSceneColors.aircraftBody,
  aircraftTrim: hangarSceneColors.aircraftTrim,
  partsStand: hangarSceneColors.partsStand,
  component: hangarSceneColors.component,
  rack: hangarSceneColors.rack,
  rackUnit: hangarSceneColors.rackUnit,
  avatarBody: hangarSceneColors.avatarBody,
  groundShadow: hangarSceneColors.groundShadow,
  guideLine: hangarSceneColors.guideLine,
  selection: hangarSceneColors.selection,
  labelBackground: hangarSceneColors.labelBackground,
  labelText: hangarSceneColors.labelText,
  labelMuted: hangarSceneColors.labelMuted,
} as const;

export const background = HANGAR_SCENE_THEME.background;
export const floor = HANGAR_SCENE_THEME.floor;
export const floorMarking = HANGAR_SCENE_THEME.floorMarking;
export const aircraftBody = HANGAR_SCENE_THEME.aircraftBody;
export const aircraftTrim = HANGAR_SCENE_THEME.aircraftTrim;
export const partsStand = HANGAR_SCENE_THEME.partsStand;
export const component = HANGAR_SCENE_THEME.component;
export const rack = HANGAR_SCENE_THEME.rack;
export const rackUnit = HANGAR_SCENE_THEME.rackUnit;
export const avatarBody = HANGAR_SCENE_THEME.avatarBody;
export const groundShadow = HANGAR_SCENE_THEME.groundShadow;
export const guideLine = HANGAR_SCENE_THEME.guideLine;
export const selection = HANGAR_SCENE_THEME.selection;
export const labelBackground = HANGAR_SCENE_THEME.labelBackground;
export const labelText = HANGAR_SCENE_THEME.labelText;
export const labelMuted = HANGAR_SCENE_THEME.labelMuted;

export const sceneTheme = HANGAR_SCENE_THEME;

export function isApprovedHangarSceneColor(color: unknown): boolean {
  return typeof color === 'string' && Object.values(HANGAR_SCENE_THEME).includes(color as typeof HANGAR_SCENE_THEME[keyof typeof HANGAR_SCENE_THEME]);
}

export function validateHangarSceneColor(color: unknown): { valid: true } | { valid: false; error: 'Visible mapping error' } {
  return isApprovedHangarSceneColor(color) ? { valid: true } : { valid: false, error: 'Visible mapping error' };
}

export function hangarLabelContrastRatio(foreground: string, background = HANGAR_SCENE_THEME.labelBackground): number {
  const luminance = (hex: string) => {
    const value = hex.replace('#', '').trim();
    if (!/^([0-9a-f]{6})$/i.test(value)) return NaN;
    const rgb = [0, 2, 4].map(i => parseInt(value.slice(i, i + 2), 16) / 255).map(v => v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
  };
  const a = luminance(foreground); const b = luminance(background);
  if (!Number.isFinite(a) || !Number.isFinite(b)) return 0;
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}
