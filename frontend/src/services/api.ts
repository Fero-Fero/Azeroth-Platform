import axios from 'axios'
import type { 
  StackConfigurationDto, 
  StackDetailsDto,
  DeploymentConfigDto,
  BuildStatusDto,
  ModuleDto,
  SaveModuleRequest,
  ValidationResultDto,
  ServerType,
  StackUpdateStatusDto,
  DiscoveredStackDto,
  ImportStackRequestDto,
  SoapCredentialsDto,
  DatabaseCredentialsDto,
  InitializeAdminResponseDto,
} from '@/types/stack.types'
import type { ModuleConfigSchema } from '@/types/moduleConfig'

const apiClient = axios.create({
  baseURL: '/api',
  headers: {
    'Content-Type': 'application/json',
  },
})

/** localStorage key holding the admin bearer token. */
export const AUTH_TOKEN_KEY = 'azp_admin_token'

export const authToken = {
  get: () => localStorage.getItem(AUTH_TOKEN_KEY),
  set: (token: string) => localStorage.setItem(AUTH_TOKEN_KEY, token),
  clear: () => localStorage.removeItem(AUTH_TOKEN_KEY),
}

// Attach the admin bearer token to every request.
apiClient.interceptors.request.use((config) => {
  const token = authToken.get()
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  // FormData must not use the default application/json header — the browser needs to
  // set multipart/form-data with a boundary or ASP.NET cannot bind [FromForm] IFormFile.
  if (config.data instanceof FormData) {
    delete config.headers['Content-Type']
  }
  return config
})

// On 401, drop the token and bounce to the login page (unless we're already there / logging in).
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error?.response?.status
    const url: string = error?.config?.url ?? ''
    if (status === 401 && !url.includes('/auth/login')) {
      authToken.clear()
      if (!window.location.pathname.startsWith('/admin/login')) {
        window.location.href = '/admin/login'
      }
    }
    return Promise.reject(error)
  }
)

export default apiClient

// Auth API
export const authApi = {
  login: (password: string) => apiClient.post<{ token: string }>('/auth/login', { password }),
  me: () => apiClient.get<{ authenticated: boolean; name: string }>('/auth/me'),
  logout: () => apiClient.post('/auth/logout'),
}

