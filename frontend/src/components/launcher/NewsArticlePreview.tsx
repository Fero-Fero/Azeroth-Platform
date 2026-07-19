import type { CSSProperties } from 'react'
import DOMPurify from 'dompurify'
import { Globe, LayoutGrid, Monitor, Newspaper, Rocket } from 'lucide-react'
import { NEWS_TAG_COLORS, NEWS_TAGS } from '@/components/launcher/NewsEditor'
import '@/components/launcher/newsContent.css'
import '@/components/launcher/newsPreview.css'
import type { LauncherNewsPreviewTheme } from '@/lib/launcher-theme'
import { cn } from '@/lib/utils'

const DEFAULT_ACCENT = '#4fa8d8'

export type NewsPreviewTarget = 'launcher' | 'armory'
export type NewsPreviewMode = 'article' | 'card'

export interface NewsPreviewArticle {
  title: string
  date?: string | null
  tag?: string | null
  html: string
  coverUrl?: string | null
  accentColor?: string
}

function sanitizeHtml(html: string): string {
  return DOMPurify.sanitize(html, {
    USE_PROFILES: { html: true },
    ALLOWED_URI_REGEXP: /^(?:(?:https?|blob):|\/|data:image\/)/i,
  })
}

export function formatNewsTagLabel(tag: string): string {
  const normalized = tag.trim().toLowerCase()
  const match = NEWS_TAGS.find((entry) => entry.value === normalized)
  return match?.label ?? tag
}

