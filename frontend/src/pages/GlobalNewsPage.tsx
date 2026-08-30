import { useMemo } from 'react'
import { Loader2 } from 'lucide-react'
import {
  useLauncherConfig,
  useLauncherTemplates,
  useGlobalNews,
  useSaveGlobalNews,
  useUploadGlobalNewsImage,
} from '@/hooks/useLauncher'
import NewsEditor from '@/components/launcher/NewsEditor'
import { StackTabInfoDetails } from '@/components/stacks/StackTabChrome'
import { armoryPreviewCssVars, CLASSIC_STYLING_FALLBACK } from '@/lib/armory-styling'
import { resolveLauncherNewsPreviewTheme } from '@/lib/launcher-theme'
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

  const launcherPreviewTheme = useMemo(
    () => resolveLauncherNewsPreviewTheme(templates, config?.template, null),
    [templates, config?.template],
  )

  const armoryPreviewStyle = useMemo(
    () => ({
      ...armoryPreviewCssVars(CLASSIC_STYLING_FALLBACK),
      backgroundColor: 'var(--armory-bg)',
    }),
    [],
  )

  const infoBanner = (
    <StackTabInfoDetails summary="Articles here are uploaded to every stack">
      <p>
        When you save, each published article is copied into every launcher-visible stack&apos;s own
        news and automatically placed as that stack&apos;s latest article. Ordering and per-stack
        details are handled on upload.
      </p>
    </StackTabInfoDetails>
  )

  if (error) {
    return (
      <div className="relative left-1/2 w-[90vw] -translate-x-1/2 space-y-5">
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
      <div className="relative left-1/2 w-[90vw] -translate-x-1/2 space-y-5">
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
    <NewsEditor
        items={news}
        onSave={(items) => saveNews.mutateAsync(items)}
        onUploadImage={(itemId, file) => uploadNewsImage.mutateAsync({ itemId, file })}
        imageUrlFor={(id) => `/api/launcher/news-image/${id}`}
        isSaving={saveNews.isPending}
        launcherPreviewTheme={launcherPreviewTheme}
        armoryPreviewStyle={armoryPreviewStyle}
        pageTitle="Global News"
        pageDescription="Write an announcement once and broadcast it to every launcher-visible stack."
        infoBanner={infoBanner}
      />
  )
}
