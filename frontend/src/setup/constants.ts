export const MODULE_IDS = {
  ahBot: 'mod-ah-bot',
  playerbots: 'mod-playerbots',
  individualProgression: 'mod-individual-progression',
  dungeonSim: 'mod-playerbot-dungeon-sim',
  dungeonClear: 'mod-dungeon-clear',
  optimalBotRaid: 'mod-optimal-bot-raid',
  ale: 'mod-ale',
  ollamaBuddy: 'mod-ollama-bot-buddy',
  ollamaChat: 'mod-ollama-chat',
  llmChatter: 'mod-llm-chatter',
  artisans: 'mod-playerbots-artisans',
} as const

export const EXPRESS_LOCKED_MODULE_IDS = [
  MODULE_IDS.individualProgression,
  MODULE_IDS.playerbots,
  MODULE_IDS.optimalBotRaid,
  MODULE_IDS.ahBot,
  MODULE_IDS.ale,
] as const

/**
 * Modules that replace the playerbots built-in talk, so the setup flow offers to turn it off.
 * LLM Chatter is deliberately absent: it speaks alongside the built-in chatter.
 */
export const OLLAMA_MODULE_IDS = [MODULE_IDS.ollamaBuddy, MODULE_IDS.ollamaChat] as const

/** Playerbots built-in talk that overlaps Ollama Chat / Bot Buddy. Keep in sync with OllamaSidecar.PlayerbotsChatterDisable. */
export const OLLAMA_PLAYERBOTS_CHATTER_DISABLE: Readonly<Record<string, string>> = {
  'AiPlayerbot.EnableBroadcasts': '0',
  'AiPlayerbot.RandomBotTalk': '0',
  'AiPlayerbot.RandomBotEmote': '0',
  'AiPlayerbot.RandomBotSuggestDungeons': '0',
  'AiPlayerbot.EnableGreet': '0',
  'AiPlayerbot.GuildFeedback': '0',
  'AiPlayerbot.RandomBotSayWithoutMaster': '0',
}

export function expressDefaultModuleIds(aiChatId: string | null = MODULE_IDS.ollamaChat): string[] {
  return aiChatId ? [...EXPRESS_LOCKED_MODULE_IDS, aiChatId] : [...EXPRESS_LOCKED_MODULE_IDS]
}

export const STEP_IDS = {
  soapAdmin: 'soap-admin',
  dbcBaseline: 'dbc-baseline',
  uploadClient: 'upload-client',
  uploadArmoryDbc: 'upload-armory-dbc',
  moduleExtraData: 'module-extra-data',
  startStack: 'start-stack',
  stopStack: 'stop-stack',
  restartStack: 'restart-stack',
  disablePlayerbots: 'mod-playerbots-disable',
  reenablePlayerbots: 'mod-playerbots-reenable',
  waitDbImport: 'wait-db-import',
  prepareProgression: 'ip-prepare-progression',
  ipSyncHint: 'ip-sync-hint',
  ipPostSyncGuide: 'ip-post-sync-guide',
  ahBot: 'mod-ah-bot-setup',
  dungeonSim: 'mod-playerbot-dungeon-sim-notes',
  ollamaDisablePlayerbotsChatter: 'ollama-disable-playerbots-chatter',
} as const

export const GLOBAL_STEP_IDS = [
  STEP_IDS.soapAdmin,
  STEP_IDS.dbcBaseline,
  STEP_IDS.uploadClient,
  STEP_IDS.uploadArmoryDbc,
] as const

export const AH_BOT_GUID_KEY = 'AC_AUCTION_HOUSE_BOT_GUIDS'
export const PLAYERBOTS_ENABLED_KEY = 'AiPlayerbot.Enabled'
