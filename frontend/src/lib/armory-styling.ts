import type { CSSProperties } from 'react'
import type { ArmoryStyleTemplate, ArmoryStylingDto } from '@/types/armory.types'

export const STYLING_TEMPLATE_COPY: Record<ArmoryStyleTemplate, { label: string; description: string }> = {
  Classic: {
    label: 'Classic',
    description: 'No generated theme overrides. The armory stays exactly as it is today.',
  },
  Tbc: {
    label: 'The Burning Crusade',
    description: 'Grey-dark surfaces with fel green accents and Outland-inspired highlights.',
  },
  Wotlk: {
    label: 'Wrath of the Lich King',
    description: 'Dark frozen surfaces with icy blue highlights and Lich King tones.',
  },
  Custom: {
    label: 'Custom',
    description: 'Start from your own colors and wallpaper for a fully customized armory theme.',
  },
}

/**
 * Minimal Classic palette used as an immediate fallback before the backend defaults
 * API responds. Must match the values in theme.css :root and ArmoryStylingTheme.cs.
 */
export const CLASSIC_STYLING_FALLBACK: ArmoryStylingDto = {
  template: 'Classic',
  advancedEnabled: false,
  primaryColor: '#8a5a24',
  secondaryColor: '#3a2412',
  accentColor: '#d8a84f',
  backgroundColor: '#1b1209',
  surfaceColor: '#2a1a0c',
  panelColor: '#2b2114',
  borderColor: '#5a4628',
  navbarColor: '#241408',
  linkColor: '#f4d68a',
  headingColor: '#ffd980',
  mutedTextColor: '#b3a384',
  inputColor: '#1c1209',
  buttonTextColor: '#fff3d1',
  textColor: '#e8dcc4',
  wallpaperUrl: '/img/bg/wallpaper_classic.jpg',
}

/**
 * Resolves preset template colors. When backend defaults are available, uses those;
 * otherwise falls back to Classic. Custom or advanced-enabled styling passes through.
 */
export function resolveEffectiveArmoryStyling(
  styling: ArmoryStylingDto,
  defaults?: Record<string, ArmoryStylingDto>,
): ArmoryStylingDto {
  if (styling.template === 'Custom' || styling.advancedEnabled) {
    return styling
  }
  const templateDefaults = defaults?.[styling.template] ?? CLASSIC_STYLING_FALLBACK
  return {
    ...templateDefaults,
    wallpaperUrl: templateDefaults.wallpaperUrl,
  }
}

export function armoryPreviewCssVars(
  styling: ArmoryStylingDto,
  defaults?: Record<string, ArmoryStylingDto>,
): CSSProperties {
  const colors = resolveEffectiveArmoryStyling(styling, defaults)
  return {
    '--armory-primary': colors.primaryColor,
    '--armory-secondary': colors.secondaryColor,
    '--armory-accent': colors.accentColor,
    '--armory-bg': colors.backgroundColor,
    '--armory-surface': colors.surfaceColor,
    '--armory-panel': colors.panelColor,
    '--armory-border': colors.borderColor,
    '--armory-navbar': colors.navbarColor,
    '--armory-link': colors.linkColor,
    '--armory-heading': colors.headingColor,
    '--armory-text-muted': colors.mutedTextColor,
    '--armory-input': colors.inputColor,
    '--armory-button-text': colors.buttonTextColor,
    '--armory-text': colors.textColor,
    '--armory-panel-highlight': 'color-mix(in srgb, var(--armory-panel) 55%, var(--armory-border))',
    '--armory-border-bright': 'color-mix(in srgb, var(--armory-border) 55%, var(--armory-accent))',
  } as CSSProperties
}

export function resolveArmoryWallpaperPreviewUrl(
  wallpaperUrl: string | null | undefined,
  stackId?: string,
): string | undefined {
  if (!wallpaperUrl) return undefined
  if (wallpaperUrl.includes('azp-wallpaper') && stackId) {
    return `/api/stacks/${stackId}/armory-assets/styling/wallpaper`
  }
  return wallpaperUrl
}

export function armoryPreviewWallpaperStyle(
  styling: ArmoryStylingDto,
  defaults?: Record<string, ArmoryStylingDto>,
  stackId?: string,
): CSSProperties | undefined {
  const colors = resolveEffectiveArmoryStyling(styling, defaults)
  const previewUrl = resolveArmoryWallpaperPreviewUrl(colors.wallpaperUrl, stackId)
  if (!previewUrl) return undefined
  return {
    backgroundImage: `linear-gradient(color-mix(in srgb, var(--armory-bg) 82%, transparent), color-mix(in srgb, var(--armory-bg) 88%, transparent)), url('${previewUrl}')`,
    backgroundSize: 'cover',
    backgroundPosition: 'center top',
  }
}
