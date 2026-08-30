import { describe, expect, it } from 'vitest'
import {
  AI_CHAT_MODULE_IDS,
  AI_CHAT_STATIC_OPTIONS,
  isAiChatModuleId,
  selectAiChatModule,
  selectedAiChatModuleId,
  toAiChatOptions,
} from '@/lib/ai-chat-modules'
import type { ModuleDto } from '@/types/stack.types'
import { MODULE_IDS, OLLAMA_MODULE_IDS } from '@/setup/constants'

function entry(id: string, requiredModuleIds: string[] = []): ModuleDto {
  return { id, name: id, description: id, repository: id, branch: 'master', requiredModuleIds }
}

const CATALOG: ModuleDto[] = [
  entry(MODULE_IDS.playerbots),
  entry(MODULE_IDS.artisans, [MODULE_IDS.playerbots]),
  ...AI_CHAT_MODULE_IDS.map((id) => entry(id, [MODULE_IDS.playerbots])),
]

describe('ai-chat-modules', () => {
  it('covers the three mutually exclusive modules', () => {
    expect(AI_CHAT_MODULE_IDS).toEqual([
      MODULE_IDS.ollamaChat,
      MODULE_IDS.ollamaBuddy,
      MODULE_IDS.llmChatter,
    ])
    expect(isAiChatModuleId(MODULE_IDS.llmChatter)).toBe(true)
    expect(isAiChatModuleId(MODULE_IDS.playerbots)).toBe(false)
  })

  it('leaves LLM Chatter out of the playerbots chatter disable set', () => {
    expect(OLLAMA_MODULE_IDS).not.toContain(MODULE_IDS.llmChatter)
  })

  it('reports the selected member and null when none is selected', () => {
    expect(selectedAiChatModuleId([MODULE_IDS.playerbots])).toBeNull()
    expect(selectedAiChatModuleId([MODULE_IDS.playerbots, MODULE_IDS.llmChatter]))
      .toBe(MODULE_IDS.llmChatter)
  })

  it('swaps one member for another and never keeps two', () => {
    const next = selectAiChatModule(
      [MODULE_IDS.playerbots, MODULE_IDS.ollamaChat],
      MODULE_IDS.llmChatter,
    )

    expect(next).toContain(MODULE_IDS.playerbots)
    expect(next).toContain(MODULE_IDS.llmChatter)
    expect(next).not.toContain(MODULE_IDS.ollamaChat)
    expect(next.filter(isAiChatModuleId)).toHaveLength(1)
  })

  it('clears the group without touching other modules', () => {
    expect(selectAiChatModule([MODULE_IDS.playerbots, MODULE_IDS.ollamaBuddy], null))
      .toEqual([MODULE_IDS.playerbots])
  })

  it('brings Playerbots in with the selection and drops it when the group is cleared', () => {
    const withChat = selectAiChatModule([], MODULE_IDS.llmChatter, CATALOG)
    expect(withChat).toContain(MODULE_IDS.playerbots)

    expect(selectAiChatModule(withChat, null, CATALOG)).toEqual([])
  })

  it('keeps Playerbots when another selected module still needs it', () => {
    const next = selectAiChatModule(
      [MODULE_IDS.playerbots, MODULE_IDS.artisans, MODULE_IDS.ollamaChat],
      null,
      CATALOG,
    )

    expect(next).toContain(MODULE_IDS.playerbots)
    expect(next).toContain(MODULE_IDS.artisans)
    expect(next).not.toContain(MODULE_IDS.ollamaChat)
  })

  it('keeps Playerbots when swapping one member for another', () => {
    const next = selectAiChatModule([MODULE_IDS.playerbots, MODULE_IDS.ollamaChat], MODULE_IDS.llmChatter, CATALOG)

    expect(next).toEqual(expect.arrayContaining([MODULE_IDS.playerbots, MODULE_IDS.llmChatter]))
    expect(next).not.toContain(MODULE_IDS.ollamaChat)
  })

  it('leaves Playerbots alone on server types that do not list it', () => {
    const bundled = CATALOG.filter((module) => module.id !== MODULE_IDS.playerbots)

    expect(selectAiChatModule([], MODULE_IDS.llmChatter, bundled)).toEqual([MODULE_IDS.llmChatter])
  })

  it('offers only the catalog entries the server type exposes, in group order', () => {
    const options = toAiChatOptions([
      { id: MODULE_IDS.llmChatter, name: 'LLM Chatter', description: 'Roleplay chatter.' },
      { id: MODULE_IDS.playerbots, name: 'Playerbots', description: 'Bots.' },
      { id: MODULE_IDS.ollamaChat, name: 'Ollama Chat', description: 'In-character chat.' },
    ])

    expect(options.map((option) => option.id)).toEqual([MODULE_IDS.ollamaChat, MODULE_IDS.llmChatter])
    expect(options[0].description).toBe('In-character chat.')
  })

  it('marks Ollama Chat as the recommended choice when the catalog is silent', () => {
    const options = toAiChatOptions([
      { id: MODULE_IDS.ollamaChat, name: 'Ollama Chat', description: 'In-character chat.' },
    ])

    expect(options[0].recommended).toBe(true)
    expect(AI_CHAT_STATIC_OPTIONS.filter((option) => option.recommended)).toHaveLength(1)
  })

  it('describes every member in the static copy used by the Express wizard', () => {
    expect(AI_CHAT_STATIC_OPTIONS.map((option) => option.id)).toEqual([...AI_CHAT_MODULE_IDS])
  })
})