// Stack API
export const stackApi = {
  // List all stacks
  list: () => apiClient.get<StackDetailsDto[]>('/stacks'),
  probeAllStatus: () =>
    apiClient.post<StackDetailsDto[]>('/stacks/probe-status', undefined, { timeout: 300_000 }),
  
  // Get stack details
  get: (stackId: string) =>
    apiClient.get<StackDetailsDto>(`/stacks/${stackId}`, { timeout: 90_000 }),
  
  // Create stack
  create: (config: StackConfigurationDto) => 
    apiClient.post<{ stackId: string; status: string }>('/stacks', config),
  
  // Update stack configuration
  updateConfig: (stackId: string, config: StackConfigurationDto) => 
    apiClient.put<StackDetailsDto>(`/stacks/${stackId}`, config),

  reconnectExternal: (stackId: string, deployment: DeploymentConfigDto) =>
    apiClient.post<StackDetailsDto>(`/stacks/${stackId}/reconnect-external`, deployment),
  
  // Delete stack
  delete: (stackId: string) => apiClient.delete(`/stacks/${stackId}`),
  
  // Control operations. These run as detached background jobs and return the initial job status so the
  // UI can reattach (see jobStatus + the /hubs/stack-progress stream).
  start: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').StackJobStatus>(`/stacks/${stackId}/start`),
  startDatabase: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').StackJobStatus>(`/stacks/${stackId}/start-database`),
  stop: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').StackJobStatus>(`/stacks/${stackId}/stop`),
  forceStop: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').StackJobStatus>(`/stacks/${stackId}/force-stop`),
  restart: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').StackJobStatus>(`/stacks/${stackId}/restart`),
  // Current lifecycle background-job status (null if none has run); used to reattach after navigating.
  jobStatus: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').StackJobStatus | null>(`/stacks/${stackId}/job/status`),
  startArmory: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').ArmoryJobStatus>(`/stacks/${stackId}/armory/start`),
  stopArmory: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').ArmoryJobStatus>(`/stacks/${stackId}/armory/stop`),
  // Current armory background-job status (null if none has run); used to reattach after a refresh.
  armoryStatus: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').ArmoryJobStatus | null>(`/stacks/${stackId}/armory/status`),
  startClient: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').ClientJobStatus>(`/stacks/${stackId}/client/start`),
  stopClient: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').ClientJobStatus>(`/stacks/${stackId}/client/stop`),
  clientStatus: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').ClientJobStatus | null>(`/stacks/${stackId}/client/status`),

  // Player-facing HTTP network settings (armory/client host ports + publish bind interface).
  armoryNetwork: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').ArmoryNetworkConfig>(`/stacks/${stackId}/armory/network`),
  updateArmoryNetwork: (stackId: string, config: import('@/types/stack.types').ArmoryNetworkConfig) =>
    apiClient.put<import('@/types/stack.types').ArmoryNetworkConfig>(`/stacks/${stackId}/armory/network`, config),
  vpcSecurityProfile: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').VpcSecurityProfileDto>(`/stacks/${stackId}/vpc-security-profile`),
  syncVpcFirewall: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').RemoteSetupResultDto>(
      `/stacks/${stackId}/sync-vpc-firewall`,
      undefined,
      { timeout: 300_000 },
    ),
  vpcFirewallStatus: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').VpcFirewallStatusDto>(
      `/stacks/${stackId}/vpc-firewall-status`,
      { timeout: 120_000 },
    ),
  vpcSshLogs: (stackId: string, limit = 100) =>
    apiClient.get<import('@/types/stack.types').VpcSshLogsDto>(`/stacks/${stackId}/vpc-ssh-logs`, {
      params: { limit },
      timeout: 120_000,
    }),
  provisionVpcDocker: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').RemoteSetupResultDto>(
      `/stacks/${stackId}/provision-vpc-docker`,
      undefined,
      { timeout: 300_000 },
    ),
  armoryAccountsStatus: (stackId: string) =>
    apiClient.get<import('@/types/stack.types').ArmoryAccountsStatusDto>(`/stacks/${stackId}/armory/accounts-status`),
  sendArmoryTestEmail: (stackId: string, testEmailAddress: string) =>
    apiClient.post<import('@/types/stack.types').ArmoryTestEmailResultDto>(`/stacks/${stackId}/armory/test-email`, {
      testEmailAddress,
    }),

  // Per-service (per-container) lifecycle: start | stop | restart | recreate
  serviceAction: (
    stackId: string,
    service: string,
    action: import('@/types/stack.types').StackServiceAction
  ) => apiClient.post(`/stacks/${stackId}/services/${encodeURIComponent(service)}/${action}`),
  
  // Update operations
  checkUpdates: (stackId: string) => 
    apiClient.post<StackUpdateStatusDto>(`/stacks/${stackId}/check-updates`),
  update: (stackId: string, configMigrationMode?: import('@/components/config/ConfigMigrationModeChoice').ConfigMigrationMode) =>
    apiClient.post<BuildStatusDto>(`/stacks/${stackId}/update`, undefined, {
      params: configMigrationMode ? { configMigrationMode } : undefined,
    }),
  
  // Import operations
  discover: () => 
    apiClient.get<DiscoveredStackDto[]>('/stacks/discover'),
  import: (stackId: string, request: ImportStackRequestDto) =>
    apiClient.post<StackDetailsDto>(`/stacks/import/${stackId}`, request),
  
  // Admin account initialization
  initializeAdmin: (stackId: string) =>
    apiClient.post<InitializeAdminResponseDto>(`/stacks/${stackId}/initialize-admin`),

  // SOAP credentials recovery
  getSoapCredentials: (stackId: string) =>
    apiClient.get<SoapCredentialsDto>(`/stacks/${stackId}/soap-credentials`),

  // Database root credentials recovery (audited reveal; not in the standard detail payload)
  getDatabaseCredentials: (stackId: string) =>
    apiClient.get<DatabaseCredentialsDto>(`/stacks/${stackId}/database-credentials`),

  getDockerOverview: (stackId: string) =>
    apiClient.get<import('@/types/docker.types').StackDockerOverviewDto>(`/stacks/${stackId}/docker`, {
      // External stacks query the remote engine over SSH; many docker calls can take a while.
      timeout: 180_000,
    }),

  deleteDockerBuildFiles: (stackId: string) =>
    apiClient.delete<import('@/types/docker.types').StackDockerDeleteResultDto>(`/stacks/${stackId}/docker/build-files`),

  deleteDockerImage: (stackId: string, imageId: string) =>
    apiClient.delete<import('@/types/docker.types').StackDockerDeleteResultDto>(
      `/stacks/${stackId}/docker/images`,
      { params: { imageId } },
    ),

  deleteDockerVolume: (stackId: string, volumeName: string) =>
    apiClient.delete<import('@/types/docker.types').StackDockerDeleteResultDto>(
      `/stacks/${stackId}/docker/volumes/${encodeURIComponent(volumeName)}`,
    ),

  getDockerVolumeAudit: (stackId: string) =>
    apiClient.get<import('@/types/docker.types').DockerVolumeAuditDto>(
      `/stacks/${stackId}/docker/volume-audit`,
      { timeout: 180_000 },
    ),

  cleanupDockerVolumeAudit: (
    stackId: string,
    request: import('@/types/docker.types').DockerVolumeCleanupRequestDto,
  ) =>
    apiClient.post<import('@/types/docker.types').DockerVolumeCleanupResultDto>(
      `/stacks/${stackId}/docker/volume-audit/cleanup`,
      request,
    ),

  // Module configuration (post-setup env var overrides)
  applyModuleConfig: (stackId: string, envVars: Record<string, string>) =>
    apiClient.post<{ success: boolean; message: string }>(`/stacks/${stackId}/module-config`, { envVars }),
}

