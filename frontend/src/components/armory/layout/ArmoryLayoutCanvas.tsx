import { useEffect, useMemo, useRef, useState } from 'react'
import GridLayout, { type Layout } from 'react-grid-layout/legacy'
import { GripVertical } from 'lucide-react'
import 'react-grid-layout/css/styles.css'
import 'react-resizable/css/styles.css'
import type { ArmoryLayoutDto, ArmoryPageId, ArmoryPageLayoutDto, ArmoryStylingDto } from '@/types/armory.types'
import {
  compactPageLayout,
  getPageLayout,
  gridContentHeight,
  normalizeNavbar,
  setPageLayout,
  applyWidgetMinimums,
  WIDGET_CATALOG,
} from '@/lib/armory-layout'
import { armoryPreviewCssVars, armoryPreviewWallpaperStyle } from '@/lib/armory-styling'
import DraggableNavbarStrip from './DraggableNavbarStrip'
import WidgetPreviewContent from './WidgetPreviewContent'

interface ArmoryLayoutCanvasProps {
  siteLayout: ArmoryLayoutDto
  pageId: ArmoryPageId
  styling: ArmoryStylingDto
  stylingDefaults?: Record<string, ArmoryStylingDto>
  stackId?: string
  siteName?: string
  multiRealm?: boolean
  topLogsEnabled?: boolean
  worldMapEnabled?: boolean
  selectedId: string | null
  onSelect: (id: string | null) => void
  onSiteLayoutChange: (layout: ArmoryLayoutDto) => void
}

