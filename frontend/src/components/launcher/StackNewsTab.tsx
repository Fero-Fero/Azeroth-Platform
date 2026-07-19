import { useMemo } from 'react'
import { Loader2 } from 'lucide-react'
import {
  useLauncherConfig,
  useLauncherProfile,
  useLauncherTemplates,
  useSaveStackNews,
  useStackNews,
  useUploadStackNewsImage,
} from '@/hooks/useLauncher'
import {
  useArmoryStyling,
  useArmoryStylingDefaults,
  useArmoryStylingPreview,
} from '@/hooks/useArmoryAssets'
import {
  armoryPreviewCssVars,
  armoryPreviewWallpaperStyle,
  CLASSIC_STYLING_FALLBACK,
} from '@/lib/armory-styling'
import { resolveLauncherNewsPreviewTheme } from '@/lib/launcher-theme'
import NewsEditor from './NewsEditor'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

export default function StackNewsTab({ stackId }: { stackId: string }) {
  const { data: templates } = useLauncherTemplates()
  const { data: globalConfig } = useLauncherConfig()
  const { data: launcherProfile } = useLauncherProfile(stackId)
  const { data: news, error } = useStackNews(stackId)
  const saveNews = useSaveStackNews(stackId)
  const uploadNewsImage = useUploadStackNewsImage(stackId)
  const { data: armoryStyling } = useArmoryStyling(stackId)
  const { data: stylingDefaults } = useArmoryStylingDefaults(stackId)
  const { data: stylingPreview } = useArmoryStylingPreview(stackId)

  const launcherPreviewTheme = useMemo(
    () => resolveLauncherNewsPreviewTheme(templates, globalConfig?.template, launcherProfile?.template),
    [templates, globalConfig?.template, launcherProfile?.template],
  )

  const armoryPreviewStyle = useMemo(() => {
    const styling = stylingPreview ?? armoryStyling ?? CLASSIC_STYLING_FALLBACK
    return {
      ...armoryPreviewCssVars(styling, stylingDefaults),
      ...armoryPreviewWallpaperStyle(styling, stylingDefaults, stackId),
      backgroundColor: 'var(--armory-bg)',
    }
  }, [armoryStyling, stackId, stylingDefaults, stylingPreview])

  if (error) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        {errorMessage(error)}
      </div>
    )
  }

  if (news === undefined) {
    return (
      <div className="flex items-center justify-center gap-2 rounded-xl border border-slate-200 bg-white py-16 text-sm text-slate-500 shadow-sm">
        <Loader2 className="h-5 w-5 animate-spin text-blue-600" /> Loading news…
      </div>
    )
  }

  return (
    <NewsEditor
      items={news}
      onSave={(items) => saveNews.mutateAsync(items)}
      onUploadImage={(itemId, file) => uploadNewsImage.mutateAsync({ itemId, file })}
      imageUrlFor={(id) => `/api/stacks/${stackId}/launcher/news-image/${id}`}
      isSaving={saveNews.isPending}
      launcherPreviewTheme={launcherPreviewTheme}
      armoryPreviewStyle={armoryPreviewStyle}
      pageTitle="News / Patch Notes"
      pageDescription="Articles shown in the launcher when this stack's profile is selected. Leave empty to fall back to global news."
    />
  )
}
