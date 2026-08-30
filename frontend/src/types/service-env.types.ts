import type { ConfigOptionType } from '@/types/module-config.types'

/**
 * Declares the environment variables a single stack service (worldserver, authserver, armory,
 * client) accepts. Mirrors the backend ServiceEnvTemplate so the schema-driven form UI can render it.
 */
export interface ServiceEnvTemplate {
  serviceId: string
  serviceName: string
  description: string
  options: ServiceEnvOption[]
}

export interface ServiceEnvOption {
  key: string
  envVarName: string
  defaultValue: string
  type: ConfigOptionType
  description: string
  enumOptions?: string[] | null
}

/** Per-service env values: serviceId -> (envVarName -> value). */
export type ServiceEnvVars = Record<string, Record<string, string>>