export const dockerApi = {
  getOverview: () => apiClient.get<import('@/types/docker.types').DockerEngineOverviewDto>('/docker/overview'),

  getDiskUsage: () => apiClient.get<import('@/types/docker.types').DockerDiskUsageDto>('/docker/disk'),

  deleteEngineVolume: (volumeName: string) =>
    apiClient.delete<import('@/types/docker.types').StackDockerDeleteResultDto>(
      `/docker/volumes/${encodeURIComponent(volumeName)}`,
    ),

  deleteEngineImage: (imageId: string) =>
    apiClient.delete<import('@/types/docker.types').StackDockerDeleteResultDto>('/docker/images', {
      params: { imageId },
    }),

  cleanupUnused: () =>
    apiClient.post<import('@/types/docker.types').DockerCleanupJobStatus>('/docker/cleanup'),
  cleanupOldBuilds: () =>
    apiClient.post<import('@/types/docker.types').DockerCleanupJobStatus>('/docker/cleanup/old-builds'),
  cleanupStatus: () =>
    apiClient.get<import('@/types/docker.types').DockerCleanupJobStatus | null>('/docker/cleanup/status'),

  getManagerFiles: (path?: string) =>
    apiClient.get<import('@/types/docker.types').DockerManagerFilesDto>('/docker/manager/files', {
      params: path ? { path } : undefined,
    }),

  deleteManagerFile: (path: string) =>
    apiClient.delete<import('@/types/docker.types').StackDockerDeleteResultDto>('/docker/manager/files', {
      params: { path },
    }),

  cleanupManagerMirrors: () =>
    apiClient.post<import('@/types/docker.types').DockerManagerMirrorCleanupResultDto>(
      '/docker/manager/cleanup-mirrors',
    ),

  migrateClientMirrors: () =>
    apiClient.post<import('@/types/docker.types').DockerManagerMirrorCleanupResultDto>(
      '/docker/manager/migrate-client-mirrors',
    ),

  getPlatformKeys: () =>
    apiClient.get<import('@/types/docker.types').DockerPlatformKeysDto>('/docker/platform-keys'),
}

// Build API
export const buildApi = {
  // Start build (configuration optional for rebuilds)
  start: (
    stackId: string,
    config?: StackConfigurationDto,
    configMigrationMode?: import('@/components/config/ConfigMigrationModeChoice').ConfigMigrationMode,
  ) =>
    apiClient.post<{ buildId: string; status: string }>(
      `/stacks/${stackId}/build`,
      config ?? null,
      { params: configMigrationMode ? { configMigrationMode } : undefined },
    ),
  
  // Get build status
  status: (stackId: string) => apiClient.get<BuildStatusDto>(`/stacks/${stackId}/build/status`),
  
  // Cancel build
  cancel: (stackId: string) => apiClient.post(`/stacks/${stackId}/build/cancel`),
}

// Characters API
export const charactersApi = {
  createAhBotAccount: (stackId: string) =>
    apiClient.post<import('@/types/account.types').AhBotSetupResultDto>(`/stacks/${stackId}/characters/ahbot-account`),
}

// Module API
export const moduleApi = {
  list: (serverType?: ServerType) => 
    apiClient.get<ModuleDto[]>('/modules', { params: { serverType } }),
  
  getConfig: (moduleId: string) =>
    apiClient.get<ModuleConfigSchema>(`/modules/${moduleId}/config`),

  // Per-service environment-variable templates (worldserver, authserver, armory, client).
  serviceEnvTemplates: () =>
    apiClient.get<import('@/types/serviceEnv').ServiceEnvTemplate[]>('/service-env-templates'),

  // Catalog administration (built-in + custom modules)
  catalog: () => apiClient.get<ModuleDto[]>('/modules/catalog'),

  create: (request: SaveModuleRequest) =>
    apiClient.post<ModuleDto>('/modules', request),

  uploadPackage: (fields: { id: string; name: string; description: string }, file: File) => {
    const form = new FormData()
    form.append('id', fields.id)
    form.append('name', fields.name)
    form.append('description', fields.description)
    form.append('file', file)
    return apiClient.post<ModuleDto>('/modules/upload', form, {
      headers: { 'Content-Type': 'multipart/form-data' },
    })
  },

  replacePackage: (moduleId: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<ModuleDto>(`/modules/${encodeURIComponent(moduleId)}/package`, form, {
      headers: { 'Content-Type': 'multipart/form-data' },
    })
  },

  update: (moduleId: string, request: SaveModuleRequest) =>
    apiClient.put<ModuleDto>(`/modules/${encodeURIComponent(moduleId)}`, request),

  remove: (moduleId: string) =>
    apiClient.delete<{ success: boolean }>(`/modules/${encodeURIComponent(moduleId)}`),

  readme: (moduleId: string) =>
    apiClient.get<import('@/types/stack.types').ModuleReadmeDto>(`/modules/${encodeURIComponent(moduleId)}/readme`),

  community: (params?: { search?: string; sort?: string; page?: number; pageSize?: number }) =>
    apiClient.get<import('@/types/stack.types').CommunityModuleListResult>('/modules/community', { params }),

  importCommunity: (repository: string) =>
    apiClient.post<import('@/types/stack.types').ModuleDto>('/modules/community/import', { repository }),
}

// Server-type catalog API (selectable variants in the stack wizard)
export const serverTypeApi = {
  list: () =>
    apiClient.get<import('@/types/stack.types').ServerTypeInfoDto[]>('/server-types'),
  // Remote branches for a custom-fork repository URL (git ls-remote on the backend).
  branches: (repositoryUrl: string) =>
    apiClient.get<string[]>('/server-types/branches', { params: { repositoryUrl } }),
}

// Validation API
export const validationApi = {
  validate: (config: StackConfigurationDto, existingStackId?: string) =>
    apiClient.post<ValidationResultDto>(`/stacks/validate${existingStackId ? `?existingStackId=${existingStackId}` : ''}`, config),
}

