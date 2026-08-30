import { applyModuleToggle } from '@/lib/module-dependencies'
import type { ModuleDto } from '@/types/stack.types'
import { MODULE_IDS } from '@/setup/constants'

/**
 * Modules that give playerbots LLM-driven speech. They all own bot dialogue, so the backend install
 * hooks declare each of them in the others' `conflictsWith` and only one may be selected.
 * Ordered as the group renders them: the recommended choice first.
 */
export const AI_CHAT_MODULE_IDS: readonly string[] = [
  MODULE_IDS.ollamaChat,
  MODULE_IDS.ollamaBuddy,
  MODULE_IDS.llmChatter,
]

export const AI_CHAT_GROUP_NAME = 'AI Bot Chatting'

export const AI_CHAT_GROUP_DESCRIPTION =
  'Give playerbots LLM-driven speech. Pick one - they all drive bot dialogue and cannot be combined. '
  + 'The stack runs its own Ollama container, so no external LLM service is needed.'

export interface AiChatModuleOption {
  id: string
  name: string
  description: string
  recommended?: boolean
}

/** Copy used where the module catalog is not loaded (the Express wizard step). */
export const AI_CHAT_STATIC_OPTIONS: readonly AiChatModuleOption[] = [
  {
    id: MODULE_IDS.ollamaChat,
    name: 'Ollama Chat',
    description:
      'In-character bot dialogue: personalities, random chatter, event remarks. Express Setup turns off '
      + 'Playerbots built-in chatter so it does not overlap the LLM.',
    recommended: true,
  },
  {
    id: MODULE_IDS.ollamaBuddy,
    name: 'Ollama Bot Buddy',
    description:
      'LLM-driven bot actions (questing, grinding, chat overrides). Express Setup turns off Playerbots '
      + 'built-in chatter so it does not overlap the LLM.',
  },
  {
    id: MODULE_IDS.llmChatter,
    name: 'LLM Chatter',
    description:
      'Roleplay conversation engine: persistent personalities, memories, backstories, and zone-aware party, '
      + 'raid, battleground and guild dialogue. Adds a Python bridge container. Playerbots built-in chatter '
      + 'stays on, because this module speaks alongside it rather than replacing it.',
  },
]

export function isAiChatModuleId(moduleId: string): boolean {
  return AI_CHAT_MODULE_IDS.includes(moduleId)
}

interface CatalogEntry {
  id: string
  name: string
  description: string
  recommended?: boolean
}

/**
 * Group options for the modules the catalog actually offers, in group order. Server types that hide
 * an AI chat module simply drop it, and a server type that hides all of them drops the whole group.
 */
export function toAiChatOptions(catalog: readonly CatalogEntry[]): AiChatModuleOption[] {
  return AI_CHAT_MODULE_IDS.flatMap((id) => {
    const entry = catalog.find((module) => module.id === id)
    if (!entry) {
      return []
    }

    const fallback = AI_CHAT_STATIC_OPTIONS.find((option) => option.id === id)
    return [{
      id,
      name: entry.name,
      description: entry.description,
      recommended: entry.recommended ?? fallback?.recommended,
    }]
  })
}

/** The group member currently selected, or null when the stack has none. */
export function selectedAiChatModuleId(moduleIds: readonly string[]): string | null {
  return AI_CHAT_MODULE_IDS.find((id) => moduleIds.includes(id)) ?? null
}

/**
 * Replaces whichever group member is selected with <code>moduleId</code>, or clears the group for null.
 * Every member requires Playerbots, so passing the catalog pulls Playerbots in with the selection and
 * drops it again once nothing else needs it. Server types that bundle or require Playerbots keep it:
 * it is not in the catalog there, or the step re-adds it.
 */
export function selectAiChatModule(
  moduleIds: readonly string[],
  moduleId: string | null,
  catalog: readonly ModuleDto[] = [],
): string[] {
  const modules = [...catalog]
  const current = selectedAiChatModuleId(moduleIds)
  let next = [...moduleIds]

  if (current) {
    next = applyModuleToggle(current, next, modules) ?? next.filter((id) => id !== current)
  }
  next = next.filter((id) => !isAiChatModuleId(id))

  if (moduleId) {
    next = applyModuleToggle(moduleId, next, modules) ?? [...next, moduleId]
  }

  return next
}
