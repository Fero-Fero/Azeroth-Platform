import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { CheckCircle2, LayoutGrid, Loader2, RotateCcw, Save } from 'lucide-react'
import {
  armoryAssetsInfoKey,
  useArmoryLayout,
  useArmoryStyling,
  useArmoryStylingDefaults,
  useArmoryStylingPreview,
  useSaveArmoryLayout,
} from '@/hooks/useArmoryAssets'
import { armoryAssetsApi } from '@/services/api'
import { useArmoryJobContext } from '@/contexts/ArmoryJobContext'
import { apiErrorMessage } from '@/lib/utils'
import { CLASSIC_STYLING_FALLBACK } from '@/lib/armory-styling'
import {
  ARMORY_PAGE_GROUPS,
  buildDefaultSiteLayout,
  buildPageTemplate,
  cloneLayout,
  compactLayout,
  compactPageLayout,
  createWidget,
  getPageLayout,
  getPageTemplates,
  layoutsEqual,
  migrateLayoutToV2,
  PAGE_WIDGET_TYPES,
  resolveEditorTemplateId,
  setPageLayout,
  WIDGET_CATALOG,
} from '@/lib/armory-layout'
import type { ArmoryLayoutDto, ArmoryLayoutWidgetDto, ArmoryPageId, ArmoryWidgetType } from '@/types/armory.types'
import { useQueryClient } from '@tanstack/react-query'
import ArmoryLayoutCanvas from './layout/ArmoryLayoutCanvas'
import NavbarEditor from './layout/NavbarEditor'
import WidgetChromeEditor from './layout/WidgetChromeEditor'

const selectClass =
  'rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-sm text-gray-900 shadow-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500'

