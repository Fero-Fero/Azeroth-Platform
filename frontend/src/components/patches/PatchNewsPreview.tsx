import { useEffect, useState } from 'react'
import { LayoutGrid, Loader2, Newspaper, X } from 'lucide-react'
import { usePatchNewsPreview } from '@/hooks/usePatches'
import { usePatchNewsPreviewMedia } from '@/hooks/usePatchNewsPreviewMedia'
import {
  LauncherNewsCardPreview,
  LauncherNewsReadingPreview,
} from '@/components/launcher/NewsArticlePreview'

interface PatchNewsPreviewProps {
  stackId: string
  patchKey: string | null
  open: boolean
  onClose: () => void
}

type PreviewMode = 'card' | 'article'

export default function PatchNewsPreview({
  stackId,
  patchKey,
  open,
  onClose,
}: PatchNewsPreviewProps) {
  const [mode, setMode] = useState<PreviewMode>('article')
  const { data: preview, isLoading, isError, error } = usePatchNewsPreview(stackId, patchKey, open)
  const { coverUrl, html, loadingMedia } = usePatchNewsPreviewMedia(preview, open)

  useEffect(() => {
    if (open) {
      setMode('article')
    }
  }, [open, patchKey])

  if (!open) {
    return null
  }

  const article =
    preview?.available && html
      ? {
          title: preview.title ?? 'Untitled',
          date: preview.date,
          tag: preview.tag,
          html,
          coverUrl,
        }
      : null

  const showContentLoading = isLoading || (preview?.available && loadingMedia)

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="patch-news-preview-title"
      onClick={onClose}
    >
      <div
        className={`flex max-h-[90vh] w-full flex-col overflow-hidden rounded-xl border border-gray-700 bg-gray-950 shadow-2xl ${
          mode === 'card' ? 'max-w-md' : 'max-w-3xl'
        }`}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3 border-b border-gray-800 px-5 py-4">
          <div className="min-w-0">
            {isLoading ? (
              <div className="flex items-center gap-2 text-sm text-gray-400">
                <Loader2 className="h-4 w-4 animate-spin" />
                Loading article…
              </div>
            ) : preview?.available ? (
              <>
                <h2 id="patch-news-preview-title" className="text-lg font-semibold text-white">
                  {preview.title}
                </h2>
                {preview.date && (
                  <p className="mt-1 text-xs text-gray-400">{preview.date}</p>
                )}
              </>
            ) : (
              <h2 id="patch-news-preview-title" className="text-lg font-semibold text-white">
                Patch news preview
              </h2>
            )}
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1 text-gray-400 hover:bg-gray-800 hover:text-white"
            aria-label="Close preview"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        {preview?.available && (
          <div className="flex gap-1 border-b border-gray-800 bg-gray-900/50 px-5">
            <button
              type="button"
              onClick={() => setMode('article')}
              className={`inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors -mb-px ${
                mode === 'article'
                  ? 'border-blue-500 text-blue-300'
                  : 'border-transparent text-gray-400 hover:text-gray-200'
              }`}
            >
              <Newspaper className="h-4 w-4" />
              Full article
            </button>
            <button
              type="button"
              onClick={() => setMode('card')}
              className={`inline-flex items-center gap-1.5 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors -mb-px ${
                mode === 'card'
                  ? 'border-blue-500 text-blue-300'
                  : 'border-transparent text-gray-400 hover:text-gray-200'
              }`}
            >
              <LayoutGrid className="h-4 w-4" />
              Card
            </button>
          </div>
        )}

        <div className="overflow-y-auto px-5 py-4">
          {showContentLoading && (
            <div className="flex justify-center py-12">
              <Loader2 className="h-8 w-8 animate-spin text-gray-500" />
            </div>
          )}

          {isError && (
            <p className="rounded-md border border-red-800 bg-red-950/40 px-3 py-2 text-sm text-red-300">
              {error instanceof Error ? error.message : 'Failed to load patch news preview.'}
            </p>
          )}

          {!isLoading && !isError && preview && !preview.available && (
            <p className="text-sm text-gray-400">
              {preview.error ?? 'This patch has no news/article.json file.'}
            </p>
          )}

          {!showContentLoading && article && mode === 'card' && (
            <div className="launcher-news-card-preview-host">
              <LauncherNewsCardPreview article={article} />
            </div>
          )}

          {!showContentLoading && article && mode === 'article' && (
            <LauncherNewsReadingPreview article={article} />
          )}

          {!showContentLoading && preview?.available && (
            <p className="mt-6 text-xs text-gray-500">
              Published to the launcher and armory news feed when this patch is applied.
            </p>
          )}
        </div>
      </div>
    </div>
  )
}
