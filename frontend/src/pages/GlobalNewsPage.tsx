import { useMemo } from 'react'
import { Loader2, Megaphone } from 'lucide-react'
import {
  useLauncherConfig,
  useLauncherTemplates,
  useGlobalNews,
  useSaveGlobalNews,
  useUploadGlobalNewsImage,
} from '@/hooks/useLauncher'
import NewsEditor from '@/components/launcher/NewsEditor'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

/**
 * Global news / patch notes editor. Articles written here are announcements for every server: on
 * save they are broadcast to each launcher-visible stack's own news store, so an announcement is
 * written once instead of duplicated per stack. Each stack owns its own copy afterwards.
 */
export default function GlobalNewsPage() {
  const { data: config } = useLauncherConfig()
  const { data: templates } = useLauncherTemplates()
  const { data: news, error } = useGlobalNews()
  const saveNews = useSaveGlobalNews()
  const uploadNewsImage = useUploadGlobalNewsImage()

  const newsAccent = useMemo(
    () => templates?.find((t) => t.id === config?.template)?.accentColor ?? '#4fa8d8',
    [templates, config?.template],
  )

  const infoBanner = (
    <div className="flex gap-3 rounded-lg border border-blue-200 bg-blue-50/80 p-4 text-sm text-blue-900 shadow-sm">
      <Megaphone className="mt-0.5 h-5 w-5 shrink-0 text-blue-600" />
      <div className="space-y-1">
        <p className="font-medium">Articles here are uploaded to every stack.</p>
        <p className="text-blue-800/90">
          When you save, each published article is copied into every launcher-visible stack's own
          news and automatically placed as that stack's latest article. Ordering and per-stack
          details are handled on upload.
        </p>
      </div>
    </div>
  )

  if (error) {
    return (
      <div className="mx-auto max-w-6xl space-y-5">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Global News</h1>
          <p className="mt-1 text-gray-600">
            Write an announcement once and it goes out to every server.
          </p>
        </div>
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {errorMessage(error)}
        </div>
      </div>
    )
  }

  if (news === undefined) {
    return (
      <div className="mx-auto max-w-6xl space-y-5">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Global News</h1>
          <p className="mt-1 text-gray-600">
            Write an announcement once and it goes out to every server.
          </p>
        </div>
        <div className="flex items-center justify-center gap-2 rounded-lg border border-gray-200 bg-white py-16 text-sm text-gray-500 shadow-sm">
          <Loader2 className="h-5 w-5 animate-spin text-blue-600" /> Loading news…
        </div>
      </div>
    )
  }

  return (
    <div className="mx-auto max-w-6xl">
      <NewsEditor
        items={news}
        onSave={(items) => saveNews.mutateAsync(items)}
        onUploadImage={(itemId, file) => uploadNewsImage.mutateAsync({ itemId, file })}
        imageUrlFor={(id) => `/api/launcher/news-image/${id}`}
        isSaving={saveNews.isPending}
        accentColor={newsAccent}
        pageTitle="Global News"
        pageDescription="Write an announcement once and broadcast it to every launcher-visible stack."
        infoBanner={infoBanner}
      />
    </div>
  )
}
