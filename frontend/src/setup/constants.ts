export const MODULE_IDS = {
  ahBot: 'mod-ah-bot',
  playerbots: 'mod-playerbots',
  individualProgression: 'mod-individual-progression',
  dungeonSim: 'mod-playerbot-dungeon-sim',
} as const

export const STEP_IDS = {
  soapAdmin: 'soap-admin',
  uploadClient: 'upload-client',
  uploadArmoryDbc: 'upload-armory-dbc',
  startStack: 'start-stack',
  stopStack: 'stop-stack',
  restartStack: 'restart-stack',
  disablePlayerbots: 'mod-playerbots-disable',
  reenablePlayerbots: 'mod-playerbots-reenable',
  prepareProgression: 'ip-prepare-progression',
  ipSyncHint: 'ip-sync-hint',
  ahBot: 'mod-ah-bot-setup',
  dungeonSim: 'mod-playerbot-dungeon-sim-notes',
} as const

export const GLOBAL_STEP_IDS = [
  STEP_IDS.soapAdmin,
  STEP_IDS.uploadClient,
  STEP_IDS.uploadArmoryDbc,
] as const

export const AH_BOT_GUID_KEY = 'AC_AUCTION_HOUSE_BOT_GUIDS'
export const PLAYERBOTS_ENABLED_KEY = 'AiPlayerbot.Enabled'
