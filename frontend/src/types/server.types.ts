// Lua scripts served to a stack's worldserver (via Eluna)
export interface LuaScriptFileDto {
  path: string
  isDirectory: boolean
  size: number
}

export interface LuaScriptListDto {
  stackId: string
  elunaPresent: boolean
  files: LuaScriptFileDto[]
  totalSize: number
}

export interface LuaScriptContentDto {
  path: string
  content: string
}

// Server .conf files (worldserver.conf, authserver.conf, module confs)
export interface ServerConfigFileDto {
  path: string
  size: number
  modifiedAt: string
  // "modules" for files under modules/, otherwise "server".
  category: 'server' | 'modules'
}

export interface ServerConfigListDto {
  stackId: string
  generated: boolean
  files: ServerConfigFileDto[]
}

export interface ServerConfigContentDto {
  path: string
  content: string
}

// Point-in-time snapshot of a stack's databases, server config, and optional checkpoint images
export interface RevisionDto {
  id: string
  stackId: string
  createdAt: string
  // "pre-update" or "manual"
  reason: string
  // "creating", "ready", or "failed"
  status: string
  error?: string | null
  coreCommitSha: string
  appliedPatchLevel: number
  sizeBytes: number
}