export default function ArmoryLayoutTab({
  stackId,
  siteName,
  moduleIds,
  realmCount = 1,
}: {
  stackId: string
  siteName?: string
  moduleIds?: string[]
  realmCount?: number
}) {
  const qc = useQueryClient()
  const { data: layout, isLoading, error } = useArmoryLayout(stackId)
  const { data: styling } = useArmoryStyling(stackId)
  const { data: stylingDefaults } = useArmoryStylingDefaults(stackId)
  const { data: stylingPreviewDraft } = useArmoryStylingPreview(stackId)
  const saveLayout = useSaveArmoryLayout(stackId)
  const { job } = useArmoryJobContext()

  const [draft, setDraft] = useState<ArmoryLayoutDto>(buildDefaultSiteLayout())
  const [activePageId, setActivePageId] = useState<ArmoryPageId>('home')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [view, setView] = useState<'layout' | 'navbar'>('layout')
  const [message, setMessage] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const prevRunningRef = useRef(false)

  useEffect(() => {
    if (layout) setDraft(cloneLayout(layout))
  }, [layout])

  useEffect(() => {
    const running = job?.isRunning ?? false
    if (prevRunningRef.current && !running && job?.action === 'Rebuild') {
      qc.invalidateQueries({ queryKey: armoryAssetsInfoKey(stackId) })
      if (job.success) flash(job.message || 'Armory image rebuilt.')
      else if (job.error) setPageError(job.error)
    }
    prevRunningRef.current = running
  }, [job?.isRunning, job?.action, job?.success, job?.error, job?.message, qc, stackId])

  const dirty = useMemo(() => (layout ? !layoutsEqual(draft, migrateLayoutToV2(layout)) : false), [draft, layout])
  const activePage = useMemo(() => getPageLayout(draft, activePageId), [draft, activePageId])
  const selectedWidget = activePage.widgets.find((w) => w.id === selectedId) ?? null
  const previewStyling = stylingPreviewDraft ?? styling ?? CLASSIC_STYLING_FALLBACK
  const topLogsEnabled = moduleIds?.includes('mod-raid-logs-tracker') ?? true
  const pageTemplates = getPageTemplates(activePageId)
  const selectedTemplateId = resolveEditorTemplateId(activePage, activePageId)
  const multiRealm = realmCount > 1
  const pageCustomized = activePage.mode === 'Custom'

  const flash = (text: string) => {
    setMessage(text)
    setTimeout(() => setMessage(null), 5000)
  }

  const selectPageTemplate = async (templateId: string) => {
    try {
      const { data: page } = await armoryAssetsApi.getPageTemplate(stackId, activePageId, templateId)
      setDraft(setPageLayout(draft, activePageId, page))
      setSelectedId(null)
    } catch {
      setDraft(setPageLayout(draft, activePageId, compactPageLayout(buildPageTemplate(activePageId, templateId))))
      setSelectedId(null)
    }
  }

  const onSave = async () => {
    setPageError(null)
    try {
      const normalized = compactLayout(draft)
      await saveLayout.mutateAsync(normalized)
      setDraft(normalized)
      flash('Layout saved. Changes are live on the running armory when it is started; rebuild when prompted for other static asset updates.')
    } catch (err) {
      setPageError(apiErrorMessage(err))
    }
  }

  const addWidget = (type: ArmoryWidgetType) => {
    const page = getPageLayout(draft, activePageId)
    const maxY = page.widgets.reduce((max, w) => Math.max(max, w.y + w.h), 0)
    const widget = createWidget(type, 0, maxY)
    setDraft(
      setPageLayout(draft, activePageId, {
        ...page,
        mode: 'Custom',
        templateId: 'Custom',
        widgets: [...page.widgets, widget],
      }),
    )
    setSelectedId(widget.id)
  }

  const updateWidget = (widget: ArmoryLayoutWidgetDto) => {
    const page = getPageLayout(draft, activePageId)
    setDraft(
      setPageLayout(draft, activePageId, {
        ...page,
        mode: 'Custom',
        templateId: 'Custom',
        widgets: page.widgets.map((w) => (w.id === widget.id ? widget : w)),
      }),
    )
  }

  const removeSelected = () => {
    if (!selectedId) return
    const page = getPageLayout(draft, activePageId)
    setDraft(
      compactLayout(
        setPageLayout(draft, activePageId, {
          ...page,
          mode: 'Custom',
          templateId: 'Custom',
          widgets: page.widgets.filter((w) => w.id !== selectedId),
        }),
      ),
    )
    setSelectedId(null)
  }

  const resetPageToTemplate = () => {
    const templateId = activePage.templateId === 'Custom' ? 'Default' : activePage.templateId
    selectPageTemplate(templateId)
  }

  const switchPage = (pageId: ArmoryPageId) => {
    setActivePageId(pageId)
    setSelectedId(null)
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16 text-gray-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-md bg-red-50 p-4 text-red-700">{apiErrorMessage(error)}</div>
  }

  return (
    <div className="space-y-4">
      {pageError && <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{pageError}</div>}
      {message && (
        <div className="inline-flex items-center gap-1 rounded-md bg-green-50 px-3 py-2 text-sm text-green-700">
          <CheckCircle2 className="h-4 w-4" /> {message}
        </div>
      )}

      <section className="rounded-lg border bg-white shadow-sm">
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 px-4 py-3">
          <div className="flex items-center gap-2">
            <LayoutGrid className="h-5 w-5 text-blue-600" />
            <h2 className="text-base font-semibold text-gray-900">Armory Layout</h2>
          </div>
          <div className="flex items-center gap-2">
            {dirty && <span className="text-xs text-amber-600">Unsaved</span>}
            <button
              type="button"
              onClick={onSave}
              disabled={saveLayout.isPending || !dirty}
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
            >
              {saveLayout.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              Save
            </button>
          </div>
        </div>

        <div className="flex gap-1 border-b border-gray-100 px-4">
          <TabButton active={view === 'layout'} onClick={() => setView('layout')}>
            Pages
          </TabButton>
          <TabButton active={view === 'navbar'} onClick={() => setView('navbar')}>
            Navbar
          </TabButton>
        </div>

        <div className="p-4">
          {view === 'navbar' ? (
            <NavbarEditor
              navbar={draft.navbar}
              siteName={siteName}
              topLogsEnabled={topLogsEnabled}
              onChange={(navbar) => setDraft({ ...draft, navbar })}
            />
          ) : (
            <div className="space-y-4">
              <div className="flex flex-wrap items-end gap-3">
                <label className="flex min-w-[140px] flex-1 flex-col gap-1 text-xs font-medium text-gray-600">
                  Page
                  <select
                    className={selectClass}
                    value={activePageId}
                    onChange={(e) => switchPage(e.target.value as ArmoryPageId)}
                  >
                    {ARMORY_PAGE_GROUPS.map((group) => (
                      <optgroup key={group.label} label={group.label}>
                        {group.pages.map((page) => (
                          <option key={page.id} value={page.id}>
                            {page.label}
                          </option>
                        ))}
                      </optgroup>
                    ))}
                  </select>
                </label>

                <label className="flex min-w-[180px] flex-[2] flex-col gap-1 text-xs font-medium text-gray-600">
                  <span className="flex items-center gap-2">
                    Template
                    {pageCustomized && (
                      <span className="rounded bg-blue-50 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-blue-600">
                        Customized
                      </span>
                    )}
                  </span>
                  <select
                    className={selectClass}
                    value={selectedTemplateId}
                    onChange={(e) => selectPageTemplate(e.target.value)}
                  >
                    {pageTemplates.map((template) => (
                      <option key={template.id} value={template.id}>
                        {template.label}
                        {template.inspiredBy ? ` · ${template.inspiredBy}` : ''}
                      </option>
                    ))}
                  </select>
                </label>

                <label className="flex min-w-[160px] flex-1 flex-col gap-1 text-xs font-medium text-gray-600">
                  Add widget
                  <select
                    className={selectClass}
                    defaultValue=""
                    onChange={(e) => {
                      const type = e.target.value as ArmoryWidgetType
                      if (type) {
                        addWidget(type)
                        e.target.value = ''
                      }
                    }}
                  >
                    <option value="">Choose type…</option>
                    {PAGE_WIDGET_TYPES[activePageId].map((type) => (
                      <option key={type} value={type}>
                        {WIDGET_CATALOG[type].label}
                      </option>
                    ))}
                  </select>
                </label>
              </div>

              <ArmoryLayoutCanvas
                siteLayout={draft}
                pageId={activePageId}
                styling={previewStyling}
                stylingDefaults={stylingDefaults}
                stackId={stackId}
                siteName={siteName}
                multiRealm={multiRealm}
                topLogsEnabled={topLogsEnabled}
                worldMapEnabled={moduleIds?.includes('mod-world-map') ?? true}
                selectedId={selectedId}
                onSelect={setSelectedId}
                onSiteLayoutChange={setDraft}
              />

              <div className="flex flex-wrap items-center gap-2 border-t border-gray-100 pt-4">
                <button
                  type="button"
                  onClick={removeSelected}
                  disabled={!selectedId}
                  className="rounded-md border border-red-200 px-2.5 py-1.5 text-sm text-red-700 hover:bg-red-50 disabled:opacity-40"
                >
                  Remove selected
                </button>
                <button
                  type="button"
                  onClick={resetPageToTemplate}
                  className="inline-flex items-center gap-1 rounded-md border border-gray-200 px-2.5 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
                >
                  <RotateCcw className="h-3.5 w-3.5" /> Reset template
                </button>
              </div>

              <WidgetChromeEditor
                widget={selectedWidget}
                onChange={updateWidget}
                onResetChrome={() => selectedWidget && updateWidget({ ...selectedWidget, chrome: null })}
              />
            </div>
          )}
        </div>
      </section>
    </div>
  )
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
        active ? 'border-blue-600 text-blue-600' : 'border-transparent text-gray-500 hover:text-gray-700'
      }`}
    >
      {children}
    </button>
  )
}
