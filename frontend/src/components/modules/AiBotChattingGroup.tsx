import { Bot } from 'lucide-react'
import {
  AI_CHAT_GROUP_DESCRIPTION,
  AI_CHAT_GROUP_NAME,
  type AiChatModuleOption,
} from '@/lib/ai-chat-modules'
import { cn } from '@/lib/utils'

interface AiBotChattingGroupProps {
  options: readonly AiChatModuleOption[]
  selectedId: string | null
  onSelect: (moduleId: string | null) => void
  disabled?: boolean
  /** Copy for the "none of them" choice, which differs between first-time setup and an existing stack. */
  noneLabel?: string
  noneDescription?: string
}

/**
 * Single-select card for the AI chat modules. They are pulled out of the flat module list so the
 * three near-identical entries do not read as independently combinable, which they are not.
 */
export default function AiBotChattingGroup({
  options,
  selectedId,
  onSelect,
  disabled = false,
  noneLabel = 'None',
  noneDescription = 'Playerbots keep their normal chatter. You can add one of these later.',
}: AiBotChattingGroupProps) {
  if (options.length === 0) {
    return null
  }

  const name = 'ai-bot-chatting'

  return (
    <section className="rounded-xl border border-slate-200 bg-white p-4">
      <div className="flex items-start gap-2">
        <Bot className="mt-0.5 h-4 w-4 shrink-0 text-slate-400" aria-hidden="true" />
        <div>
          <h4 className="text-sm font-semibold text-slate-800">{AI_CHAT_GROUP_NAME}</h4>
          <p className="mt-0.5 text-xs text-slate-500">{AI_CHAT_GROUP_DESCRIPTION}</p>
        </div>
      </div>

      <fieldset className="mt-3 space-y-2" disabled={disabled}>
        <legend className="sr-only">{AI_CHAT_GROUP_NAME}</legend>
        {options.map((option) => (
          <AiChatChoice
            key={option.id}
            name={name}
            label={option.name}
            description={option.description}
            recommended={option.recommended}
            checked={selectedId === option.id}
            disabled={disabled}
            onSelect={() => onSelect(option.id)}
          />
        ))}
        <AiChatChoice
          name={name}
          label={noneLabel}
          description={noneDescription}
          checked={selectedId === null}
          disabled={disabled}
          onSelect={() => onSelect(null)}
        />
      </fieldset>
    </section>
  )
}

function AiChatChoice({
  name,
  label,
  description,
  recommended = false,
  checked,
  disabled,
  onSelect,
}: {
  name: string
  label: string
  description: string
  recommended?: boolean
  checked: boolean
  disabled: boolean
  onSelect: () => void
}) {
  return (
    <label
      className={cn(
        'flex items-start gap-3 rounded-lg border p-3',
        checked ? 'border-blue-500 bg-blue-50/80' : 'border-slate-200 bg-white',
        disabled ? 'cursor-not-allowed opacity-60' : 'cursor-pointer hover:border-blue-200',
      )}
    >
      <input
        type="radio"
        name={name}
        className="mt-1"
        checked={checked}
        disabled={disabled}
        onChange={onSelect}
      />
      <span className="min-w-0 flex-1">
        <span className="text-sm font-medium text-slate-900">{label}</span>
        {recommended && (
          <span className="ml-2 rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
            Recommended
          </span>
        )}
        <span className="mt-0.5 block wrap-break-word text-xs text-slate-500">{description}</span>
      </span>
    </label>
  )
}
