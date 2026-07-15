import Markdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import 'github-markdown-css/github-markdown-light.css'

interface MarkdownViewProps {
  content: string
  // Base URL used to resolve relative links/images (e.g. a repo's raw base).
  baseUrl?: string | null
}

/** Renders Markdown with GitHub-flavoured styling (tables, task lists, etc.). */
export default function MarkdownView({ content, baseUrl }: MarkdownViewProps) {
  const urlTransform = (url: string): string => {
    // Block anything that isn't a safe scheme or a relative/anchor URL.
    if (/^\s*javascript:/i.test(url)) return ''

    const isAbsolute = /^[a-z][a-z0-9+.-]*:/i.test(url) || url.startsWith('//')
    const isAnchor = url.startsWith('#')
    if (isAbsolute || isAnchor || !baseUrl) return url

    try {
      return new URL(url, baseUrl).toString()
    } catch {
      return url
    }
  }

  return (
    <div className="markdown-body" style={{ background: 'transparent' }}>
      <Markdown remarkPlugins={[remarkGfm]} urlTransform={urlTransform}>
        {content}
      </Markdown>
    </div>
  )
}
