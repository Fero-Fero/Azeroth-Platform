export const MODULE_IDS = {
  ahBot: 'mod-ah-bot',
  playerbots: 'mod-playerbots',
  individualProgression: 'mod-individual-progression',
  dungeonSim: 'mod-playerbot-dungeon-sim',
  dungeonClear: 'mod-dungeon-clear',
  optimalBotRaid: 'mod-optimal-bot-raid',
  ollamaBuddy: 'mod-ollama-bot-buddy',
  ollamaBuddyAdvanced: 'mod-ollama-bot-buddy-advanced',
  artisans: 'mod-playerbots-artisans',
} as const

export const EXPRESS_LOCKED_MODULE_IDS = [
  MODULE_IDS.individualProgression,
  MODULE_IDS.playerbots,
  MODULE_IDS.optimalBotRaid,
  MODULE_IDS.ahBot,
] as const

export function expressDefaultModuleIds(ollamaId: string = MODULE_IDS.ollamaBuddy): string[] {
  return [...EXPRESS_LOCKED_MODULE_IDS, ollamaId]
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
  prepareProgression: 'ip-prepare-progression',
  ipSyncHint: 'ip-sync-hint',
  expressProvision: 'express-provision',
  ahBot: 'mod-ah-bot-setup',
  dungeonSim: 'mod-playerbot-dungeon-sim-notes',
} as const

export const GLOBAL_STEP_IDS = [
  STEP_IDS.soapAdmin,
  STEP_IDS.dbcBaseline,
  STEP_IDS.uploadClient,
  STEP_IDS.uploadArmoryDbc,
] as const

export const AH_BOT_GUID_KEY = 'AC_AUCTION_HOUSE_BOT_GUIDS'
export const PLAYERBOTS_ENABLED_KEY = 'AiPlayerbot.Enabled'
