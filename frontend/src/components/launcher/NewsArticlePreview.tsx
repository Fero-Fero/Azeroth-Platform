import DOMPurify from 'dompurify'
import { LayoutGrid, Newspaper } from 'lucide-react'
import { NEWS_TAG_COLORS, NEWS_TAGS } from '@/components/launcher/NewsEditor'
import '@/components/launcher/newsContent.css'
import '@/components/launcher/newsPreview.css'

const DEFAULT_ACCENT = '#4fa8d8'

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

/** Canonical launcher reading preview — matches the Global News editor preview panel. */
export function LauncherNewsArticlePreview({
  article,
  accentColor = DEFAULT_ACCENT,
  className,
}: {
  article: NewsPreviewArticle
  accentColor?: string
  className?: string
}) {
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const tagColor = tag ? NEWS_TAG_COLORS[tag] : undefined
  const sanitizedHtml = sanitizeHtml(article.html)
  const title = article.title?.trim() || 'Untitled'

  return (
    <div
      className={`overflow-hidden rounded-lg border border-gray-800 bg-gray-900 shadow-inner ${className ?? ''}`}
      style={{ ['--news-accent' as string]: accentColor }}
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
        <div className="text-xl font-bold text-white">{title}</div>
        {article.date && <div className="mb-3 text-xs text-gray-400">{article.date}</div>}
        <div className="news-content" dangerouslySetInnerHTML={{ __html: sanitizedHtml }} />
      </div>
    </div>
  )
}

/** Fixed-size card as shown in the launcher Play tab news strip (156×210). */
export function LauncherNewsCardPreview({ article }: { article: NewsPreviewArticle }) {
  const title = article.title?.trim() || 'Untitled'
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const tagColor = tag ? NEWS_TAG_COLORS[tag] : undefined
  const excerpt = extractNewsExcerpt(article.html)

  return (
    <div className="launcher-news-preview-strip">
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
            <div className="flex h-full items-center justify-center text-[11px] text-[#b3a384]/70">
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

export function NewsPreviewModeTabs({
  mode,
  onModeChange,
  variant = 'light',
}: {
  mode: NewsPreviewMode
  onModeChange: (mode: NewsPreviewMode) => void
  variant?: 'light' | 'dark'
}) {
  const isDark = variant === 'dark'
  const activeClass = isDark
    ? 'border-blue-500 text-blue-300'
    : 'border-blue-600 text-blue-700'
  const inactiveClass = isDark
    ? 'border-transparent text-gray-400 hover:text-gray-200'
    : 'border-transparent text-gray-500 hover:text-gray-800'
  const containerClass = isDark
    ? 'border-gray-800 bg-gray-900/50'
    : 'border-gray-100 bg-gray-50/80'

  return (
    <div className={`flex gap-1 border-b ${containerClass}`}>
      <button
        type="button"
        onClick={() => onModeChange('article')}
        className={`inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors -mb-px ${
          mode === 'article' ? activeClass : inactiveClass
        }`}
      >
        <Newspaper className="h-4 w-4" />
        Full article
      </button>
      <button
        type="button"
        onClick={() => onModeChange('card')}
        className={`inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors -mb-px ${
          mode === 'card' ? activeClass : inactiveClass
        }`}
      >
        <LayoutGrid className="h-4 w-4" />
        Card
      </button>
    </div>
  )
}

export function NewsPreviewPanel({
  article,
  mode,
  accentColor = DEFAULT_ACCENT,
}: {
  article: NewsPreviewArticle
  mode: NewsPreviewMode
  accentColor?: string
}) {
  if (mode === 'card') {
    return (
      <div className="launcher-news-card-preview-host">
        <LauncherNewsCardPreview article={article} />
      </div>
    )
  }

  return <LauncherNewsArticlePreview article={article} accentColor={accentColor} />
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
