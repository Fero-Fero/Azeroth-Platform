/** Small amber pill for features that are usable but not finished. */
export function WipBadge() {
  return (
    <span
      className="inline-flex shrink-0 items-center rounded px-1.5 py-px text-[10px] font-bold uppercase leading-4 tracking-wide bg-amber-400 text-amber-950 ring-1 ring-amber-600/35"
      title="This feature is still being finished"
    >
      Work in progress
    </span>
  )
}

/** The progression sync action name plus its WIP mark, for inline copy. */
export function SyncWithIndividualProgressionLabel({
  as: Tag = 'strong',
}: {
  as?: 'strong' | 'span'
}) {
  return (
    <Tag className="inline-flex flex-wrap items-center gap-x-1.5 gap-y-0.5 align-middle">
      Sync with mod-individual-progression
      <WipBadge />
    </Tag>
  )
}