// System / host helper API (LAN IP suggestion, remote connection test)
export const systemApi = {
  network: () =>
    apiClient.get<import('@/types/stack.types').NetworkInfoDto>('/system/network'),
  testRemoteConnection: (
    deployment: import('@/types/stack.types').DeploymentConfigDto,
    phase?: import('@/types/stack.types').RemoteConnectionTestPhase
  ) =>
    apiClient.post<import('@/types/stack.types').RemoteConnectionTestResultDto>(
      '/system/test-remote-connection',
      { deployment, phase: phase ?? 0 }
    ),
  provisionRemoteHost: (request: import('@/types/stack.types').RemoteProvisionRequestDto) =>
    apiClient.post<import('@/types/stack.types').RemoteSetupResultDto>(
      '/system/provision-remote-host',
      request
    ),
  vpcSecurityRoles: () =>
    apiClient.get<import('@/types/stack.types').VpcSecurityCatalogDto>('/system/vpc-security-roles'),
  vpcSecurityProfile: (params: {
    host: string
    authPort?: number
    worldPort?: number
    armoryPort?: number
    clientPort?: number
    databasePort?: number
    soapPort?: number
    sshPort?: number
  }) =>
    apiClient.get<import('@/types/stack.types').VpcSecurityProfileDto>('/system/vpc-security-profile', {
      params,
    }),
  vpcLaunchUserData: (sshUser?: string) =>
    apiClient.get<import('@/types/stack.types').VpcLaunchUserDataDto>('/system/vpc-launch-user-data', {
      params: sshUser ? { sshUser } : undefined,
    }),
}


// Migrations / Patches API
export const patchApi = {
  overview: (stackId: string) =>
    apiClient.get<import('@/types/patch.types').MigrationOverviewDto>(
      `/stacks/${stackId}/migrations`
    ),

  detail: (stackId: string, patchKey: string) =>
    apiClient.get<import('@/types/patch.types').PatchDetailsDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}`
    ),

  configOverridesPreview: (stackId: string, patchKey: string) =>
    apiClient.get<import('@/types/patch.types').PatchConfigOverrideDto[]>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/config-overrides-preview`
    ),

  newsPreview: (stackId: string, patchKey: string) =>
    apiClient.get<import('@/types/patch.types').PatchNewsPreviewDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/news-preview`
    ),

  saveNews: (
    stackId: string,
    patchKey: string,
    request: import('@/types/patch.types').SavePatchNewsRequest
  ) =>
    apiClient.put<import('@/types/patch.types').PatchDetailsDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/news`,
      request
    ),

  uploadNewsCover: (stackId: string, patchKey: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/patch.types').PatchDetailsDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/news/cover`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  saveLauncherTheme: (stackId: string, patchKey: string, theme: string) =>
    apiClient.put<import('@/types/patch.types').PatchDetailsDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/launcher-theme`,
      { theme }
    ),

  saveDescription: (stackId: string, patchKey: string, content: string) =>
    apiClient.put<import('@/types/patch.types').PatchDetailsDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/description`,
      { content }
    ),

  create: (stackId: string, request: import('@/types/patch.types').CreatePatchRequest) =>
    apiClient.post<import('@/types/patch.types').PatchSummaryDto>(
      `/stacks/${stackId}/migrations`,
      request
    ),

  importCollection: (
    stackId: string,
    file: File,
    mode: import('@/types/patch.types').ImportPatchCollectionMode,
    onProgress?: (percent: number) => void
  ) => {
    const form = new FormData()
    form.append('file', file)
    form.append('mode', mode)
    return apiClient.post<import('@/types/patch.types').ImportPatchCollectionResultDto>(
      `/stacks/${stackId}/migrations/import`,
      form,
      {
        headers: { 'Content-Type': 'multipart/form-data' },
        onUploadProgress: (e) => {
          if (onProgress && e.total) {
            onProgress(Math.round((e.loaded / e.total) * 100))
          }
        },
      }
    )
  },

  initBaseline: (stackId: string) =>
    apiClient.post<{ success: boolean }>(`/stacks/${stackId}/migrations/init-baseline`),

  apply: (stackId: string, patchKey: string) =>
    apiClient.post<import('@/types/patch.types').ApplyStatusDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/apply`
    ),

  reapplyAll: (stackId: string) =>
    apiClient.post<import('@/types/patch.types').ApplyStatusDto>(
      `/stacks/${stackId}/migrations/reapply-sql`
    ),

  applyStatus: (stackId: string) =>
    apiClient.get<import('@/types/patch.types').ApplyStatusDto>(
      `/stacks/${stackId}/migrations/apply/status`
    ),

  browseFiles: (stackId: string, path?: string) =>
    apiClient.get<import('@/types/client.types').ClientBrowseResultDto>(
      `/stacks/${stackId}/migrations/browse`,
      { params: path ? { path } : undefined }
    ),

  deleteEntry: (stackId: string, path: string) =>
    apiClient.delete<{ success: boolean }>(`/stacks/${stackId}/migrations/browse/entry`, {
      params: { path },
    }),

  dropAllPatches: (stackId: string) =>
    apiClient.delete<{ success: boolean; deletedCount: number }>(
      `/stacks/${stackId}/migrations/patches`
    ),

  downloadApplyLog: (stackId: string, runId?: string | null) =>
    apiClient.get<Blob>(
      `/stacks/${stackId}/migrations/apply/log${runId ? `/${encodeURIComponent(runId)}` : ''}`,
      { responseType: 'blob' }
    ),

  downloadPatchTemplate: (stackId: string) =>
    apiClient.get<Blob>(`/stacks/${stackId}/migrations/patch-template`, { responseType: 'blob' }),

  upload: (
    stackId: string,
    patchKey: string,
    category: string,
    files: FileList | File[],
    description?: string
  ) => {
    const form = new FormData()
    Array.from(files).forEach((file) => form.append('files', file))
    if (description) form.append('description', description)
    return apiClient.post<import('@/types/patch.types').PatchFileDto[]>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/files/${category}`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  // Upload where each file carries a relative path (optionally one container sub-folder).
  // Works for container categories: dbc, map, sql/world, sql/auth, sql/characters.
  uploadContainer: (
    stackId: string,
    patchKey: string,
    category: string,
    items: { file: File; path: string }[]
  ) => {
    const form = new FormData()
    items.forEach(({ file, path }) => {
      form.append('files', file)
      form.append('paths', path)
    })
    return apiClient.post<import('@/types/patch.types').PatchFileDto[]>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/files/${category}`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  readDbc: (stackId: string, patchKey: string, fileName: string) =>
    apiClient.get<import('@/types/patch.types').DbcContentDto>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/dbc/${encodePathSegments(fileName)}`
    ),

  saveDbc: (stackId: string, patchKey: string, fileName: string, content: string) =>
    apiClient.put<{ success: boolean }>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/dbc/${encodePathSegments(fileName)}`,
      { fileName, content }
    ),

  deleteFile: (stackId: string, patchKey: string, category: string, fileName: string) =>
    apiClient.delete<{ success: boolean }>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/files/${category}/${encodePathSegments(fileName)}`
    ),

  // MPQ files currently published to the stack's client overlay (created by earlier patches).
  publishedMpqs: (stackId: string) =>
    apiClient.get<import('@/types/patch.types').PublishedMpqDto[]>(
      `/stacks/${stackId}/migrations/published-mpqs`
    ),

  // Sets which published MPQ files this patch removes from the overlay on apply.
  setMpqRemovals: (stackId: string, patchKey: string, fileNames: string[]) =>
    apiClient.put<{ success: boolean }>(
      `/stacks/${stackId}/migrations/${encodeURIComponent(patchKey)}/mpq-removals`,
      { fileNames }
    ),

  bootstrapIndividualProgression: (stackId: string) =>
    apiClient.post<import('@/types/individual-progression.types').IndividualProgressionBootstrapResult>(
      `/stacks/${stackId}/migrations/individual-progression/bootstrap`
    ),

  validatePatches: (stackId: string) =>
    apiClient.post<import('@/types/individual-progression.types').IndividualProgressionValidationResult>(
      `/stacks/${stackId}/migrations/validate-patches`
    ),

  validateIndividualProgressionPatches: (stackId: string) =>
    apiClient.post<import('@/types/individual-progression.types').IndividualProgressionValidationResult>(
      `/stacks/${stackId}/migrations/individual-progression/validate-patches`
    ),

  // ===== Progression Sync =====

  progressionSyncStatus: (stackId: string) =>
    apiClient.get<import('@/types/individual-progression.types').ProgressionSyncStatus>(
      `/stacks/${stackId}/migrations/individual-progression/sync/status`
    ),

  runProgressionSync: (stackId: string) =>
    apiClient.post<import('@/types/individual-progression.types').ProgressionSyncResult>(
      `/stacks/${stackId}/migrations/individual-progression/sync/run`
    ),

  resolveProgressionOptionalFiles: (
    stackId: string,
    decisions: Record<string, boolean>
  ) =>
    apiClient.post<import('@/types/individual-progression.types').ProgressionSyncResult>(
      `/stacks/${stackId}/migrations/individual-progression/sync/resolve-optional`,
      { decisions }
    ),

  getProgressionIgnoredFiles: (stackId: string) =>
    apiClient.get<import('@/types/individual-progression.types').ProgressionIgnoredFile[]>(
      `/stacks/${stackId}/migrations/individual-progression/sync/ignored-files`
    ),

  repromptProgressionIgnoredFile: (stackId: string, source: string) =>
    apiClient.post<import('@/types/individual-progression.types').ProgressionSyncResult>(
      `/stacks/${stackId}/migrations/individual-progression/sync/reprompt`,
      null,
      { params: { source } }
    ),
}