/** Builds a short plain-text summary from article HTML for card previews. */
export function extractNewsExcerpt(html: string, maxLength = 110): string {
  const text = html
    .replace(/<[^>]+>/g, ' ')
    .replace(/&nbsp;/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim()

  if (text.length <= maxLength) {
    return text
  }

  return `${text.slice(0, maxLength).trimEnd()}…`
}

/** Canonical launcher reading preview — matches the desktop launcher WebView reading view. */
export function LauncherNewsArticlePreview({
  article,
  accentColor = DEFAULT_ACCENT,
  launcherThemeStyle,
  className,
}: {
  article: NewsPreviewArticle
  accentColor?: string
  launcherThemeStyle?: CSSProperties
  className?: string
}) {
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const tagColor = tag ? NEWS_TAG_COLORS[tag] : undefined
  const sanitizedHtml = sanitizeHtml(article.html)
  const title = article.title?.trim() || 'Untitled'
  const themeStyle = launcherThemeStyle ?? ({ ['--news-accent' as string]: accentColor } as CSSProperties)

  return (
    <div
      className={cn(
        'launcher-news-preview-themed launcher-news-article-preview overflow-hidden rounded-lg border shadow-inner',
        className,
      )}
      style={themeStyle}
    >
      {article.coverUrl && (
        <img src={article.coverUrl} alt="" className="aspect-video w-full object-cover" />
      )}
      <div className="p-5">
        {tag && tagColor && (
          <span
            className="mb-2 inline-block rounded-full px-2.5 py-0.5 text-[10px] font-bold uppercase tracking-wide text-white"
            style={{ backgroundColor: tagColor }}
          >
            {formatNewsTagLabel(tag)}
          </span>
        )}
        <div className="launcher-news-article-preview__title text-xl font-bold">{title}</div>
        {article.date && (
          <div className="launcher-news-article-preview__date mb-3 text-xs">{article.date}</div>
        )}
        <div className="news-content text-[#E8DCC4]" dangerouslySetInnerHTML={{ __html: sanitizedHtml }} />
      </div>
    </div>
  )
}

/** Fixed-size card as shown in the launcher Play tab news strip (156×210). */
export function LauncherNewsCardPreview({
  article,
  accentColor = DEFAULT_ACCENT,
  launcherThemeStyle,
}: {
  article: NewsPreviewArticle
  accentColor?: string
  launcherThemeStyle?: CSSProperties
}) {
  const title = article.title?.trim() || 'Untitled'
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const tagColor = tag ? NEWS_TAG_COLORS[tag] : undefined
  const excerpt = extractNewsExcerpt(article.html)
  const themeStyle = launcherThemeStyle ?? ({ ['--launcher-news-accent' as string]: accentColor } as CSSProperties)

  return (
    <div className="launcher-news-preview-themed launcher-news-preview-strip" style={themeStyle}>
      <div className="launcher-news-preview-strip__label">Latest News</div>
      <div className="launcher-news-card" aria-label={`News card: ${title}`}>
        <div className="launcher-news-card__cover">
          {tag && tagColor && (
            <span className="launcher-news-card__tag" style={{ backgroundColor: tagColor }}>
              {formatNewsTagLabel(tag)}
            </span>
          )}
          {article.coverUrl ? (
            <img src={article.coverUrl} alt="" />
          ) : (
            <div
              className="flex h-full items-center justify-center text-[11px]"
              style={{ color: 'var(--launcher-news-placeholder)' }}
            >
              No cover
            </div>
          )}
        </div>
        <div className="launcher-news-card__body">
          <div className="launcher-news-card__title">{title}</div>
          {article.date && <div className="launcher-news-card__date">{article.date}</div>}
          {excerpt && <div className="launcher-news-card__excerpt">{excerpt}</div>}
        </div>
      </div>
      <p className="launcher-news-preview-strip__caption">
        Launcher card at 156×210 px proportions — scales with preview width.
      </p>
    </div>
  )
}

/** Armory article detail page preview — uses stack armory theme tokens when provided. */
export function ArmoryNewsArticlePreview({
  article,
  className,
}: {
  article: NewsPreviewArticle
  className?: string
}) {
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const sanitizedHtml = sanitizeHtml(article.html)
  const title = article.title?.trim() || 'Untitled'

  return (
    <article className={cn('armory-news-article', className)}>
      {article.coverUrl && (
        <div className="armory-news-article__cover">
          <img src={article.coverUrl} alt="" />
        </div>
      )}
      <h1 className="armory-news-article__title">{title}</h1>
      <div className="armory-news-article__meta">
        {tag && (
          <span className={`armory-news-tag armory-news-tag--${tag}`}>{formatNewsTagLabel(tag)}</span>
        )}
        {article.date && <span className="armory-news-article__date">{article.date}</span>}
      </div>
      <div className="armory-news-article__body" dangerouslySetInnerHTML={{ __html: sanitizedHtml }} />
    </article>
  )
}

/** Armory home/news list card preview — horizontal card from widget-news.hbs. */
export function ArmoryNewsCardPreview({ article }: { article: NewsPreviewArticle }) {
  const title = article.title?.trim() || 'Untitled'
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const excerpt = extractNewsExcerpt(article.html, 160)

  return (
    <div className="armory-news-card-preview">
      <div
        className={cn('armory-news-card', tag && `armory-news-card--${tag}`)}
        aria-label={`Armory news card: ${title}`}
      >
        {article.coverUrl && (
          <div className="armory-news-card__cover">
            <img src={article.coverUrl} alt="" />
          </div>
        )}
        <div className="armory-news-card__body">
          {tag && <span className="armory-news-card__eyebrow">{formatNewsTagLabel(tag)}</span>}
          <div className="armory-news-card__head">
            <span className="armory-news-card__title">{title}</span>
            {article.date && <span className="armory-news-card__date">{article.date}</span>}
          </div>
          {excerpt && <div className="armory-news-card__excerpt">{excerpt}</div>}
          <span className="armory-news-card__readmore">Read more</span>
        </div>
      </div>
    </div>
  )
}

export function NewsPreviewModeTabs({
  mode,
  onModeChange,
  target,
  onTargetChange,
  variant = 'light',
}: {
  mode: NewsPreviewMode
  onModeChange: (mode: NewsPreviewMode) => void
  target?: NewsPreviewTarget
  onTargetChange?: (target: NewsPreviewTarget) => void
  variant?: 'light' | 'dark'
}) {
  const isDark = variant === 'dark'
  const activeClass = isDark ? 'border-blue-500 text-blue-300' : 'border-blue-600 text-blue-700'
  const inactiveClass = isDark
    ? 'border-transparent text-gray-400 hover:text-gray-200'
    : 'border-transparent text-gray-500 hover:text-gray-800'
  const containerClass = isDark ? 'border-gray-800 bg-gray-900/50' : 'border-slate-100 bg-slate-50/80'
  const segmentActive = isDark ? 'bg-gray-800 text-white shadow-sm' : 'bg-white text-slate-900 shadow-sm ring-1 ring-slate-200'
  const segmentInactive = isDark
    ? 'text-gray-400 hover:bg-gray-800/60 hover:text-gray-200'
    : 'text-slate-600 hover:bg-white/70 hover:text-slate-800'

  return (
    <div className="space-y-2">
      {target && onTargetChange && (
        <div
          className={cn(
            'flex gap-1 rounded-lg border p-1',
            isDark ? 'border-gray-800 bg-gray-900/40' : 'border-slate-200 bg-slate-100',
          )}
          role="tablist"
          aria-label="Preview surface"
        >
          <button
            type="button"
            role="tab"
            aria-selected={target === 'launcher'}
            onClick={() => onTargetChange('launcher')}
            className={cn(
              'inline-flex flex-1 items-center justify-center gap-1.5 rounded-md px-2 py-2 text-xs font-semibold transition-colors sm:text-sm',
              target === 'launcher' ? segmentActive : segmentInactive,
            )}
          >
            <Rocket className="h-3.5 w-3.5" />
            Launcher
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={target === 'armory'}
            onClick={() => onTargetChange('armory')}
            className={cn(
              'inline-flex flex-1 items-center justify-center gap-1.5 rounded-md px-2 py-2 text-xs font-semibold transition-colors sm:text-sm',
              target === 'armory' ? segmentActive : segmentInactive,
            )}
          >
            <Globe className="h-3.5 w-3.5" />
            Armory
          </button>
        </div>
      )}

      <div className={cn('flex gap-1 border-b', containerClass)} role="tablist" aria-label="Preview format">
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'article'}
          onClick={() => onModeChange('article')}
          className={cn(
            'inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors -mb-px',
            mode === 'article' ? activeClass : inactiveClass,
          )}
        >
          <Newspaper className="h-4 w-4" />
          Full article
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'card'}
          onClick={() => onModeChange('card')}
          className={cn(
            'inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors -mb-px',
            mode === 'card' ? activeClass : inactiveClass,
          )}
        >
          <LayoutGrid className="h-4 w-4" />
          Card
        </button>
      </div>
    </div>
  )
}

