import type { CSSProperties } from 'react'
import type { LauncherTemplateDto } from '@/types/launcher.types'

const DEFAULT_LAUNCHER_ACCENT = '#4fa8d8'

/** Effective template id: per-stack override wins, then global, then WotLK. */
export function resolveEffectiveLauncherTemplateId(
  globalTemplate?: string | null,
  stackTemplate?: string | null,
): string {
  const stack = stackTemplate?.trim()
  if (stack) return stack
  const global = globalTemplate?.trim()
  if (global) return global
  return 'wotlk'
}

export function resolveLauncherAccentColor(
  templates: LauncherTemplateDto[] | undefined,
  globalTemplate?: string | null,
  stackTemplate?: string | null,
): string {
  const id = resolveEffectiveLauncherTemplateId(globalTemplate, stackTemplate)
  return templates?.find((t) => t.id === id)?.accentColor ?? DEFAULT_LAUNCHER_ACCENT
}

export function resolveLauncherTemplateName(
  templates: LauncherTemplateDto[] | undefined,
  globalTemplate?: string | null,
  stackTemplate?: string | null,
): string {
  const id = resolveEffectiveLauncherTemplateId(globalTemplate, stackTemplate)
  return templates?.find((t) => t.id === id)?.name ?? id
}

interface LauncherNewsPalette {
  label: string
  title: string
  muted: string
  excerpt: string
  border: string
  cardBg: string
  stripBorder: string
  stripBg: string
  articleBg: string
  articleBorder: string
  articleTitle: string
  articleDate: string
  placeholder: string
}

const LAUNCHER_NEWS_PALETTES: Record<string, LauncherNewsPalette> = {
  classic: {
    label: '#F0C869',
    title: '#FFD980',
    muted: '#B3A384',
    excerpt: '#D4C4A8',
    border: '#5A4628',
    cardBg: 'rgba(51, 43, 33, 0.08)',
    stripBorder: 'rgba(90, 70, 40, 0.45)',
    stripBg: 'linear-gradient(180deg, rgba(21, 16, 10, 0.35) 0%, rgba(18, 13, 7, 0.85) 100%)',
    articleBg: '#15100A',
    articleBorder: '#5A4628',
    articleTitle: '#FFD980',
    articleDate: '#B3A384',
    placeholder: 'rgba(179, 163, 132, 0.7)',
  },
  tbc: {
    label: '#8BC963',
    title: '#A8E06E',
    muted: '#7A9A6A',
    excerpt: '#9CB888',
    border: '#3D5A2E',
    cardBg: 'rgba(40, 60, 30, 0.18)',
    stripBorder: 'rgba(63, 95, 50, 0.55)',
    stripBg: 'linear-gradient(180deg, rgba(12, 20, 8, 0.45) 0%, rgba(8, 14, 6, 0.92) 100%)',
    articleBg: '#0C1408',
    articleBorder: '#3D5A2E',
    articleTitle: '#A8E06E',
    articleDate: '#7A9A6A',
    placeholder: 'rgba(122, 154, 106, 0.7)',
  },
  wotlk: {
    label: '#7EC8EA',
    title: '#A8DDF5',
    muted: '#7A9AAD',
    excerpt: '#9CB8C8',
    border: '#2E4A5A',
    cardBg: 'rgba(30, 50, 70, 0.18)',
    stripBorder: 'rgba(46, 74, 90, 0.55)',
    stripBg: 'linear-gradient(180deg, rgba(8, 14, 22, 0.45) 0%, rgba(6, 10, 18, 0.92) 100%)',
    articleBg: '#0A1018',
    articleBorder: '#2E4A5A',
    articleTitle: '#A8DDF5',
    articleDate: '#7A9AAD',
    placeholder: 'rgba(122, 154, 173, 0.7)',
  },
}

export interface LauncherNewsPreviewTheme {
  accentColor: string
  templateId: string
  templateName: string
  cssVars: CSSProperties
}

export function resolveLauncherNewsPreviewTheme(
  templates: LauncherTemplateDto[] | undefined,
  globalTemplate?: string | null,
  stackTemplate?: string | null,
): LauncherNewsPreviewTheme {
  const templateId = resolveEffectiveLauncherTemplateId(globalTemplate, stackTemplate)
  const template = templates?.find((t) => t.id === templateId)
  const accentColor = template?.accentColor ?? DEFAULT_LAUNCHER_ACCENT
  const palette = LAUNCHER_NEWS_PALETTES[templateId] ?? LAUNCHER_NEWS_PALETTES.wotlk

  return {
    accentColor,
    templateId,
    templateName: template?.name ?? templateId,
    cssVars: {
      '--launcher-news-accent': accentColor,
      '--launcher-news-label': palette.label,
      '--launcher-news-title': palette.title,
      '--launcher-news-muted': palette.muted,
      '--launcher-news-excerpt': palette.excerpt,
      '--launcher-news-border': palette.border,
      '--launcher-news-card-bg': palette.cardBg,
      '--launcher-news-strip-border': palette.stripBorder,
      '--launcher-news-strip-bg': palette.stripBg,
      '--launcher-news-article-bg': palette.articleBg,
      '--launcher-news-article-border': palette.articleBorder,
      '--launcher-news-article-title': palette.articleTitle,
      '--launcher-news-article-date': palette.articleDate,
      '--launcher-news-placeholder': palette.placeholder,
      '--news-accent': accentColor,
    } as CSSProperties,
  }
}
