import DOMPurify from 'dompurify'
import { NEWS_TAG_COLORS } from '@/components/launcher/NewsEditor'
import '@/components/launcher/newsContent.css'
import '@/components/launcher/newsPreview.css'

const DEFAULT_ACCENT = '#4fa8d8'

export interface NewsPreviewArticle {
  title: string
  date?: string | null
  tag?: string | null
  html: string
  coverUrl?: string | null
  accentColor?: string
}

function sanitizeHtml(html: string): string {
  return DOMPurify.sanitize(html, { USE_PROFILES: { html: true } })
}

/** Fixed-size card as shown in the launcher Play tab news strip (156×210). */
export function LauncherNewsCardPreview({ article }: { article: NewsPreviewArticle }) {
  const title = article.title?.trim() || 'Untitled'

  return (
    <div className="launcher-news-preview-strip">
      <div className="launcher-news-preview-strip__label">Latest News</div>
      <div className="launcher-news-card" aria-label={`News card: ${title}`}>
        <div className="launcher-news-card__cover">
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
        </div>
      </div>
      <p className="mt-3 text-xs text-gray-500">
        Launcher card — cover, title, and date only (156×210 px).
      </p>
    </div>
  )
}

/** Full reading view as shown when a launcher news card is opened. */
export function LauncherNewsReadingPreview({ article }: { article: NewsPreviewArticle }) {
  const accent = article.accentColor || DEFAULT_ACCENT
  const tag = article.tag?.trim().toLowerCase() ?? ''
  const tagColor = tag ? NEWS_TAG_COLORS[tag] : undefined
  const sanitizedHtml = sanitizeHtml(article.html)
  const title = article.title?.trim() || 'Untitled'

  return (
    <div className="launcher-news-reading" style={{ ['--news-accent' as string]: accent }}>
      {article.coverUrl && (
        <img src={article.coverUrl} alt="" className="launcher-news-reading__cover" />
      )}
      <div className="launcher-news-reading__wrap">
        {tag && tagColor && (
          <span className="launcher-news-reading__tag" style={{ backgroundColor: tagColor }}>
            {tag}
          </span>
        )}
        <h1 className="launcher-news-reading__title">{title}</h1>
        {article.date && <div className="launcher-news-reading__date">{article.date}</div>}
        <div className="news-content" dangerouslySetInnerHTML={{ __html: sanitizedHtml }} />
      </div>
      <p className="px-6 pb-5 text-xs text-gray-500">
        Launcher reading view — full article body with cover hero.
      </p>
    </div>
  )
}