export default function ArmoryLayoutCanvas({
  siteLayout,
  pageId,
  styling,
  stylingDefaults,
  stackId,
  siteName = 'Azeroth',
  multiRealm = false,
  topLogsEnabled = true,
  worldMapEnabled = true,
  selectedId,
  onSelect,
  onSiteLayoutChange,
}: ArmoryLayoutCanvasProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const measureRef = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(0)

  const pageLayout = useMemo(() => compactPageLayout(getPageLayout(siteLayout, pageId)), [siteLayout, pageId])
  const navbarConfig = useMemo(() => normalizeNavbar(siteLayout.navbar), [siteLayout.navbar])
  const previewVars = armoryPreviewCssVars(styling, stylingDefaults)
  const wallpaperStyle = armoryPreviewWallpaperStyle(styling, stylingDefaults, stackId)

  const navbarLinkMuted = (link: { kind: string; visible?: boolean }) =>
    (link.kind === 'TopLogs' && !topLogsEnabled) || (link.kind === 'Map' && !worldMapEnabled)

  const handleNavbarReorder = (links: typeof navbarConfig.links) => {
    onSiteLayoutChange({
      ...siteLayout,
      navbar: { ...navbarConfig, links },
    })
  }

  useEffect(() => {
    const element = measureRef.current
    if (!element) return
    const updateWidth = () => {
      const nextWidth = Math.floor(element.clientWidth)
      if (nextWidth > 0) setWidth(nextWidth)
    }
    updateWidth()
    const observer = new ResizeObserver(updateWidth)
    observer.observe(element)
    if (containerRef.current) observer.observe(containerRef.current)
    return () => observer.disconnect()
  }, [])

  const gridLayout = useMemo(
    () =>
      pageLayout.widgets
        .filter((w) => w.visible !== false)
        .map((w) => ({
          i: w.id,
          x: w.x,
          y: w.y,
          w: w.w,
          h: w.h,
          minW: WIDGET_CATALOG[w.type].minW,
          minH: WIDGET_CATALOG[w.type].minH,
        })),
    [pageLayout.widgets],
  )

  const gridHeight = useMemo(
    () => gridContentHeight(pageLayout.widgets, pageLayout.grid.rowHeight, pageLayout.grid.gap, pageLayout.grid.columns),
    [pageLayout],
  )

  const onLayoutChange = (next: Layout) => {
    const byId = new Map(next.map((item) => [item.i, item]))
    let changed = false
    const widgets = pageLayout.widgets.map((widget) => {
      const item = byId.get(widget.id)
      if (!item) return widget
      if (item.x !== widget.x || item.y !== widget.y || item.w !== widget.w || item.h !== widget.h) {
        changed = true
        return { ...widget, x: item.x, y: item.y, w: item.w, h: item.h }
      }
      return widget
    })
    if (!changed) return

    const updatedPage: ArmoryPageLayoutDto = {
      ...pageLayout,
      mode: 'Custom',
      templateId: 'Custom',
      widgets,
    }
    onSiteLayoutChange(
      setPageLayout(
        siteLayout,
        pageId,
        updatedPage.mode === 'Custom' ? applyWidgetMinimums(updatedPage) : compactPageLayout(updatedPage),
      ),
    )
  }

  return (
    <div
      className="overflow-hidden rounded-lg border"
      style={{
        ...previewVars,
        ...wallpaperStyle,
        borderColor: 'var(--armory-border)',
        background:
          wallpaperStyle?.backgroundImage ??
          'linear-gradient(color-mix(in srgb, var(--armory-background) 88%, transparent), color-mix(in srgb, var(--armory-background) 88%, transparent))',
      }}
    >
      <div
        style={{
          background:
            'linear-gradient(180deg, color-mix(in srgb, var(--armory-navbar) 90%, #000), var(--armory-navbar))',
          borderBottom: '2px solid var(--armory-border)',
        }}
      >
        <DraggableNavbarStrip
          links={navbarConfig.links}
          siteName={siteName}
          onReorder={handleNavbarReorder}
          isLinkMuted={navbarLinkMuted}
          variant="canvas"
          showSearch={navbarConfig.showSearch !== false}
          searchPlaceholder={navbarConfig.searchPlaceholder}
        />
      </div>

      <div className="p-3">
        <style>{`
          .armory-layout-canvas-grid {
            max-width: 100%;
          }
          .armory-layout-canvas-grid .react-grid-item {
            overflow: hidden;
          }
          .armory-layout-canvas-grid .armory-layout-widget {
            height: 100%;
            min-height: 0;
            display: flex;
            flex-direction: column;
            color: var(--armory-text);
            background: linear-gradient(
              180deg,
              color-mix(in srgb, var(--armory-panel) 96%, #fff),
              var(--armory-panel)
            );
            border-color: var(--armory-border);
          }
          .armory-layout-canvas-grid .armory-layout-widget__body {
            flex: 1 1 auto;
            min-height: 0;
            overflow: hidden;
          }
          .armory-layout-canvas-grid .armory-layout-widget--page-title {
            display: flex;
            flex-direction: row;
            align-items: stretch;
          }
          .armory-layout-canvas-grid .armory-layout-widget--page-title .armory-layout-widget__body {
            overflow: visible;
            flex: 1;
            min-width: 0;
            padding: 0.25rem 0.375rem 0.25rem 0;
          }
          .armory-layout-canvas-grid .armory-layout-widget--character-header {
            display: flex;
            flex-direction: row;
            align-items: stretch;
          }
          .armory-layout-canvas-grid .armory-layout-widget--character-subnav {
            display: flex;
            flex-direction: row;
            align-items: stretch;
          }
          .armory-layout-canvas-grid .armory-layout-widget--character-header .armory-layout-widget__body {
            overflow: visible;
            flex: 1;
            min-width: 0;
            padding: 0.25rem 0.375rem 0.25rem 0;
          }
          .armory-layout-canvas-grid .armory-layout-widget--character-subnav .armory-layout-widget__body {
            overflow: visible;
          }
          @media screen and (max-width: 950px) {
            .armory-layout-canvas-grid {
              display: flex !important;
              flex-direction: column;
            }
            .armory-layout-canvas-grid .react-grid-item {
              width: 100% !important;
              position: relative !important;
              transform: none !important;
            }
          }
        `}</style>
        <div
          ref={containerRef}
          className="w-full min-w-0 overflow-hidden rounded-md border p-2"
          style={{ borderColor: 'var(--armory-border)' }}
        >
          <div ref={measureRef} className="min-w-0 w-full">
            {width > 0 && (
              <GridLayout
                className="armory-layout-canvas-grid"
                layout={gridLayout}
                cols={pageLayout.grid.columns}
                rowHeight={pageLayout.grid.rowHeight}
                width={width}
                margin={[pageLayout.grid.gap, pageLayout.grid.gap]}
                containerPadding={[0, 0]}
                onLayoutChange={onLayoutChange}
                draggableHandle=".widget-drag-handle"
                compactType="vertical"
                style={{ minHeight: gridHeight }}
              >
                {pageLayout.widgets
                  .filter((w) => w.visible !== false)
                  .map((widget) => {
                    const selected = selectedId === widget.id
                    const shellClass = `armory-layout-widget box-border overflow-hidden rounded-md border shadow-sm ${
                      widget.type === 'PageTitle'
                        ? 'armory-layout-widget--page-title'
                        : widget.type === 'CharacterSubnav'
                          ? 'armory-layout-widget--character-subnav'
                          : ''
                    } ${selected ? 'ring-2 ring-inset ring-blue-500' : ''}`

                    if (widget.type === 'PageTitle' || widget.type === 'CharacterSubnav') {
                      return (
                        <div
                          key={widget.id}
                          className={shellClass}
                          onClick={() => onSelect(widget.id)}
                          role="button"
                          tabIndex={0}
                          onKeyDown={(e) => e.key === 'Enter' && onSelect(widget.id)}
                        >
                          <div
                            className="widget-drag-handle flex shrink-0 cursor-grab items-center px-1 active:cursor-grabbing"
                            style={{ color: 'var(--armory-muted)' }}
                            title="Drag to move"
                          >
                            <GripVertical className="h-3.5 w-3.5" />
                          </div>
                          <div className="armory-layout-widget__body">
                            <WidgetPreviewContent
                              widget={widget}
                              pageId={pageId}
                              siteName={siteName}
                              multiRealm={multiRealm}
                            />
                          </div>
                        </div>
                      )
                    }

                    return (
                      <div
                        key={widget.id}
                        className={shellClass}
                        onClick={() => onSelect(widget.id)}
                        role="button"
                        tabIndex={0}
                        onKeyDown={(e) => e.key === 'Enter' && onSelect(widget.id)}
                      >
                        <div
                          className="widget-drag-handle flex shrink-0 cursor-grab items-center justify-between border-b px-2 py-1 active:cursor-grabbing"
                          style={{
                            borderColor: 'var(--armory-border)',
                            background: 'color-mix(in srgb, var(--armory-navbar) 55%, transparent)',
                            color: 'var(--armory-muted)',
                          }}
                        >
                          <span className="text-[10px] font-semibold uppercase tracking-wide">
                            {WIDGET_CATALOG[widget.type].label}
                          </span>
                          <span className="text-[10px] opacity-70">
                            {widget.w}×{widget.h}
                          </span>
                        </div>
                        <div className="armory-layout-widget__body p-1.5">
                          <WidgetPreviewContent
                            widget={widget}
                            pageId={pageId}
                            siteName={siteName}
                            multiRealm={multiRealm}
                          />
                        </div>
                      </div>
                    )
                  })}
              </GridLayout>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
