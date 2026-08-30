/**
 * Summary of the shared BASE WoW client the admin uploads on the global Client tab. This seed is what
 * every stack's client container serves as its read-only base layer.
 */
export interface ClientBaseInfoDto {
  exists: boolean
  volumeExists?: boolean
  inspectionWarning?: string | null
  fileCount: number
  totalSize: number
  hasWowExe: boolean
  hasDataMpq: boolean
  gamePath: string
  downloadAvailable?: boolean
  downloadUnavailableReason?: string | null
  /**
   * Set when the files changed but the launcher manifest could not be refreshed immediately (client
   * container stopped, engine unreachable). The change is not lost; propagation to players is delayed.
   */
  manifestWarning?: string | null
  /**
   * What the stack's client container is currently serving. Null when there is no client container or
   * it could not be reached. Compare against the volume figures above: a mismatch means players are
   * still being offered the previous content.
   */
  manifest?: ClientManifestStatusDto | null
}

/** Summary of the manifest launchers are currently downloading against. */
export interface ClientManifestStatusDto {
  /** Hash-of-hashes over the served content; changes whenever any file does. */
  version: string
  /** Bumped by "force re-validate" to make launchers re-hash their local files. */
  verifyToken: string
  fileCount: number
  /** Files served from the uploaded base client. */
  baseFileCount: number
  /** Files served from this stack's overlay (published patches, server addons). */
  managedFileCount: number
  totalSize: number
  /** ISO timestamp of the last manifest build, or null if it has not built yet. */
  builtAtUtc?: string | null
  /** False when no signing key is provisioned, so launchers cannot verify the manifest. */
  signed: boolean
}

/** A single entry (file or sub-directory) within the base client tree. */
export interface ClientBrowseEntryDto {
  name: string
  isDirectory: boolean
  /** File size in bytes (0 for directories). */
  size: number
  /** Number of immediate children (directories only). */
  itemCount: number
  /** Path relative to the base client root, using '/' separators. */
  relativePath: string
  /** True when the entry is visible but cannot be deleted from the browser. */
  isLocked?: boolean
  /** Shown on the lock icon when the entry is locked. */
  lockReason?: string | null
}

/** Listing of one directory level within the base client tree. */
export interface ClientBrowseResultDto {
  /** The listed directory's path relative to the base root ('' = root). */
  path: string
  exists: boolean
  entries: ClientBrowseEntryDto[]
}

/** Result of rebuilding a stack's distributable client manifest. */
export interface ClientManifestRebuildResultDto {
  version: string
  verifyToken: string
  fileCount: number
  totalSize: number
  baseFileCount: number
  baseTotalSize: number
  managedFileCount: number
  managedTotalSize: number
}
