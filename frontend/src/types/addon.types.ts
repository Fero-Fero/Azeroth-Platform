// Mirrors backend AzerothPlatform.Core.Contracts.AddonDtos

export interface AddonSummaryDto {
  name: string
  fileCount: number
  totalSize: number
  recommended: boolean
}

export interface AddonListDto {
  isStackScoped: boolean
  stackId: string | null
  addons: AddonSummaryDto[]
  totalSize: number
}

export interface AddonCatalogEntryDto {
  id: string
  name: string
  description: string
  category: string
  downloadUrl: string
  website?: string | null
  isBuiltIn: boolean
  folders: string[]
  installed: boolean
  recommended: boolean
  relatedModuleIds?: string[]
  suggested?: boolean
}