// Encodes each path segment but preserves "/" so catch-all routes see real sub-folders.
function encodePathSegments(path: string): string {
  return path
    .split('/')
    .map((segment) => encodeURIComponent(segment))
    .join('/')
}

// Addons API (global client when stackId is undefined, otherwise a specific stack's client)
const addonBase = (stackId?: string) => (stackId ? `/stacks/${stackId}/addons` : '/addons')

export const addonApi = {
  list: (stackId?: string) =>
    apiClient.get<import('@/types/addon.types').AddonListDto>(addonBase(stackId)),

  upload: (stackId: string | undefined, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/addon.types').AddonListDto>(addonBase(stackId), form, {
      headers: { 'Content-Type': 'multipart/form-data' },
    })
  },

  remove: (stackId: string | undefined, name: string) =>
    apiClient.delete<import('@/types/addon.types').AddonListDto>(
      `${addonBase(stackId)}/${encodeURIComponent(name)}`
    ),

  catalog: (stackId?: string) =>
    apiClient.get<import('@/types/addon.types').AddonCatalogEntryDto[]>(
      `${addonBase(stackId)}/catalog`
    ),

  install: (stackId: string | undefined, addonId: string) =>
    apiClient.post<import('@/types/addon.types').AddonListDto>(
      `${addonBase(stackId)}/catalog/${encodeURIComponent(addonId)}/install`
    ),
}

