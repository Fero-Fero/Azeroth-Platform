import { AH_BOT_GUID_KEY, MODULE_IDS } from '@/setup/constants'

const MODULE_ENV_DEFAULTS: Record<string, Record<string, string>> = {
  [MODULE_IDS.ahBot]: { [AH_BOT_GUID_KEY]: '' },
}

export function envDefaultsForModule(moduleId: string): Record<string, string> | undefined {
  return MODULE_ENV_DEFAULTS[moduleId]
}

export function mergeModuleEnvDefaults(
  moduleId: string,
  serviceEnvVars: Record<string, Record<string, string>>,
): Record<string, Record<string, string>> {
  const defaults = envDefaultsForModule(moduleId)
  if (!defaults) {
    return serviceEnvVars
  }
  const worldserver = serviceEnvVars.worldserver ?? {}
  return { ...serviceEnvVars, worldserver: { ...defaults, ...worldserver } }
}
