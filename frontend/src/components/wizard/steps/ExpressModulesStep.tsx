import type { WizardForm } from '@/components/wizard/types'
import { EXPRESS_LOCKED_MODULE_IDS, MODULE_IDS } from '@/setup/constants'
import { cn } from '@/lib/utils'

const OPTIONAL = [
  { id: MODULE_IDS.artisans, name: 'Playerbots Artisans', hint: 'Profession bots that craft and gather.' },
  { id: MODULE_IDS.dungeonClear, name: 'Dungeon Clear', hint: 'Autonomous tank-led 5-man dungeon runs.' },
  { id: MODULE_IDS.dungeonSim, name: 'Playerbot Dungeon Sim', hint: 'Requires Dungeon Clear. Offscreen dungeon and raid progression.' },
] as const

interface ExpressModulesStepProps {
  form: WizardForm
}

export function ExpressModulesStep({ form }: ExpressModulesStepProps) {
  const selected = form.watch('moduleIds') ?? []
  const ollama = selected.includes(MODULE_IDS.ollamaBuddyAdvanced)
    ? MODULE_IDS.ollamaBuddyAdvanced
    : MODULE_IDS.ollamaBuddy

  const setModules = (next: string[]) => {
    form.setValue('moduleIds', next, { shouldDirty: true, shouldValidate: true })
  }

  const selectOllama = (id: string) => {
    const without = selected.filter(
      (moduleId) => moduleId !== MODULE_IDS.ollamaBuddy && moduleId !== MODULE_IDS.ollamaBuddyAdvanced,
    )
    setModules([...new Set([...without, ...EXPRESS_LOCKED_MODULE_IDS, id])])
  }

  const toggleOptional = (id: string) => {
    const next = new Set(selected)
    EXPRESS_LOCKED_MODULE_IDS.forEach((locked) => next.add(locked))
    next.add(ollama)
    if (next.has(id)) {
      next.delete(id)
      if (id === MODULE_IDS.dungeonClear) {
        next.delete(MODULE_IDS.dungeonSim)
      }
    } else {
      next.add(id)
      if (id === MODULE_IDS.dungeonSim) {
        next.add(MODULE_IDS.dungeonClear)
      }
    }
    setModules([...next])
  }

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Express modules</h2>
        <p className="mt-1 text-sm text-gray-500">
          Individual Progression, Playerbots, Optimal Bot Raid, and Auction House Bot are included.
          Choose an Ollama flavor and any extras.
        </p>
      </div>

      <div className="rounded-lg border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-700">
        Included: Individual Progression, Playerbots, Optimal Bot Raid, Auction House Bot
      </div>

      <fieldset className="space-y-2">
        <legend className="text-sm font-medium text-gray-700">Ollama Bot Buddy</legend>
        <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-gray-200 p-3">
          <input
            type="radio"
            className="mt-1"
            checked={ollama === MODULE_IDS.ollamaBuddy}
            onChange={() => selectOllama(MODULE_IDS.ollamaBuddy)}
          />
          <span>
            <span className="font-medium text-gray-900">Simple</span>
            <span className="mt-0.5 block text-xs text-gray-500">
              DustinHendrickson module. Lighter LLM control loop.
            </span>
          </span>
        </label>
        <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-gray-200 p-3">
          <input
            type="radio"
            className="mt-1"
            checked={ollama === MODULE_IDS.ollamaBuddyAdvanced}
            onChange={() => selectOllama(MODULE_IDS.ollamaBuddyAdvanced)}
          />
          <span>
            <span className="font-medium text-gray-900">Advanced</span>
            <span className="mt-0.5 block text-xs text-gray-500">
              Fero-Fero fork with bot memory. Slightly more expensive at runtime.
            </span>
          </span>
        </label>
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="text-sm font-medium text-gray-700">Optional</legend>
        {OPTIONAL.map((item) => {
          const checked = selected.includes(item.id)
          return (
            <label
              key={item.id}
              className={cn(
                'flex cursor-pointer items-start gap-3 rounded-lg border p-3',
                checked ? 'border-blue-300 bg-blue-50' : 'border-gray-200',
              )}
            >
              <input
                type="checkbox"
                className="mt-1"
                checked={checked}
                onChange={() => toggleOptional(item.id)}
              />
              <span>
                <span className="font-medium text-gray-900">{item.name}</span>
                <span className="mt-0.5 block text-xs text-gray-500">{item.hint}</span>
              </span>
            </label>
          )
        })}
      </fieldset>
    </div>
  )
}