// Lua scripts API (per-stack, served to the worldserver's Eluna engine)
export const luaApi = {
  list: (stackId: string) =>
    apiClient.get<import('@/types/server.types').LuaScriptListDto>(`/stacks/${stackId}/lua`),

  read: (stackId: string, path: string) =>
    apiClient.get<import('@/types/server.types').LuaScriptContentDto>(
      `/stacks/${stackId}/lua/content`,
      { params: { path } }
    ),

  save: (stackId: string, path: string, content: string) =>
    apiClient.put<import('@/types/server.types').LuaScriptListDto>(
      `/stacks/${stackId}/lua/content`,
      { path, content }
    ),

  upload: (stackId: string, file: File, path?: string) => {
    const form = new FormData()
    form.append('file', file)
    if (path) form.append('path', path)
    return apiClient.post<import('@/types/server.types').LuaScriptListDto>(
      `/stacks/${stackId}/lua/upload`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  remove: (stackId: string, path: string) =>
    apiClient.delete<import('@/types/server.types').LuaScriptListDto>(
      `/stacks/${stackId}/lua/content`,
      { params: { path } }
    ),

  apply: (stackId: string) =>
    apiClient.post<{ restarted: boolean }>(`/stacks/${stackId}/lua/apply`),
}

// Stack revisions API (point-in-time DB + config snapshots)
export const revisionApi = {
  list: (stackId: string) =>
    apiClient.get<import('@/types/server.types').RevisionDto[]>(`/stacks/${stackId}/revisions`),

  create: (stackId: string) =>
    apiClient.post<import('@/types/server.types').RevisionDto>(`/stacks/${stackId}/revisions`),

  restore: (stackId: string, revisionId: string) =>
    apiClient.post<{ restored: boolean }>(
      `/stacks/${stackId}/revisions/${encodeURIComponent(revisionId)}/restore`
    ),

  remove: (stackId: string, revisionId: string) =>
    apiClient.delete<{ deleted: boolean }>(
      `/stacks/${stackId}/revisions/${encodeURIComponent(revisionId)}`
    ),
}

// Server config files API (worldserver.conf, authserver.conf, module confs)
export const serverConfigApi = {
  list: (stackId: string) =>
    apiClient.get<import('@/types/server.types').ServerConfigListDto>(`/stacks/${stackId}/config`),

  read: (stackId: string, path: string) =>
    apiClient.get<import('@/types/server.types').ServerConfigContentDto>(
      `/stacks/${stackId}/config/content`,
      { params: { path } }
    ),

  save: (stackId: string, path: string, content: string) =>
    apiClient.put<import('@/types/server.types').ServerConfigListDto>(
      `/stacks/${stackId}/config/content`,
      { path, content }
    ),

  apply: (stackId: string) =>
    apiClient.post<{ restarted: boolean }>(`/stacks/${stackId}/config/apply`),
}

// Per-stack BASE client management (upload/inspect/re-seed the base WoW client that a stack's client
// container serves as its read-only base layer). Each stack has its own base.
export const clientApi = {
  getBaseInfo: (stackId: string) =>
    apiClient.get<import('@/types/client.types').ClientBaseInfoDto>(`/stacks/${stackId}/client`),

  uploadBase: (stackId: string, file: File, onProgress?: (percent: number) => void) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/client.types').ClientBaseInfoDto>(`/stacks/${stackId}/client/base`, form, {
      headers: { 'Content-Type': 'multipart/form-data' },
      onUploadProgress: (e) => {
        if (onProgress && e.total) {
          onProgress(Math.round((e.loaded / e.total) * 100))
        }
      },
    })
  },

  rescanBase: (stackId: string) =>
    apiClient.post<import('@/types/client.types').ClientBaseInfoDto>(`/stacks/${stackId}/client/rescan`),

  browse: (stackId: string, path?: string) =>
    apiClient.get<import('@/types/client.types').ClientBrowseResultDto>(`/stacks/${stackId}/client/browse`, {
      params: path ? { path } : undefined,
    }),

  deleteEntry: (stackId: string, path: string) =>
    apiClient.delete<import('@/types/client.types').ClientBaseInfoDto>(`/stacks/${stackId}/client/entry`, {
      params: { path },
    }),

  // Uploads a single file into a folder of the base client (drag & drop in the browser).
  uploadFile: (stackId: string, dir: string, file: File, onProgress?: (percent: number) => void) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/client.types').ClientBaseInfoDto>(`/stacks/${stackId}/client/file`, form, {
      params: dir ? { path: dir } : undefined,
      headers: { 'Content-Type': 'multipart/form-data' },
      onUploadProgress: (e) => {
        if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100))
      },
    })
  },
}

