import { describe, expect, it } from 'vitest'
import { confValuesMatch, getConfValue, setConfValue, setConfValues } from '@/lib/conf-file'
import { OLLAMA_PLAYERBOTS_CHATTER_DISABLE, expressDefaultModuleIds, MODULE_IDS } from '@/setup/constants'

describe('conf-file', () => {
  it('replaces an existing key and appends a missing one', () => {
    const next = setConfValues('AiPlayerbot.EnableBroadcasts = 1\n', {
      'AiPlayerbot.EnableBroadcasts': '0',
      'AiPlayerbot.EnableGreet': '0',
    })
    expect(getConfValue(next, 'AiPlayerbot.EnableBroadcasts')).toBe('0')
    expect(getConfValue(next, 'AiPlayerbot.EnableGreet')).toBe('0')
  })

  it('ignores commented assignment lines when reading and writing', () => {
    const content = '# AiPlayerbot.Enabled = 1\nAiPlayerbot.Enabled = 1 # default\n'
    expect(getConfValue(content, 'AiPlayerbot.Enabled')).toBe('1')
    expect(getConfValue(setConfValue(content, 'AiPlayerbot.Enabled', '0'), 'AiPlayerbot.Enabled')).toBe('0')
    expect(setConfValue(content, 'AiPlayerbot.Enabled', '0')).toContain('# AiPlayerbot.Enabled = 1')
  })

  it('matches the Ollama playerbots chatter disable set only when every key is 0', () => {
    let content = 'AiPlayerbot.Enabled = 1\n'
    expect(confValuesMatch(content, OLLAMA_PLAYERBOTS_CHATTER_DISABLE)).toBe(false)
    content = setConfValues(content, OLLAMA_PLAYERBOTS_CHATTER_DISABLE)
    expect(confValuesMatch(content, OLLAMA_PLAYERBOTS_CHATTER_DISABLE)).toBe(true)
    content = setConfValue(content, 'AiPlayerbot.RandomBotTalk', '1')
    expect(confValuesMatch(content, OLLAMA_PLAYERBOTS_CHATTER_DISABLE)).toBe(false)
  })
})

describe('expressDefaultModuleIds', () => {
  it('defaults to Ollama Chat and can omit Ollama', () => {
    expect(expressDefaultModuleIds()).toContain(MODULE_IDS.ollamaChat)
    expect(expressDefaultModuleIds(null)).not.toContain(MODULE_IDS.ollamaChat)
    expect(expressDefaultModuleIds(null)).not.toContain(MODULE_IDS.ollamaBuddy)
    expect(expressDefaultModuleIds(MODULE_IDS.ollamaBuddy)).toContain(MODULE_IDS.ollamaBuddy)
  })
})
