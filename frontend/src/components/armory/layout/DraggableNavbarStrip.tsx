import { GripVertical } from 'lucide-react'
import { useRef, useState } from 'react'
import { navbarLinkDisplayLabel, reorderNavbarLinks } from '@/lib/armory-layout'
import type { ArmoryNavbarLinkDto } from '@/types/armory.types'

export interface DraggableNavbarStripProps {
  links: ArmoryNavbarLinkDto[]
  siteName?: string
  onReorder: (links: ArmoryNavbarLinkDto[]) => void
  onLinkClick?: (id: string) => void
  selectedLinkId?: string | null
  /** When false, link is shown dimmed (hidden in live preview). */
  isLinkMuted?: (link: ArmoryNavbarLinkDto) => boolean
  variant?: 'canvas' | 'editor'
  showSearch?: boolean
  searchPlaceholder?: string
}

export default function DraggableNavbarStrip({
  links,
  siteName = 'Azeroth',
  onReorder,
  onLinkClick,
  selectedLinkId,
  isLinkMuted,
  variant = 'editor',
  showSearch = false,
  searchPlaceholder = 'Search character...',
}: DraggableNavbarStripProps) {
  const [dragId, setDragId] = useState<string | null>(null)
  const [overId, setOverId] = useState<string | null>(null)
  const didMoveRef = useRef(false)

  const finishDrag = () => {
    setDragId(null)
    setOverId(null)
  }

  const handleDrop = (targetId: string) => {
    if (!dragId || dragId === targetId) {
      finishDrag()
      return
    }
    didMoveRef.current = true
    onReorder(reorderNavbarLinks(links, dragId, targetId))
    finishDrag()
  }

  const isCanvas = variant === 'canvas'

  return (
    <div
      className={`flex flex-wrap items-center gap-2 ${isCanvas ? 'px-4 py-2 text-sm font-bold' : 'text-sm font-semibold text-gray-800'}`}
      style={
        isCanvas
          ? {
              color: 'var(--armory-heading)',
            }
          : undefined
      }
    >
      {links.map((link, index) => {
        const label = navbarLinkDisplayLabel(link, siteName)
        const hidden = link.visible === false
        const muted = hidden || (isLinkMuted?.(link) ?? false)
        const isDragging = dragId === link.id
        const isDropTarget = overId === link.id && dragId !== link.id
        const isSelected = selectedLinkId === link.id

        return (
          <div
            key={link.id}
            draggable
            onDragStart={(e) => {
              didMoveRef.current = false
              setDragId(link.id)
              e.dataTransfer.effectAllowed = 'move'
              e.dataTransfer.setData('text/plain', link.id)
            }}
            onDragEnd={finishDrag}
            onDragOver={(e) => {
              e.preventDefault()
              e.dataTransfer.dropEffect = 'move'
              if (dragId && dragId !== link.id) {
                didMoveRef.current = true
                setOverId(link.id)
              }
            }}
            onDragLeave={() => {
              if (overId === link.id) {
                setOverId(null)
              }
            }}
            onDrop={(e) => {
              e.preventDefault()
              handleDrop(link.id)
            }}
            className={`group flex items-center gap-1 rounded-md border transition-colors ${
              isCanvas
                ? `border-transparent px-1 py-0.5 ${index === 0 ? '' : 'opacity-80'}`
                : 'border-gray-200 bg-white px-1 py-0.5 shadow-sm'
            } ${hidden || muted ? 'opacity-45' : ''} ${isDragging ? 'opacity-40' : ''} ${
              isDropTarget ? (isCanvas ? 'ring-2 ring-inset ring-white/40' : 'border-blue-400 bg-blue-50') : ''
            } ${isSelected ? (isCanvas ? 'ring-2 ring-inset ring-blue-400/80' : 'border-blue-500 ring-1 ring-blue-500') : ''} ${
              onLinkClick ? 'cursor-pointer' : ''
            }`}
            onClick={() => {
              if (didMoveRef.current) {
                didMoveRef.current = false
                return
              }
              onLinkClick?.(link.id)
            }}
            onKeyDown={(e) => {
              if (onLinkClick && (e.key === 'Enter' || e.key === ' ')) {
                e.preventDefault()
                onLinkClick(link.id)
              }
            }}
            role={onLinkClick ? 'button' : undefined}
            tabIndex={onLinkClick ? 0 : undefined}
            title={hidden ? 'Hidden in navbar' : muted ? 'Hidden when module is disabled' : 'Drag to reorder'}
          >
            <span
              className={`cursor-grab rounded p-0.5 active:cursor-grabbing ${
                isCanvas ? 'text-white/50 group-hover:text-white/80' : 'text-gray-400 group-hover:text-gray-600'
              }`}
              aria-hidden
            >
              <GripVertical className="h-3.5 w-3.5" />
            </span>
            <span className="truncate">{label}</span>
          </div>
        )
      })}

      {showSearch && (
        <span
          className={`ml-auto shrink-0 rounded border px-2 py-0.5 text-xs font-normal ${
            isCanvas ? 'opacity-70' : 'text-gray-500'
          }`}
          style={
            isCanvas
              ? {
                  borderColor: 'var(--armory-border)',
                  color: 'var(--armory-muted)',
                  background: 'color-mix(in srgb, var(--armory-input) 70%, transparent)',
                }
              : undefined
          }
        >
          {searchPlaceholder}
        </span>
      )}
    </div>
  )
}