// Per-stack armory asset bundles: the 3D model-viewer dataset (armory.data.zip + armory.textures.zip)
// and the static web assets (armory.static.zip). Each stack has its own bundles.
export const armoryAssetsApi = {
  getInfo: (stackId: string) =>
    apiClient.get<import('@/types/armory.types').ArmoryAssetsInfoDto>(`/stacks/${stackId}/armory-assets`),

  getStylingDefaults: (stackId: string) =>
    apiClient.get<Record<string, import('@/types/armory.types').ArmoryStylingDto>>(
      `/stacks/${stackId}/armory-assets/styling/defaults`,
    ),

  getStyling: (stackId: string) =>
    apiClient.get<import('@/types/armory.types').ArmoryStylingDto>(`/stacks/${stackId}/armory-assets/styling`),

  saveStyling: (stackId: string, styling: import('@/types/armory.types').ArmoryStylingDto) =>
    apiClient.put<import('@/types/armory.types').ArmoryStylingDto>(
      `/stacks/${stackId}/armory-assets/styling`,
      styling,
    ),

  getPageTemplate: (stackId: string, pageId: string, templateId: string) =>
    apiClient.get<import('@/types/armory.types').ArmoryPageLayoutDto>(
      `/stacks/${stackId}/armory-assets/layout/template`,
      { params: { pageId, templateId } },
    ),

  getLayout: (stackId: string) =>
    apiClient.get<import('@/types/armory.types').ArmoryLayoutDto>(`/stacks/${stackId}/armory-assets/layout`),

  saveLayout: (stackId: string, layout: import('@/types/armory.types').ArmoryLayoutDto) =>
    apiClient.put<import('@/types/armory.types').ArmoryLayoutDto>(`/stacks/${stackId}/armory-assets/layout`, layout),

  uploadWallpaper: (stackId: string, file: File, onProgress?: (percent: number) => void) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/armory.types').ArmoryStylingDto>(
      `/stacks/${stackId}/armory-assets/styling/wallpaper`,
      form,
      {
        onUploadProgress: (e) => {
          if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100))
        },
      },
    )
  },

  uploadFavicon: (stackId: string, file: File, onProgress?: (percent: number) => void) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/armory.types').ArmoryAssetsInfoDto>(
      `/stacks/${stackId}/armory-assets/favicon`,
      form,
      {
        onUploadProgress: (e) => {
          if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100))
        },
      },
    )
  },

  deleteFavicon: (stackId: string) =>
    apiClient.delete<import('@/types/armory.types').ArmoryAssetsInfoDto>(`/stacks/${stackId}/armory-assets/favicon`),

  faviconPreviewUrl: (stackId: string) => `/api/stacks/${stackId}/armory-assets/favicon`,

  uploadData: (stackId: string, file: File, onProgress?: (percent: number) => void) =>
    uploadArmoryAsset(`/stacks/${stackId}/armory-assets/data`, file, onProgress),

  uploadStatic: (stackId: string, file: File, onProgress?: (percent: number) => void) =>
    uploadArmoryAsset(`/stacks/${stackId}/armory-assets/static`, file, onProgress),

  deleteStatic: (stackId: string) =>
    apiClient.delete<import('@/types/armory.types').ArmoryAssetsInfoDto>(`/stacks/${stackId}/armory-assets/static`),

  // Rebuilds the armory image (baking static assets) + restarts the armory as a detached background
  // job, returning the initial job status. Progress is tracked via the armory job status / SignalR.
  rebuildImage: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').ArmoryJobStatus>(`/stacks/${stackId}/armory-assets/rebuild-image`),

  // Extracts the stack's server DBCs, converts them for the armory, rebuilds the image + restarts, as a
  // detached background job. Returns the initial job status (tracked via armory job status / SignalR).
  syncDbcs: (stackId: string) =>
    apiClient.post<import('@/types/stack.types').ArmoryJobStatus>(`/stacks/${stackId}/armory-assets/sync-dbcs`),

  /** Downloads armory.data.zip, armory.textures.zip, and armory.static.zip from GitHub and applies them. */
  downloadRelease: (stackId: string) =>
    apiClient.post<import('@/types/armory.types').ArmoryReleaseDownloadResultDto>(
      `/stacks/${stackId}/armory-assets/download-release`,
      null,
      { timeout: 3_600_000 },
    ),

  browseData: (stackId: string, path?: string) =>
    apiClient.get<import('@/types/client.types').ClientBrowseResultDto>(`/stacks/${stackId}/armory-assets/data/browse`, {
      params: path ? { path } : undefined,
    }),

  deleteData: (stackId: string, path: string) =>
    apiClient.delete<import('@/types/armory.types').ArmoryAssetsInfoDto>(`/stacks/${stackId}/armory-assets/data/entry`, {
      params: { path },
    }),

  // Uploads a single file into a folder of the model-viewer dataset (drag & drop in the browser).
  uploadDataFile: (stackId: string, dir: string, file: File, onProgress?: (percent: number) => void) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/armory.types').ArmoryAssetsInfoDto>(`/stacks/${stackId}/armory-assets/data/file`, form, {
      params: dir ? { path: dir } : undefined,
      headers: { 'Content-Type': 'multipart/form-data' },
      onUploadProgress: (e) => {
        if (onProgress && e.total) onProgress(Math.round((e.loaded / e.total) * 100))
      },
    })
  },
}

function uploadArmoryAsset(url: string, file: File, onProgress?: (percent: number) => void) {
  const form = new FormData()
  form.append('file', file)
  return apiClient.post<import('@/types/armory.types').ArmoryAssetsInfoDto>(url, form, {
    headers: { 'Content-Type': 'multipart/form-data' },
    onUploadProgress: (e) => {
      if (onProgress && e.total) {
        onProgress(Math.round((e.loaded / e.total) * 100))
      }
    },
  })
}

