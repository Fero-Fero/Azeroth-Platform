import type { CustomSetup } from '@/server-types/types'

export function CustomSetupNotice({
  setups,
  selectedModuleIds,
}: {
  setups: CustomSetup[] | undefined
  selectedModuleIds: string[]
}) {
  const visible = (setups ?? []).filter((setup) =>
    setup.requiresModuleIds.every((id) => selectedModuleIds.includes(id)),
  )

  if (visible.length === 0) {
    return null
  }

  return (
    <div className="space-y-2">
      {visible.map((setup) => (
        <p key={setup.id} className="text-violet-800">
          {setup.notice ?? (
            <>
              After creating the stack, complete <strong>{setup.title}</strong> — {setup.description}
            </>
          )}
        </p>
      ))}
    </div>
  )
}
