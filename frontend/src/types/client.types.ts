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