// Launcher distribution admin API (global config, assets, compile, per-stack profiles)
export const launcherApi = {
  getConfig: () =>
    apiClient.get<import('@/types/launcher.types').LauncherDistributionConfigDto>('/launcher-admin/config'),

  saveConfig: (config: import('@/types/launcher.types').LauncherDistributionConfigDto) =>
    apiClient.put<import('@/types/launcher.types').LauncherDistributionConfigDto>(
      '/launcher-admin/config',
      config
    ),

  getTemplates: () =>
    apiClient.get<import('@/types/launcher.types').LauncherTemplateDto[]>('/launcher-admin/templates'),

  uploadAsset: (kind: 'background' | 'logo' | 'icon', file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/launcher.types').LauncherDistributionConfigDto>(
      `/launcher-admin/assets/${kind}`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  // Build pipeline. The launcher is compiled once on the manager's local engine, then pushed to every
  // launcher-visible, client-enabled stack (like news distribution) so each stack serves it itself.
  build: (part: import('@/types/launcher.types').LauncherVersionPart) =>
    apiClient.post<import('@/types/launcher.types').LauncherBuildStatusDto>('/launcher-build', { part }),

  buildStatus: () =>
    apiClient.get<import('@/types/launcher.types').LauncherBuildStatusDto>('/launcher-build/status'),

  downloadUrl: () => '/api/launcher-build/download',

  // Pings every client-enabled stack for the launcher version it serves, vs the manager's built version,
  // so the admin can verify propagation. Re-send re-pushes the current build to a single stale stack.
  stackVersions: () =>
    apiClient.get<import('@/types/launcher.types').LauncherPropagationDto>('/launcher-build/stack-versions'),

  resendToStack: (stackId: string) =>
    apiClient.post<import('@/types/launcher.types').LauncherStackVersionDto>(
      `/launcher-build/stacks/${stackId}/resend`
    ),

  // Per-stack profile
  getProfile: (stackId: string) =>
    apiClient.get<import('@/types/launcher.types').LauncherProfileConfigDto>(
      `/launcher-admin/stacks/${stackId}/profile`
    ),

  saveProfile: (
    stackId: string,
    profile: import('@/types/launcher.types').LauncherProfileConfigDto
  ) =>
    apiClient.put<import('@/types/launcher.types').LauncherProfileConfigDto>(
      `/launcher-admin/stacks/${stackId}/profile`,
      profile
    ),

  uploadProfileAsset: (stackId: string, kind: 'background' | 'logo', file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/launcher.types').LauncherProfileConfigDto>(
      `/launcher-admin/stacks/${stackId}/profile/assets/${kind}`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  // Removes a stack's uploaded wallpaper/logo override so the launcher falls back to the global theme.
  deleteProfileAsset: (stackId: string, kind: 'background' | 'logo') =>
    apiClient.delete<import('@/types/launcher.types').LauncherProfileConfigDto>(
      `/launcher-admin/stacks/${stackId}/profile/assets/${kind}`
    ),

  /**
   * Rescans this stack's client distribution, rebuilding the manifest with the current realmlist
   * host/port so a changed realmlist propagates to players (Config.wtf) and the version bumps.
   */
  rescanStackClient: (stackId: string) =>
    apiClient.post(`/stacks/${stackId}/launcher/rescan`),

  /**
   * Forces every launcher pointed at this stack to full-verify (re-hash) all client files on its next
   * check by bumping the manifest's verify token. Use when a same-size file edit (e.g. Config.wtf)
   * wouldn't otherwise be picked up by the launcher's quick size-only check.
   */
  forceVerifyStackClient: (stackId: string) =>
    apiClient.post(`/stacks/${stackId}/launcher/force-verify`),

  /** Re-hash every file, rebuild the manifest, and queue a full launcher sync. */
  rebuildStackClientManifest: (stackId: string) =>
    apiClient.post<import('@/types/client.types').ClientManifestRebuildResultDto>(
      `/stacks/${stackId}/launcher/rebuild-manifest`
    ),

  /** Reads the editable WTF/Config.wtf settings template for a stack (placeholders intact). */
  getStackConfigTemplate: (stackId: string) =>
    apiClient.get<{ content: string }>(`/stacks/${stackId}/launcher/config-template`),

  /** Overwrites the WTF/Config.wtf settings template for a stack. */
  saveStackConfigTemplate: (stackId: string, content: string) =>
    apiClient.put(`/stacks/${stackId}/launcher/config-template`, { content }),

  // ===== News (rich articles: cover image + headline + HTML body) =====
  getGlobalNews: () =>
    apiClient.get<import('@/types/launcher.types').LauncherNewsItemDto[]>('/launcher-admin/news'),

  saveGlobalNews: (items: import('@/types/launcher.types').LauncherNewsItemDto[]) =>
    apiClient.put<import('@/types/launcher.types').LauncherNewsItemDto[]>('/launcher-admin/news', items),

  uploadGlobalNewsImage: (itemId: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/launcher.types').LauncherNewsItemDto[]>(
      `/launcher-admin/news/${itemId}/image`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },

  getStackNews: (stackId: string) =>
    apiClient.get<import('@/types/launcher.types').LauncherNewsItemDto[]>(
      `/launcher-admin/stacks/${stackId}/news`
    ),

  saveStackNews: (stackId: string, items: import('@/types/launcher.types').LauncherNewsItemDto[]) =>
    apiClient.put<import('@/types/launcher.types').LauncherNewsItemDto[]>(
      `/launcher-admin/stacks/${stackId}/news`,
      items
    ),

  uploadStackNewsImage: (stackId: string, itemId: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post<import('@/types/launcher.types').LauncherNewsItemDto[]>(
      `/launcher-admin/stacks/${stackId}/news/${itemId}/image`,
      form,
      { headers: { 'Content-Type': 'multipart/form-data' } }
    )
  },
}