export function NewsPreviewPanel({
  article,
  mode,
  target = 'launcher',
  accentColor = DEFAULT_ACCENT,
  launcherThemeStyle,
  armoryHostStyle,
}: {
  article: NewsPreviewArticle
  mode: NewsPreviewMode
  target?: NewsPreviewTarget
  accentColor?: string
  launcherThemeStyle?: CSSProperties
  armoryHostStyle?: CSSProperties
}) {
  if (target === 'armory') {
    return (
      <div className="armory-news-preview-host" style={armoryHostStyle}>
        {mode === 'card' ? (
          <ArmoryNewsCardPreview article={article} />
        ) : (
          <ArmoryNewsArticlePreview article={article} />
        )}
      </div>
    )
  }

  if (mode === 'card') {
    return (
      <div className="launcher-news-card-preview-host">
        <LauncherNewsCardPreview
          article={article}
          accentColor={accentColor}
          launcherThemeStyle={launcherThemeStyle}
        />
      </div>
    )
  }

  return (
    <LauncherNewsArticlePreview
      article={article}
      accentColor={accentColor}
      launcherThemeStyle={launcherThemeStyle}
    />
  )
}

export function NewsLivePreviewSidebar({
  article,
  accentColor = DEFAULT_ACCENT,
  launcherPreviewTheme,
  target,
  mode,
  onTargetChange,
  onModeChange,
  armoryHostStyle,
}: {
  article: NewsPreviewArticle
  accentColor?: string
  launcherPreviewTheme?: LauncherNewsPreviewTheme
  target: NewsPreviewTarget
  mode: NewsPreviewMode
  onTargetChange: (target: NewsPreviewTarget) => void
  onModeChange: (mode: NewsPreviewMode) => void
  armoryHostStyle?: CSSProperties
}) {
  const launcherThemeStyle = launcherPreviewTheme?.cssVars
  const themeLabel =
    target === 'launcher' && launcherPreviewTheme
      ? `Launcher · ${launcherPreviewTheme.templateName}`
      : 'Updates as you type · switch launcher or armory'

  return (
    <aside className="min-w-0 xl:sticky xl:top-4 xl:self-start">
      <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm">
        <div className="border-b border-slate-100 bg-slate-50 px-4 py-3">
          <div className="flex items-center gap-2">
            <Monitor className="h-4 w-4 text-blue-600" />
            <div>
              <h4 className="text-sm font-semibold text-slate-900">Live preview</h4>
              <p className="mt-0.5 text-xs text-slate-500">{themeLabel}</p>
            </div>
          </div>
        </div>

        <div className="border-b border-slate-100 px-4 py-3">
          <NewsPreviewModeTabs
            target={target}
            onTargetChange={onTargetChange}
            mode={mode}
            onModeChange={onModeChange}
            variant="light"
          />
        </div>

        <div className="max-h-[min(72vh,760px)] overflow-y-auto p-4">
          <NewsPreviewPanel
            article={article}
            target={target}
            mode={mode}
            accentColor={launcherPreviewTheme?.accentColor ?? accentColor}
            launcherThemeStyle={launcherThemeStyle}
            armoryHostStyle={armoryHostStyle}
          />
        </div>
      </div>
    </aside>
  )
}

/** @deprecated Use LauncherNewsArticlePreview */
export function LauncherNewsReadingPreview({
  article,
  accentColor,
}: {
  article: NewsPreviewArticle
  accentColor?: string
}) {
  return <LauncherNewsArticlePreview article={article} accentColor={accentColor} />
}
