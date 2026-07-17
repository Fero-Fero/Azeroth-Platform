import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Loader2, Plus, Play, CheckCircle2, Lock, ArrowRight, Database, AlertTriangle, RefreshCw, Download, Clock, Upload, FolderOpen, Save, ChevronDown, TrendingUp, ShieldCheck, Trash2 } from 'lucide-react'
import {
  usePatchOverview,
  usePatchDetail,
  useCreatePatch,
  useImportPatchCollection,
  useInitBaseline,
  useApplyPatch,
  useReapplyAllPatches,
  useApplyStatus,
  useUploadPatchFiles,
  useUploadContainerFiles,
  useDeletePatchFile,
  useDeletePatchEntry,
  useDropAllPatches,
  downloadApplyLog,
  downloadPatchTemplate,
  useSavePatchDescription,
  useBootstrapIndividualProgression,
  useValidateIndividualProgressionPatches,
  patchKeys,
} from '@/hooks/usePatches'
import { stackKeys } from '@/hooks/useStacks'
import type { PatchStatus, PatchFileDto, Expansion, ImportPatchCollectionMode, PatchKind, PatchSummaryDto } from '@/types/patch.types'
import PatchFileCategory from './PatchFileCategory'
import PatchConfigOverridesPreview, {
  PatchConfigOverridesPreviewButton,
} from './PatchConfigOverridesPreview'
import PatchNewsPreview from './PatchNewsPreview'
import PatchNewsEditor from './PatchNewsEditor'
import PatchLauncherThemePanel, { PatchNewsFilesPanel } from './PatchLauncherThemePanel'
import ContainerFileCategory from './ContainerFileCategory'
import DbcEditorDialog from './DbcEditorDialog'
import MpqRemovalPanel from './MpqRemovalPanel'
import MpqManifestPanel from './MpqManifestPanel'
import PatchesFolderBrowser from './PatchesFolderBrowser'
import ProgressionSyncPanel from './ProgressionSyncPanel'
import type { IndividualProgressionValidationResult } from '@/types/individual-progression.types'
import { useLauncherConfig, useLauncherTemplates } from '@/hooks/useLauncher'

interface PatchesTabProps {
  stackId: string
}

// Expansion roots must match MigrationLayout.ExpansionRoots on the backend.
const EXPANSION_ROOT: Record<Expansion, number> = {
  classic: 1,
  tbc: 2,
  wotlk: 3,
  custom: 4,
}

const EXPANSIONS: { id: Expansion; label: string }[] = [
  { id: 'classic', label: 'Classic' },
  { id: 'tbc', label: 'The Burning Crusade' },
  { id: 'wotlk', label: 'Wrath of the Lich King' },
  { id: 'custom', label: 'Custom' },
]

type PatchCategory = Expansion

const PATCH_CATEGORIES: { id: PatchCategory; label: string; hint: string }[] = [
  { id: 'classic', label: 'Classic', hint: 'Index root 1' },
  { id: 'tbc', label: 'TBC', hint: 'Index root 2' },
  { id: 'wotlk', label: 'WotLK', hint: 'Index root 3' },
  { id: 'custom', label: 'Custom', hint: 'Index root 4' },
]

const CATEGORY_STYLE: Record<
  PatchCategory,
  { header: string; chip: string; empty: string }
> = {
  classic: {
    header: 'bg-amber-50/90 hover:bg-amber-100/80 border-amber-100',
    chip: 'bg-amber-100 text-amber-800',
    empty: 'text-amber-700/60',
  },
  tbc: {
    header: 'bg-emerald-50/90 hover:bg-emerald-100/80 border-emerald-100',
    chip: 'bg-emerald-100 text-emerald-800',
    empty: 'text-emerald-700/60',
  },
  wotlk: {
    header: 'bg-sky-50/90 hover:bg-sky-100/80 border-sky-100',
    chip: 'bg-sky-100 text-sky-800',
    empty: 'text-sky-700/60',
  },
  custom: {
    header: 'bg-violet-50/90 hover:bg-violet-100/80 border-violet-100',
    chip: 'bg-violet-100 text-violet-800',
    empty: 'text-violet-700/60',
  },
}

const STATUS_ROW_ACCENT: Record<PatchStatus, string> = {
  Applied: 'border-l-green-500',
  Next: 'border-l-blue-500',
  Locked: 'border-l-gray-200',
}

const PATCH_KINDS: { id: PatchKind; label: string; hint: string }[] = [
  { id: 'expansion', label: 'Expansion', hint: 'Expansion entry point (1.0, 2.0, 3.0, or 4.0)' },
  { id: 'patch', label: 'Patch', hint: 'Release index (1.1, 2.3, …)' },
  { id: 'hotfix', label: 'Hotfix', hint: 'Sub-release index (1.1.1, 1.2.3, …)' },
]

function parsePatchIndex(index: string): number[] {
  return index.split('.').map((part) => Number(part))
}

function comparePatchIndex(a: number[], b: number[]): number {
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const av = a[i] ?? 0
    const bv = b[i] ?? 0
    if (av !== bv) return av - bv
  }
  return 0
}

function hasExpansionEntryPoint(index: string): boolean {
  const parts = parsePatchIndex(index)
  return parts.length === 1 || (parts.length >= 2 && parts[1] === 0 && (parts[2] ?? 0) === 0)
}

function nextPatchIndex(
  expansion: Expansion,
  patches: { index: string }[],
  kind: PatchKind,
  parentIndex?: string
): string {
  const root = EXPANSION_ROOT[expansion]
  const indices = patches
    .filter((p) => parsePatchIndex(p.index)[0] === root)
    .map((p) => parsePatchIndex(p.index))

  if (kind === 'expansion') {
    return `${root}.0`
  }

  if (kind === 'patch') {
    const maxSub1 = indices
      .filter((parts) => parts.length >= 2)
      .map((parts) => parts[1])
      .reduce((max, value) => Math.max(max, value), 0)
    return `${root}.${maxSub1 + 1}`
  }

  const parentParts = parentIndex ? parsePatchIndex(parentIndex) : null
  const parentSub1 =
    parentParts && parentParts.length >= 2
      ? parentParts[1]
      : indices
          .filter((parts) => parts.length >= 2)
          .map((parts) => parts[1])
          .reduce((max, value) => Math.max(max, value), 1)

  const maxSub2 = indices
    .filter((parts) => parts.length >= 3 && parts[1] === parentSub1)
    .map((parts) => parts[2])
    .reduce((max, value) => Math.max(max, value), 0)
  return `${root}.${parentSub1}.${maxSub2 + 1}`
}

function formatPatchFolder(index: string, name?: string): string {
  const trimmed = name?.trim()
  return trimmed ? `patch ${index} ${trimmed}` : `patch ${index}`
}

function patchCategory(index: string): PatchCategory {
  const root = parsePatchIndex(index)[0]
  if (root === 1) return 'classic'
  if (root === 2) return 'tbc'
  if (root === 3) return 'wotlk'
  return 'custom'
}

const IMPORT_ARCHIVE_EXTENSIONS = [
  '.zip',
  '.rar',
  '.7z',
  '.tar',
  '.tar.gz',
  '.tgz',
  '.tar.bz2',
  '.tbz2',
  '.tar.xz',
  '.gz',
  '.bz2',
  '.xz',
] as const
const IMPORT_ARCHIVE_ACCEPT = IMPORT_ARCHIVE_EXTENSIONS.join(',')


function isImportArchive(file: File): boolean {
  const lower = file.name.toLowerCase()
  return IMPORT_ARCHIVE_EXTENSIONS.some((ext) => lower.endsWith(ext))
}

/** Primary label for a patch row — uses the name from the patch folder (repo label after index). */
function patchRowTitle(patch: PatchSummaryDto): string {
  if (patch.name) {
    return patch.name
  }
  if (patch.progressionTitle) return patch.progressionTitle
  return patch.key
}

const DROP_ALL_PATCHES_CONFIRMATION = 'i am sure'

const STATUS_BADGE: Record<PatchStatus, { label: string; className: string; icon: ReactNode }> = {
  Applied: {
    label: 'Applied',
    className: 'bg-green-100 text-green-700',
    icon: <CheckCircle2 className="w-3.5 h-3.5" />,
  },
  Next: {
    label: 'Next',
    className: 'bg-blue-100 text-blue-700',
    icon: <ArrowRight className="w-3.5 h-3.5" />,
  },
  Locked: {
    label: 'Locked',
    className: 'bg-gray-100 text-gray-500',
    icon: <Lock className="w-3.5 h-3.5" />,
  },
}

export default function PatchesTab({ stackId }: PatchesTabProps) {
  const queryClient = useQueryClient()
  const { data: overview, isLoading, error } = usePatchOverview(stackId)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const { data: detail } = usePatchDetail(stackId, selectedKey)
  const { data: launcherConfig } = useLauncherConfig()
  const { data: launcherTemplates } = useLauncherTemplates()

  const newsPreviewAccent = useMemo(() => {
    const themeId = detail?.launcherTheme || launcherConfig?.template || 'wotlk'
    return launcherTemplates?.find((template) => template.id === themeId)?.accentColor ?? '#4fa8d8'
  }, [detail?.launcherTheme, launcherConfig?.template, launcherTemplates])

  const createMutation = useCreatePatch(stackId)
  const importMutation = useImportPatchCollection(stackId)
  const baselineMutation = useInitBaseline(stackId)
  const applyMutation = useApplyPatch(stackId)
  const reapplyMutation = useReapplyAllPatches(stackId)
  const uploadMutation = useUploadPatchFiles(stackId)
  const uploadContainerMutation = useUploadContainerFiles(stackId)
  const deleteMutation = useDeletePatchFile(stackId)
  const deletePatchMutation = useDeletePatchEntry(stackId)
  const dropAllPatchesMutation = useDropAllPatches(stackId)
  const saveDescriptionMutation = useSavePatchDescription(stackId)
  const bootstrapMutation = useBootstrapIndividualProgression(stackId)
  const validateMutation = useValidateIndividualProgressionPatches(stackId)
  const hasIpModule = overview?.hasIndividualProgressionModule ?? false
  const ipBootstrapped = hasIpModule && (overview?.individualProgressionBootstrapped ?? false)

  const [showCreate, setShowCreate] = useState(false)
  const [showImport, setShowImport] = useState(false)
  const [showBrowser, setShowBrowser] = useState(false)
  const [confirmReapply, setConfirmReapply] = useState(false)
  const [confirmApply, setConfirmApply] = useState(false)
  const [confirmDropAll, setConfirmDropAll] = useState(false)
  const [dropAllConfirmText, setDropAllConfirmText] = useState('')
  const [newExpansion, setNewExpansion] = useState<Expansion>('classic')
  const [newKind, setNewKind] = useState<PatchKind>('patch')
  const [newParentIndex, setNewParentIndex] = useState('')
  const [newName, setNewName] = useState('')
  const [importMode, setImportMode] = useState<ImportPatchCollectionMode>('merge')
  const [importFile, setImportFile] = useState<File | null>(null)
  const [importSummary, setImportSummary] = useState<string | null>(null)
  const [validationResult, setValidationResult] = useState<IndividualProgressionValidationResult | null>(null)
  const [downloadingTemplate, setDownloadingTemplate] = useState(false)
  const [importDragActive, setImportDragActive] = useState(false)
  const [importUploadPercent, setImportUploadPercent] = useState<number | null>(null)
  const importFileRef = useRef<HTMLInputElement>(null)
  const [uploadingCategory, setUploadingCategory] = useState<string | null>(null)
  const [editFile, setEditFile] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  // Upload errors are shown inline next to the failing section's upload field (not the top banner),
  // so they're visible without scrolling.
  const [uploadError, setUploadError] = useState<{ category: string; message: string } | null>(null)
  const [showConfigOverridesPreview, setShowConfigOverridesPreview] = useState(false)
  const [showPatchNewsPreview, setShowPatchNewsPreview] = useState(false)
  const [patchDetailTab, setPatchDetailTab] = useState<'description' | 'files' | 'news'>('files')
  const [descriptionDraft, setDescriptionDraft] = useState<string | null>(null)
  const [descriptionSaved, setDescriptionSaved] = useState(false)
  const [descriptionSaveError, setDescriptionSaveError] = useState<string | null>(null)
  const [collapsedCategories, setCollapsedCategories] = useState<Set<PatchCategory>>(new Set())

  // Poll live status whenever the DB lock is held (covers runs started by another operator/machine)
  // or we just started a run locally and are waiting for the first status to arrive.
  const overviewApplying = overview?.isApplying ?? false
  const [pollRequested, setPollRequested] = useState(false)
  const pollActive = overviewApplying || pollRequested
  const { data: applyStatus } = useApplyStatus(stackId, pollActive)
  const isApplying = overviewApplying || (applyStatus?.isApplying ?? false)

  // When a run finishes, stop polling and refresh the overview + patch detail so levels/status update.
  const wasApplying = useRef(false)
  useEffect(() => {
    const now = applyStatus?.isApplying ?? false
    if (wasApplying.current && !now) {
      setPollRequested(false)
      queryClient.invalidateQueries({ queryKey: patchKeys.overview(stackId) })
      queryClient.invalidateQueries({ queryKey: patchKeys.all })
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId) })
    }
    wasApplying.current = now
  }, [applyStatus?.isApplying, queryClient, stackId])

  // While an apply/reapply is in flight, warn before the tab is closed/reloaded. The server run
  // continues regardless (it's an uncancellable background job), but this avoids the operator losing
  // the live progress view mid-run by accident.
  useEffect(() => {
    if (!isApplying) return
    const handler = (e: BeforeUnloadEvent) => {
      e.preventDefault()
      e.returnValue = ''
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [isApplying])

  // Auto-scroll the live log to the bottom as new lines stream in.
  const logRef = useRef<HTMLPreElement>(null)
  useEffect(() => {
    const el = logRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [applyStatus?.log, applyStatus?.error])

  const selectedSummary = useMemo(
    () => overview?.patches.find((p) => p.key === selectedKey) ?? null,
    [overview, selectedKey]
  )

  const descriptionValue =
    descriptionDraft ?? detail?.description ?? selectedSummary?.description ?? ''
  const descriptionDirty =
    descriptionDraft !== null &&
    descriptionDraft !== (detail?.description ?? selectedSummary?.description ?? '')

  useEffect(() => {
    setDescriptionDraft(null)
    setDescriptionSaved(false)
    setDescriptionSaveError(null)
  }, [selectedKey])

  useEffect(() => {
    if (!selectedKey || !overview?.patches) return
    const patch = overview.patches.find((p) => p.key === selectedKey)
    if (!patch) return
    const category = patchCategory(patch.index)
    setCollapsedCategories((prev) => {
      if (!prev.has(category)) return prev
      const next = new Set(prev)
      next.delete(category)
      return next
    })
  }, [selectedKey, overview?.patches])

  const configOverrides = detail?.configOverrides ?? []

  const filesByCategory = useMemo(() => {
    const map: Record<string, PatchFileDto[]> = {}
    for (const file of detail?.files ?? []) {
      ;(map[file.category] ??= []).push(file)
    }
    return map
  }, [detail])

  const newsFiles = useMemo(
    () =>
      (detail?.files ?? [])
        .filter((file) => file.category === 'news')
        .map((file) => ({ name: file.name, size: file.size })),
    [detail?.files]
  )

  const sectionCollapseKey = (category: string) =>
    selectedKey ? `patch-section:${stackId}:${selectedKey}:${category}` : undefined

  const patchReleaseOptions = useMemo(() => {
    const root = EXPANSION_ROOT[newExpansion]
    return (overview?.patches ?? [])
      .filter((patch) => {
        const parts = parsePatchIndex(patch.index)
        return parts[0] === root && parts.length >= 2
      })
      .map((patch) => patch.index)
      .sort((a, b) => comparePatchIndex(parsePatchIndex(a), parsePatchIndex(b)))
  }, [newExpansion, overview?.patches])

  const expansionPatchExists = useMemo(() => {
    const root = EXPANSION_ROOT[newExpansion]
    return (overview?.patches ?? []).some(
      (patch) => parsePatchIndex(patch.index)[0] === root && hasExpansionEntryPoint(patch.index)
    )
  }, [newExpansion, overview?.patches])

  const previewIndex = nextPatchIndex(
    newExpansion,
    overview?.patches ?? [],
    newKind,
    newParentIndex || undefined
  )
  const previewKey = formatPatchFolder(previewIndex, newName)
  const canCreate =
    newKind !== 'expansion' || !expansionPatchExists
  const hasAppliedPatches =
    (overview?.currentLevel ?? 0) > 0 || (overview?.patches ?? []).some((patch) => patch.status === 'Applied')
  const effectiveImportMode: ImportPatchCollectionMode =
    hasAppliedPatches && importMode === 'override' ? 'merge' : importMode

  const patchesByCategory = useMemo(() => {
    const grouped: Record<PatchCategory, PatchSummaryDto[]> = {
      classic: [],
      tbc: [],
      wotlk: [],
      custom: [],
    }

    for (const patch of overview?.patches ?? []) {
      grouped[patchCategory(patch.index)].push(patch)
    }

    for (const category of PATCH_CATEGORIES) {
      grouped[category.id].sort((a, b) =>
        comparePatchIndex(parsePatchIndex(a.index), parsePatchIndex(b.index))
      )
    }

    return grouped
  }, [overview?.patches])

  const toggleCategory = (category: PatchCategory) => {
    setCollapsedCategories((prev) => {
      const next = new Set(prev)
      if (next.has(category)) next.delete(category)
      else next.add(category)
      return next
    })
  }

  const selectPatch = (patchKey: string) => {
    setSelectedKey(patchKey)
    setPatchDetailTab('files')
    setActionError(null)
    setUploadError(null)
  }

  const handleCreate = async () => {
    setActionError(null)
    try {
      const res = await createMutation.mutateAsync({
        expansion: newExpansion,
        kind: newKind,
        name: newName.trim() || undefined,
        parentIndex: newKind === 'hotfix' && newParentIndex.trim() ? newParentIndex.trim() : undefined,
      })
      setShowCreate(false)
      setNewExpansion('classic')
      setNewKind('patch')
      setNewParentIndex('')
      setNewName('')
      setSelectedKey(res.data.key)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleDownloadTemplate = async () => {
    setActionError(null)
    setDownloadingTemplate(true)
    try {
      await downloadPatchTemplate(stackId)
    } catch (err) {
      setActionError(extractError(err))
    } finally {
      setDownloadingTemplate(false)
    }
  }

  const handleSaveDescription = async () => {
    if (!selectedKey) return
    setDescriptionSaveError(null)
    try {
      await saveDescriptionMutation.mutateAsync({
        patchKey: selectedKey,
        content: descriptionValue,
      })
      setDescriptionDraft(null)
      setDescriptionSaved(true)
      setTimeout(() => setDescriptionSaved(false), 3000)
    } catch (err) {
      setDescriptionSaveError(extractError(err))
    }
  }

  const handleSelectImportFile = (file: File | null | undefined) => {
    if (!file) return
    if (!isImportArchive(file)) {
      setActionError(`Unsupported file type. Accepted archives: ${IMPORT_ARCHIVE_EXTENSIONS.join(', ')}.`)
      return
    }
    setActionError(null)
    setImportSummary(null)
    setImportFile(file)
  }


  const handleImportCollection = async () => {
    setActionError(null)
    setImportSummary(null)

    if (!importFile) {
      setActionError('Choose a patch collection archive to import.')
      return
    }

    setImportUploadPercent(0)
    try {
      const res = await importMutation.mutateAsync({
        file: importFile,
        mode: effectiveImportMode,
        onProgress: setImportUploadPercent,
      })
      const imported = res.data.importedPatches
      setImportFile(null)
      const remapped = imported.filter((p) => p.sourceKey !== p.targetKey)
      setImportSummary(
        `Imported ${res.data.importedCount} patch${res.data.importedCount === 1 ? '' : 'es'} in ${res.data.mode} mode.` +
          (remapped.length > 0
            ? ` Mapped ${remapped.length} archive folder${remapped.length === 1 ? '' : 's'} onto existing templates (e.g. ${remapped[0].sourceKey} → ${remapped[0].targetKey}).`
            : '')
      )
      if (imported[0]?.targetKey) {
        setSelectedKey(imported[0].targetKey)
      }
    } catch (err) {
      setActionError(extractError(err))
    } finally {
      setImportUploadPercent(null)
    }
  }

  const handleApply = async () => {
    if (!selectedKey) return
    if (hasIpModule && detail?.progression) {
      setConfirmApply(true)
      return
    }
    await runApply()
  }

  const runApply = async () => {
    if (!selectedKey) return
    setConfirmApply(false)
    setActionError(null)
    try {
      await applyMutation.mutateAsync(selectedKey)
      setPollRequested(true)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleBootstrap = async () => {
    setActionError(null)
    setValidationResult(null)
    try {
      const res = await bootstrapMutation.mutateAsync()
      setImportSummary(
        `Prepared server-wide progression: ${res.data.templatesCreated} patch templates created. Import patch content, then run patch validation before applying.`
      )
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleValidatePatches = async () => {
    setActionError(null)
    setValidationResult(null)
    try {
      const res = await validateMutation.mutateAsync()
      setValidationResult(res.data)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleUpload = async (category: string, files: File[], descriptions?: string[]) => {
    if (!selectedKey) return
    setUploadError(null)
    const arr = Array.from(files)
    if (arr.length === 0) return

    // patch-D is generated from the compiled DBC files, so block a manual upload of it up front
    // (the backend also rejects it) with a clear pointer to the DBC section.
    if (category === 'mpq' && arr.some((f) => f.name.toLowerCase() === 'patch-d.mpq')) {
      setUploadError({
        category,
        message:
          'patch-D.MPQ is reserved for DBC files and is compiled automatically from the CSV files placed in the DBC section above. Upload your DBC changes there instead.',
      })
      return
    }

    setUploadingCategory(category)
    try {
      if (descriptions) {
        // Per-file descriptions: upload each file in its own request so each keeps its own note.
        for (let i = 0; i < arr.length; i++) {
          await uploadMutation.mutateAsync({
            patchKey: selectedKey,
            category,
            files: [arr[i]],
            description: descriptions[i],
          })
        }
      } else {
        await uploadMutation.mutateAsync({ patchKey: selectedKey, category, files: arr })
      }
    } catch (err) {
      setUploadError({ category, message: extractError(err) })
    } finally {
      setUploadingCategory(null)
    }
  }

  const handleContainerUpload = async (
    category: string,
    items: { file: File; path: string }[]
  ) => {
    if (!selectedKey || items.length === 0) return
    setUploadError(null)
    setUploadingCategory(category)
    try {
      await uploadContainerMutation.mutateAsync({ patchKey: selectedKey, category, items })
    } catch (err) {
      setUploadError({ category, message: extractError(err) })
    } finally {
      setUploadingCategory(null)
    }
  }

  const errorFor = (category: string) =>
    uploadError?.category === category ? uploadError.message : undefined

  const handleDelete = async (category: string, fileName: string) => {
    if (!selectedKey) return
    setActionError(null)
    try {
      await deleteMutation.mutateAsync({ patchKey: selectedKey, category, fileName })
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleDeletePatch = async () => {
    if (!selectedKey || hasAppliedPatches || isApplying) return
    if (
      !window.confirm(
        `Delete patch folder "${selectedKey}" and all its contents? This cannot be undone.`
      )
    ) {
      return
    }
    setActionError(null)
    try {
      await deletePatchMutation.mutateAsync(selectedKey)
      setSelectedKey(null)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleDropAllPatches = async () => {
    setActionError(null)
    try {
      await dropAllPatchesMutation.mutateAsync()
      setConfirmDropAll(false)
      setDropAllConfirmText('')
      setSelectedKey(null)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const openDropAllConfirm = () => {
    setDropAllConfirmText('')
    setConfirmDropAll(true)
  }

  const closeDropAllConfirm = () => {
    setConfirmDropAll(false)
    setDropAllConfirmText('')
  }

  const dropAllConfirmMatches =
    dropAllConfirmText.trim().toLowerCase() === DROP_ALL_PATCHES_CONFIRMATION

  const handleBaseline = async () => {
    setActionError(null)
    try {
      await baselineMutation.mutateAsync()
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleReapplyAll = async () => {
    setConfirmReapply(false)
    setActionError(null)
    try {
      await reapplyMutation.mutateAsync()
      setPollRequested(true)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  const handleDownloadLog = async () => {
    try {
      await downloadApplyLog(stackId, applyStatus?.runId)
    } catch (err) {
      setActionError(extractError(err))
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="w-8 h-8 text-blue-600 animate-spin" />
      </div>
    )
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-6 text-red-800">
        {extractError(error)}
      </div>
    )
  }

  const ipValidationRequired = overview?.individualProgressionValidationRequired ?? false
  const ipValidationCurrent = overview?.individualProgressionValidationCurrent ?? false
  const patchApplyBlocked = ipValidationRequired && !ipValidationCurrent
  const expectedProgressionPatchCount = overview?.individualProgressionExpectedPatchCount ?? 0

  const patchIsApplyable =
    selectedSummary?.status === 'Next' && !isApplying && !patchApplyBlocked
  // Applying but this session didn't start it (no local poll request and status shows another run).
  const appliedByOther = isApplying && !pollRequested

  // Reapply-all runs against every applied patch (patchKey "*"). We surface per-patch progress in the
  // list by parsing the live phase, whose patch-scoped stages are named "<stage>:<patchKey>".
  const reapplyingAll = isApplying && (applyStatus?.patchKey === '*' || overview?.applyingPatchKey === '*')
  const activePhase = applyStatus?.phase ?? ''
  const activePatchKey = (() => {
    const idx = activePhase.indexOf(':')
    return idx === -1 ? null : activePhase.slice(idx + 1)
  })()
  const appliedOrder = (overview?.patches ?? []).filter((p) => p.status === 'Applied').map((p) => p.key)
  const activeIndex = activePatchKey ? appliedOrder.indexOf(activePatchKey) : -1

  type ReapplyState = 'reapplying' | 'done' | 'queued' | null
  const reapplyStateFor = (key: string, status: PatchStatus): ReapplyState => {
    if (!reapplyingAll || status !== 'Applied') return null
    if (key === activePatchKey) return 'reapplying'
    // No specific patch in the current phase (a global stage like extract-dbc/restart) → generic spinner.
    if (activeIndex === -1) return 'reapplying'
    return appliedOrder.indexOf(key) < activeIndex ? 'done' : 'queued'
  }

  const renderPatchRow = (patch: PatchSummaryDto, category: PatchCategory) => {
    const badge = STATUS_BADGE[patch.status]
    const reapplyState = reapplyStateFor(patch.key, patch.status)
    const selected = selectedKey === patch.key
    const categoryStyle = CATEGORY_STYLE[category]
    const title = patchRowTitle(patch)
    return (
      <button
        key={patch.key}
        type="button"
        onClick={() => selectPatch(patch.key)}
        className={`w-full text-left border-l-4 px-3 py-2.5 transition-colors ${
          STATUS_ROW_ACCENT[patch.status]
        } ${
          reapplyState === 'reapplying'
            ? 'bg-blue-50/80 ring-1 ring-inset ring-blue-200'
            : selected
            ? 'bg-blue-50 shadow-sm ring-1 ring-inset ring-blue-200'
            : 'hover:bg-gray-50/90'
        }`}
      >
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-1.5">
              <span className={`rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide ${categoryStyle.chip}`}>
                {patch.index}
              </span>
              <span className="truncate font-medium text-sm text-gray-900">{title}</span>
            </div>
            <p className="mt-0.5 truncate font-mono text-xs text-gray-400">{patch.key}</p>
          </div>
          {reapplyState === 'reapplying' ? (
            <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-blue-100 px-2 py-0.5 text-[11px] text-blue-700">
              <Loader2 className="h-3 w-3 animate-spin" /> Reapplying
            </span>
          ) : reapplyState === 'done' ? (
            <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-green-100 px-2 py-0.5 text-[11px] text-green-700">
              <CheckCircle2 className="h-3 w-3" /> Done
            </span>
          ) : reapplyState === 'queued' ? (
            <span className="inline-flex shrink-0 items-center gap-1 rounded-full bg-gray-100 px-2 py-0.5 text-[11px] text-gray-500">
              <Clock className="h-3 w-3" /> Queued
            </span>
          ) : (
            <span className={`inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-[11px] ${badge.className}`}>
              {badge.icon}
              {badge.label}
            </span>
          )}
        </div>
        <p className="mt-1.5 line-clamp-2 text-xs leading-relaxed text-gray-500">{patch.description}</p>
        <div className="mt-2 flex flex-wrap gap-1">
          {patch.sqlCount > 0 && (
            <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium text-gray-600">
              {patch.sqlCount} sql
            </span>
          )}
          {patch.dbcCount > 0 && (
            <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium text-gray-600">
              {patch.dbcCount} dbc
            </span>
          )}
          {patch.mapCount > 0 && (
            <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium text-gray-600">
              {patch.mapCount} map
            </span>
          )}
          {patch.mpqCount > 0 && (
            <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium text-gray-600">
              {patch.mpqCount} mpq
            </span>
          )}
          {patch.sqlCount + patch.dbcCount + patch.mapCount + patch.mpqCount === 0 && (
            <span className="text-[10px] text-gray-400">No files yet</span>
          )}
        </div>
      </button>
    )
  }

  const totalPatchCount = overview?.patches.length ?? 0
  const importInProgress = importMutation.isPending || importUploadPercent !== null
  const importUploading = importUploadPercent !== null && importUploadPercent < 100
  const importProcessing = importMutation.isPending && !importUploading
  const showBootstrapCta =
    hasIpModule &&
    (overview?.currentLevel ?? 0) === 0 &&
    !isApplying &&
    !overview?.individualProgressionBootstrapped
  const showValidationPanel = true
  const validationMode =
    validationResult?.mode ??
    (hasIpModule && (overview?.individualProgressionExpectedPatchCount ?? 0) > 0 ? 'Full' : 'ConfigOnly')
  const validationPanelPositive =
    ipValidationCurrent ||
    (validationResult?.passed === true && validationResult.mode === 'ConfigOnly')
  const validationModeDescription =
    validationMode === 'Full'
      ? 'Validates patch folders against the synced Azeroth-Platform-Progression reference and checks config overrides.'
      : 'Validates config override keys against live server configs. Run progression sync first for full structure checks.'
  const progressionPatchCountMismatch =
    expectedProgressionPatchCount > 0 &&
    (validationResult?.patchCount ?? overview?.individualProgressionPatchCount ?? 0) !==
      expectedProgressionPatchCount

  const applyProgressionPreview = (() => {
    const meta = detail?.progression
    if (!meta) return null
    let nextExpansion: string | null = null
    if (meta.expansion === 'tbc' && meta.state === 8) nextExpansion = '1'
    if (meta.expansion === 'wotlk' && meta.state === 14) nextExpansion = '2'
    return { meta, nextExpansion }
  })()

  return (
    <div className="space-y-5">
      {/* Header */}
      <section className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4 px-5 py-4">
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Patches</h2>
            <p className="mt-1 max-w-2xl text-sm text-gray-500">
              Manage stack migrations in order — SQL, DBC, maps, and client MPQs per patch index.
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button
              onClick={() => setShowCreate((v) => !v)}
              className={`flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition-colors ${
                showCreate
                  ? 'bg-blue-700 text-white'
                  : 'bg-blue-600 text-white hover:bg-blue-700'
              }`}
            >
              <Plus className="h-4 w-4" /> New Patch
            </button>
            <button
              onClick={() => setShowImport((v) => !v)}
              disabled={isApplying}
              className={`flex items-center gap-2 rounded-md border px-3 py-2 text-sm transition-colors disabled:opacity-50 ${
                showImport
                  ? 'border-blue-300 bg-blue-50 text-blue-800'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              }`}
              title="Import a zip containing classic, tbc, wotlk, and custom patch folders"
            >
              <Upload className="h-4 w-4" />
              Import
            </button>
            <button
              onClick={() => setShowBrowser((v) => !v)}
              className={`flex items-center gap-2 rounded-md border px-3 py-2 text-sm transition-colors ${
                showBrowser
                  ? 'border-gray-400 bg-gray-100 text-gray-900'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              }`}
              title="Browse the stack's patches folder"
            >
              <FolderOpen className="h-4 w-4" />
              Browse
            </button>
            <button
              type="button"
              onClick={openDropAllConfirm}
              disabled={
                hasAppliedPatches || isApplying || totalPatchCount === 0 || dropAllPatchesMutation.isPending
              }
              title={
                hasAppliedPatches
                  ? 'Unavailable after any patch has been applied'
                  : totalPatchCount === 0
                  ? 'No patch folders to remove'
                  : 'Delete every patch folder on this stack'
              }
              className="flex items-center gap-2 rounded-md border border-red-200 px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
            >
              {dropAllPatchesMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <Trash2 className="h-4 w-4" />
              )}
              Drop all patches
            </button>
          </div>
        </div>
        <div className="grid gap-px border-t border-gray-100 bg-gray-100 sm:grid-cols-2 lg:grid-cols-4">
          <div className="bg-white px-5 py-3">
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">Current index</p>
            <p className="mt-0.5 font-mono text-sm font-semibold text-gray-900">
              {overview?.currentIndex || 'none'}
            </p>
          </div>
          <div className="bg-white px-5 py-3">
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">DBC baseline</p>
            <p className={`mt-0.5 flex items-center gap-1.5 text-sm font-medium ${
              overview?.baselineInitialized ? 'text-green-700' : 'text-amber-700'
            }`}>
              {overview?.baselineInitialized ? (
                <>
                  <CheckCircle2 className="h-4 w-4" /> Captured
                </>
              ) : (
                <>
                  <AlertTriangle className="h-4 w-4" /> Not captured
                </>
              )}
            </p>
          </div>
          <div className="bg-white px-5 py-3">
            <p className="text-[11px] font-medium uppercase tracking-wide text-gray-400">Patches</p>
            <p className="mt-0.5 text-sm font-semibold text-gray-900">{totalPatchCount}</p>
          </div>
          <div className="flex items-center bg-white px-5 py-3">
            <div className="flex flex-wrap gap-2">
              <button
                onClick={handleBaseline}
                disabled={baselineMutation.isPending}
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                title="Capture the current DBC set from the running stack as the baseline"
              >
                {baselineMutation.isPending ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <Database className="h-3.5 w-3.5" />
                )}
                {overview?.baselineInitialized ? 'Recapture baseline' : 'Init baseline'}
              </button>
              <button
                onClick={() => setConfirmReapply(true)}
                disabled={
                  reapplyMutation.isPending ||
                  isApplying ||
                  (overview?.currentLevel ?? 0) === 0 ||
                  patchApplyBlocked
                }
                className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                title={
                  patchApplyBlocked
                    ? 'Validate patches for the current server build first'
                    : 'Reapply all applied patches on top of standard AzerothCore updates'
                }
              >
                {reapplyMutation.isPending ? (
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <RefreshCw className="h-3.5 w-3.5" />
                )}
                Reapply all
              </button>
            </div>
          </div>
        </div>
      </section>

      {showBootstrapCta && (
        <section className="rounded-lg border border-violet-200 bg-violet-50 px-5 py-4 shadow-sm">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <p className="text-sm font-semibold text-violet-900">Individual Progression</p>
              <p className="mt-1 max-w-2xl text-sm text-violet-800">
                Prepare server-wide progression settings, then use{' '}
                <strong>Sync with mod-individual-progression</strong> below to pull both repositories,
                create patch folders from Azeroth-Platform-Progression, and import mapped module files.
              </p>
            </div>
            <button
              type="button"
              onClick={handleBootstrap}
              disabled={bootstrapMutation.isPending}
              className="inline-flex shrink-0 items-center gap-2 rounded-md bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700 disabled:opacity-50"
            >
              {bootstrapMutation.isPending ? (
                <Loader2 className="h-4 w-4 animate-spin" />
              ) : (
                <TrendingUp className="h-4 w-4" />
              )}
              Prepare progression
            </button>
          </div>
        </section>
      )}

      {showValidationPanel && (
        <section
          className={`rounded-lg border px-5 py-4 shadow-sm ${
            validationPanelPositive ? 'border-green-200 bg-green-50' : 'border-amber-200 bg-amber-50'
          }`}
        >
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-semibold text-gray-900">Patch validation</p>
              <p className="mt-1 max-w-2xl text-sm text-gray-600">{validationModeDescription}</p>
              {ipValidationCurrent && overview?.individualProgressionValidationPassedAt && validationMode === 'Full' && (
                <p className="mt-2 flex items-center gap-1.5 text-sm text-green-800">
                  <CheckCircle2 className="h-4 w-4 shrink-0" />
                  Validation passed for the current server build (
                  {new Date(overview.individualProgressionValidationPassedAt).toLocaleString()}).
                </p>
              )}
              {patchApplyBlocked && validationMode === 'Full' && (
                <p className="mt-2 flex items-center gap-1.5 text-sm text-amber-800">
                  <AlertTriangle className="h-4 w-4 shrink-0" />
                  Patch apply is blocked until validation passes.
                  {progressionPatchCountMismatch && (
                    <>
                      {' '}
                      Found {overview?.individualProgressionPatchCount ?? 0} of{' '}
                      {expectedProgressionPatchCount} expected progression patches from
                      Azeroth-Platform-Progression.
                    </>
                  )}
                </p>
              )}
            </div>
            <div className="flex shrink-0 flex-wrap items-center gap-2">
              <button
                type="button"
                onClick={handleValidatePatches}
                disabled={validateMutation.isPending}
                className="inline-flex shrink-0 items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
              >
                {validateMutation.isPending ? (
                  <Loader2 className="h-4 w-4 animate-spin" />
                ) : (
                  <ShieldCheck className="h-4 w-4" />
                )}
                Validate patches
              </button>
            </div>
          </div>

          {validationResult && (
            <div
              className={`mt-4 rounded-md border p-4 text-sm ${
                validationResult.passed
                  ? 'border-green-200 bg-white text-green-900'
                  : 'border-red-200 bg-white text-red-900'
              }`}
            >
              <p className="font-medium">
                {validationResult.passed ? 'Validation passed' : 'Validation failed'}
                {validationResult.expectedPatchCount > 0
                  ? ` — ${validationResult.patchCount} / ${validationResult.expectedPatchCount} progression patches`
                  : validationResult.patchCount > 0
                  ? ` — ${validationResult.patchCount} progression patch${validationResult.patchCount === 1 ? '' : 'es'}`
                  : ''}
              </p>
              {!validationResult.passed && progressionPatchCountMismatch && (
                <p className="mt-2 text-sm text-red-800">
                  Missing patch folders can be created by running Update &amp; re-sync in the progression
                  sync panel below.
                </p>
              )}
              {validationResult.errors.length > 0 && (
                <ul className="mt-2 list-disc space-y-1 pl-5">
                  {validationResult.errors.map((entry) => (
                    <li key={entry}>{entry}</li>
                  ))}
                </ul>
              )}
              {validationResult.keyChecks.length > 0 && (
                <div className="mt-3 overflow-x-auto">
                  <table className="min-w-full text-xs">
                    <thead>
                      <tr className="text-left text-gray-500">
                        <th className="py-1 pr-4">Patch</th>
                        <th className="py-1 pr-4">Source</th>
                        <th className="py-1 pr-4">Server config</th>
                        <th className="py-1 pr-4">Key</th>
                        <th className="py-1 pr-4">Available</th>
                        <th className="py-1">Details</th>
                      </tr>
                    </thead>
                    <tbody>
                      {validationResult.keyChecks.map((check) => (
                        <tr
                          key={`${check.patchKey ?? 'none'}:${check.configSource ?? check.configPath}:${check.key}`}
                          className="border-t border-gray-100"
                        >
                          <td className="py-1 pr-4 font-mono">{check.patchKey ?? '—'}</td>
                          <td className="py-1 pr-4 font-mono">{check.configSource ?? '—'}</td>
                          <td className="py-1 pr-4 font-mono">{check.configPath}</td>
                          <td className="py-1 pr-4 font-mono">{check.key}</td>
                          <td className="py-1 pr-4">
                            {check.exists && check.canRead ? 'Yes' : 'No'}
                          </td>
                          <td className="py-1 text-gray-600">{check.error ?? check.value ?? ''}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </section>
      )}

      {ipBootstrapped && (
        <ProgressionSyncPanel stackId={stackId} onReapplyAllRecommended={() => setConfirmReapply(true)} />
      )}

      {actionError && (
        <div className="bg-red-50 border border-red-200 rounded-md px-4 py-2 text-sm text-red-700">
          {actionError}
        </div>
      )}

      {importSummary && (
        <div className="bg-green-50 border border-green-200 rounded-md px-4 py-2 text-sm text-green-700">
          {importSummary}
        </div>
      )}

      {appliedByOther && (
        <div className="bg-amber-50 border border-amber-200 rounded-md px-4 py-2 text-sm text-amber-800 flex items-center gap-2">
          <Lock className="w-4 h-4" />
          An apply is currently in progress
          {overview?.applyingPatchKey && overview.applyingPatchKey !== '*'
            ? ` for ${overview.applyingPatchKey}`
            : ''}
          {' '}(started by another session). Applying is locked until it finishes.
        </div>
      )}

      {applyStatus && (applyStatus.isApplying || applyStatus.success != null) && (
        <div
          className={`rounded-md border px-4 py-3 text-sm ${
            applyStatus.isApplying
              ? 'bg-blue-50 border-blue-200 text-blue-800'
              : applyStatus.success
              ? 'bg-green-50 border-green-200 text-green-800'
              : 'bg-red-50 border-red-200 text-red-800'
          }`}
        >
          <div className="flex items-center justify-between mb-1">
            <p className="font-medium flex items-center gap-2">
              {applyStatus.isApplying && <Loader2 className="w-4 h-4 animate-spin" />}
              {applyStatus.isApplying
                ? `Applying ${applyStatus.patchKey === '*' ? 'all patches' : applyStatus.patchKey ?? ''}${
                    applyStatus.phase ? ` — ${applyStatus.phase}` : ''
                  }...`
                : applyStatus.success
                ? 'Operation completed successfully.'
                : 'Operation failed.'}
            </p>
            {applyStatus.logAvailable && (
              <button
                onClick={handleDownloadLog}
                className="inline-flex items-center gap-1 text-xs px-2 py-1 border border-current/30 rounded hover:bg-white/40"
                title="Download the full trace log"
              >
                <Download className="w-3.5 h-3.5" /> Download log
              </button>
            )}
          </div>
          {applyStatus.isApplying && (
            <p className="text-xs opacity-80 mb-1">
              Running in the background — this cannot be cancelled. You can safely leave this page; the
              run continues on the server.
            </p>
          )}
          <pre ref={logRef} className="whitespace-pre-wrap font-mono text-xs max-h-48 overflow-auto">
            {applyStatus.log.join('\n')}
            {applyStatus.error ? `\n${applyStatus.error}` : ''}
          </pre>
        </div>
      )}

      {showBrowser && <PatchesFolderBrowser stackId={stackId} />}


      {showImport && (
        <section
          className="rounded-lg border p-5 shadow-sm space-y-4 border-gray-200 bg-white"
        >
          <div>
            <h3 className="font-semibold text-gray-900">Import patch collection</h3>
            <p className="text-sm text-gray-500 mt-1">
              Upload an archive (zip, rar, 7z, tar, …) with expansion folders (
              <span className="font-mono">classic</span>, <span className="font-mono">tbc</span>,{' '}
              <span className="font-mono">wotlk</span>, <span className="font-mono">custom</span>) or patch
              folders at the root. Each patch folder must be named <span className="font-mono">patch {'{index}'}</span>{' '}
              or <span className="font-mono">patch {'{index} {name}'}</span>. Indices use expansion roots{' '}
              <span className="font-mono">1</span> (classic), <span className="font-mono">2</span> (tbc),{' '}
              <span className="font-mono">3</span> (wotlk), <span className="font-mono">4</span> (custom) with
              sub-versions like <span className="font-mono">1.1</span> (patch) or{' '}
              <span className="font-mono">1.1.1</span> (hotfix). Override imports preserve archive indices;
              append assigns the next <span className="font-mono">1.x</span>, <span className="font-mono">2.x</span>,{' '}
              <span className="font-mono">3.x</span>, or <span className="font-mono">4.x</span> per expansion.
            </p>
          </div>

          <div className="rounded-md border border-gray-200 bg-gray-50 p-3 text-xs text-gray-700 font-mono whitespace-pre-wrap">
            {`collection.zip
├── classic/                         (or tbc/, wotlk/)
│   └── patch 1.1/                   e.g. patch 1.0, patch 1.1, patch 2.0, patch 3.1 my_content
│       ├── description.md           optional — shown on the Description tab
│       ├── description.txt          optional — alternative to description.md
│       ├── sql/
│       │   ├── world/*.sql
│       │   ├── auth/*.sql
│       │   └── characters/*.sql
│       ├── dbc/**                   CSV, .txt, or .dbc files (subfolders allowed)
│       ├── map/**                   map files (subfolders allowed)
│       └── mpq/*.mpq                client patch archives (not patch-D.MPQ)
│           remove.json              optional — { "remove": "Patch-L.MPQ" } retires a published MPQ on apply

Flat layout also works:
└── patch 1.1/sql/world/...         at the zip root when expansion folders are omitted`}
          </div>

          <p className="text-xs text-gray-500">
            Patch descriptions default to <span className="font-mono">no description</span> unless{' '}
            <span className="font-mono">description.md</span> or <span className="font-mono">description.txt</span> is
            included in the patch folder. To retire a published client MPQ on apply, add a JSON file under{' '}
            <span className="font-mono">mpq/</span> such as{' '}
            <span className="font-mono">{'{ "remove": "Patch-L.MPQ" }'}</span> (case-insensitive file name).
          </p>

          <button
            type="button"
            onClick={handleDownloadTemplate}
            disabled={downloadingTemplate}
            className="px-3 py-2 border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50 flex items-center gap-2 text-sm disabled:opacity-50"
          >
            {downloadingTemplate ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <Download className="w-4 h-4" />
            )}
            Download patch template
          </button>

          <div className="flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => setImportMode('merge')}
              className={`px-3 py-2 rounded-md border text-sm ${
                effectiveImportMode === 'merge'
                  ? 'border-blue-500 bg-blue-50 text-blue-700 font-medium'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              }`}
            >
              Merge
            </button>
            <button
              type="button"
              onClick={() => setImportMode('append')}
              className={`px-3 py-2 rounded-md border text-sm ${
                effectiveImportMode === 'append'
                  ? 'border-blue-500 bg-blue-50 text-blue-700 font-medium'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              }`}
            >
              Append
            </button>
            <button
              type="button"
              onClick={() => setImportMode('override')}
              disabled={hasAppliedPatches}
              title={hasAppliedPatches ? 'Override is unavailable after any patch has been applied.' : undefined}
              className={`px-3 py-2 rounded-md border text-sm disabled:opacity-40 disabled:cursor-not-allowed ${
                effectiveImportMode === 'override'
                  ? 'border-blue-500 bg-blue-50 text-blue-700 font-medium'
                  : 'border-gray-300 text-gray-700 hover:bg-gray-50'
              }`}
            >
              Override
            </button>
          </div>

          <p className="text-xs text-gray-500">
            {effectiveImportMode === 'merge'
              ? 'Merge copies files into existing patch folders that match the archive names, and creates any missing patches using the indices from the archive.'
              : effectiveImportMode === 'append'
              ? 'Append keeps existing patches and assigns each imported folder the next patch index (1.x, 2.x, or 3.x) for its expansion. Optional labels from the archive are preserved.'
              : 'Override removes all existing patch folders first, then imports using the indices provided in the archive.'}
            {hasAppliedPatches && (
              <span className="ml-1 text-amber-600">
                Override is locked because patches have already been applied.
              </span>
            )}
          </p>

          <div className="space-y-3">
            <div
              onDragOver={(e) => {
                e.preventDefault()
                if (!importInProgress && !isApplying) setImportDragActive(true)
              }}
              onDragLeave={(e) => {
                e.preventDefault()
                setImportDragActive(false)
              }}
              onDrop={(e) => {
                e.preventDefault()
                setImportDragActive(false)
                if (importInProgress || isApplying) return
                handleSelectImportFile(e.dataTransfer.files?.[0])
              }}
              onClick={() => {
                if (importInProgress || isApplying) return
                importFileRef.current?.click()
              }}
              className={`flex flex-col items-center justify-center gap-2 rounded-md border-2 border-dashed px-4 py-8 text-center text-sm transition-colors ${
                importInProgress || isApplying
                  ? 'border-gray-200 bg-gray-50 text-gray-400 cursor-not-allowed'
                  : importDragActive
                  ? 'border-blue-500 bg-blue-50 text-blue-700 cursor-pointer'
                  : 'border-gray-300 bg-gray-50/50 text-gray-600 hover:border-blue-400 hover:bg-blue-50/40 cursor-pointer'
              }`}
            >
              {importInProgress ? (
                <Loader2 className="h-6 w-6 animate-spin text-blue-600" />
              ) : (
                <Upload className="h-6 w-6 text-blue-600" />
              )}
              <span className="font-medium">
                {importUploading
                  ? `Uploading… ${importUploadPercent}%`
                  : importProcessing
                  ? 'Processing archive on server…'
                  : importDragActive
                  ? 'Drop the archive to select it'
                  : importFile
                  ? importFile.name
                  : 'Drag & drop a patch collection archive here, or click to browse'}
              </span>
              {!importInProgress && (
                <span className="text-xs text-gray-400">
                  {importFile ? 'Click or drop another file to replace' : 'zip · rar · 7z · tar / tar.gz'}
                </span>
              )}
              <input
                ref={importFileRef}
                type="file"
                accept={IMPORT_ARCHIVE_ACCEPT}
                className="hidden"
                disabled={importInProgress || isApplying}
                onChange={(e) => {
                  handleSelectImportFile(e.target.files?.[0])
                  e.target.value = ''
                }}
              />
            </div>

            {importUploading && (
              <div className="h-2 w-full overflow-hidden rounded bg-gray-200">
                <div
                  className="h-full bg-blue-600 transition-all duration-150"
                  style={{ width: `${importUploadPercent}%` }}
                />
              </div>
            )}
            {importProcessing && (
              <div className="h-2 w-full overflow-hidden rounded bg-gray-200">
                <div className="h-full w-full animate-pulse bg-blue-400" />
              </div>
            )}

            <button
              type="button"
              onClick={handleImportCollection}
              disabled={importInProgress || isApplying || !importFile}
              className="px-4 py-2 text-white rounded-md flex items-center gap-2 disabled:opacity-50 bg-blue-600 hover:bg-blue-700"
            >
              {importInProgress && <Loader2 className="w-4 h-4 animate-spin" />}
              {importUploading
                ? `Uploading ${importUploadPercent}%`
                : importProcessing
                ? 'Processing…'
                : 'Import'}
            </button>
          </div>
        </section>
      )}

      {showCreate && (
        <section className="rounded-lg border border-blue-200 bg-blue-50/30 p-5 shadow-sm space-y-4">
          <div>
            <h3 className="font-semibold text-gray-900">Create new patch</h3>
            <p className="mt-1 text-sm text-gray-500">Choose expansion, kind, and an optional folder label.</p>
          </div>
          <div>
            <label className="block text-xs font-semibold uppercase tracking-wide text-gray-500 mb-2">Expansion</label>
            <div className="flex flex-wrap gap-2">
              {EXPANSIONS.map((exp) => {
                const active = newExpansion === exp.id
                return (
                  <button
                    key={exp.id}
                    type="button"
                    onClick={() => setNewExpansion(exp.id)}
                    className={`px-3 py-2 rounded-md border text-sm transition-colors ${
                      active
                        ? 'border-blue-500 bg-blue-50 text-blue-700 font-medium'
                        : 'border-gray-300 text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    {exp.label}
                    <span className="ml-1 text-xs text-gray-400">(root {EXPANSION_ROOT[exp.id]})</span>
                  </button>
                )
              })}
            </div>
          </div>
          <div>
            <label className="block text-xs font-semibold uppercase tracking-wide text-gray-500 mb-2">Kind</label>
            <div className="flex flex-wrap gap-2">
              {PATCH_KINDS.map((kind) => {
                const active = newKind === kind.id
                const disabled = kind.id === 'expansion' && expansionPatchExists
                return (
                  <button
                    key={kind.id}
                    type="button"
                    onClick={() => setNewKind(kind.id)}
                    disabled={disabled}
                    title={disabled ? 'Expansion patch already exists for this expansion.' : kind.hint}
                    className={`px-3 py-2 rounded-md border text-sm transition-colors disabled:opacity-40 disabled:cursor-not-allowed ${
                      active
                        ? 'border-blue-500 bg-blue-50 text-blue-700 font-medium'
                        : 'border-gray-300 text-gray-700 hover:bg-gray-50'
                    }`}
                  >
                    {kind.label}
                  </button>
                )
              })}
            </div>
            <p className="text-xs text-gray-500 mt-1">
              {PATCH_KINDS.find((kind) => kind.id === newKind)?.hint}
            </p>
          </div>
          {newKind === 'hotfix' && (
            <div>
              <label className="block text-xs font-semibold uppercase tracking-wide text-gray-500 mb-2">
                Parent patch (optional)
              </label>
              <select
                value={newParentIndex}
                onChange={(e) => setNewParentIndex(e.target.value)}
                className="w-full max-w-sm px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 bg-white"
              >
                <option value="">Latest patch release</option>
                {patchReleaseOptions.map((index) => (
                  <option key={index} value={index}>
                    {index}
                  </option>
                ))}
              </select>
            </div>
          )}
          <div>
            <label className="block text-xs font-semibold uppercase tracking-wide text-gray-500 mb-2">
              Optional label
            </label>
            <input
              type="text"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && canCreate && !createMutation.isPending) handleCreate()
              }}
              placeholder="custom_content"
              className="w-full max-w-sm px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
          </div>
          <div className="flex items-center gap-3">
            <button
              onClick={handleCreate}
              disabled={createMutation.isPending || !canCreate}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:opacity-50"
            >
              Create
            </button>
            <p className="text-sm text-gray-500">
              {newKind === 'expansion' && expansionPatchExists ? (
                <span className="text-amber-600">Expansion patch already exists for this expansion.</span>
              ) : (
                <>
                  Creates: <span className="font-mono font-medium text-gray-700">{previewKey}</span>
                </>
              )}
            </p>
          </div>
        </section>
      )}

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(280px,340px)_1fr]">
        {/* Patch list */}
        <aside className="lg:sticky lg:top-4 lg:self-start">
          <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
            <div className="border-b border-gray-100 bg-gray-50 px-4 py-3">
              <h3 className="text-sm font-semibold text-gray-900">Patch library</h3>
              <p className="mt-0.5 text-xs text-gray-500">Classic · TBC · WotLK · Custom</p>
            </div>
            <div className="max-h-[calc(100vh-12rem)] overflow-y-auto">
          {PATCH_CATEGORIES.map((category) => {
            const patches = patchesByCategory[category.id]
            const collapsed = collapsedCategories.has(category.id)
            const style = CATEGORY_STYLE[category.id]
            return (
              <div key={category.id} className="border-b border-gray-100 last:border-b-0">
                <button
                  type="button"
                  onClick={() => toggleCategory(category.id)}
                  className={`w-full flex items-center justify-between gap-2 border-b border-gray-100/80 px-3 py-2.5 transition-colors text-left ${style.header}`}
                >
                  <span className="flex min-w-0 items-center gap-2">
                    <ChevronDown
                      className={`h-4 w-4 shrink-0 text-gray-500 transition-transform ${collapsed ? '-rotate-90' : ''}`}
                    />
                    <span>
                      <span className="block text-sm font-semibold text-gray-800">{category.label}</span>
                      <span className="block text-[10px] text-gray-500">{category.hint}</span>
                    </span>
                  </span>
                  <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${style.chip}`}>
                    {patches.length}
                  </span>
                </button>
                {!collapsed && (
                  <div className="divide-y divide-gray-50 bg-white">
                    {patches.length === 0 ? (
                      <div className={`px-4 py-3 text-xs ${style.empty}`}>No patches in this category</div>
                    ) : (
                      patches.map((patch) => renderPatchRow(patch, category.id))
                    )}
                  </div>
                )}
              </div>
            )
          })}
            </div>
          </div>
        </aside>

        {/* Patch detail */}
        <main className="min-w-0">
          {!selectedKey ? (
            <div className="flex h-72 flex-col items-center justify-center gap-2 rounded-lg border border-dashed border-gray-300 bg-gray-50/80 shadow-sm">
              <FolderOpen className="h-8 w-8 text-gray-300" />
              <p className="text-sm font-medium text-gray-600">Select a patch from the library</p>
              <p className="text-xs text-gray-400">View files, edit description, and apply patches</p>
            </div>
          ) : (
            <div className="overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-gray-100 bg-gray-50/80 px-5 py-4">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <h3 className="truncate text-lg font-semibold text-gray-900">
                      {selectedSummary ? patchRowTitle(selectedSummary) : selectedKey}
                    </h3>
                    {selectedSummary && (
                      <span className={`inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium ${STATUS_BADGE[selectedSummary.status].className}`}>
                        {STATUS_BADGE[selectedSummary.status].icon}
                        {STATUS_BADGE[selectedSummary.status].label}
                      </span>
                    )}
                  </div>
                  {selectedSummary && (
                    <p className="mt-1 text-xs text-gray-500">
                      <span className="font-mono text-gray-600">{selectedSummary.key}</span>
                      {' · '}
                      Index <span className="font-mono font-medium text-gray-700">{selectedSummary.index}</span>
                    </p>
                  )}
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  {!hasAppliedPatches && (
                    <button
                      type="button"
                      onClick={handleDeletePatch}
                      disabled={deletePatchMutation.isPending || isApplying}
                      title="Delete this patch folder (available before any patch is applied)"
                      className="flex items-center gap-2 rounded-md border border-red-200 px-3 py-2 text-sm font-medium text-red-700 hover:bg-red-50 disabled:cursor-not-allowed disabled:opacity-40"
                    >
                      {deletePatchMutation.isPending ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                      ) : (
                        <Trash2 className="h-4 w-4" />
                      )}
                      Delete patch
                    </button>
                  )}
                  <button
                    onClick={handleApply}
                    disabled={!patchIsApplyable || applyMutation.isPending}
                    title={
                      patchApplyBlocked
                        ? 'Validate patches for the current server build before applying'
                        : isApplying
                        ? 'An apply is already in progress for this stack'
                        : patchIsApplyable
                        ? 'Apply this patch'
                        : 'Only the next incremental patch can be applied'
                    }
                    className="flex shrink-0 items-center gap-2 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white hover:bg-green-700 disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    {applyMutation.isPending || isApplying ? (
                      <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                      <Play className="h-4 w-4" />
                    )}
                    Apply patch
                  </button>
                </div>
              </div>

              <div className="flex gap-1 border-b border-gray-100 bg-white px-5">
                <button
                  type="button"
                  onClick={() => setPatchDetailTab('files')}
                  className={`px-3 py-2.5 text-sm font-medium border-b-2 -mb-px transition-colors ${
                    patchDetailTab === 'files'
                      ? 'border-blue-600 text-blue-700'
                      : 'border-transparent text-gray-500 hover:text-gray-800'
                  }`}
                >
                  Files
                </button>
                <button
                  type="button"
                  onClick={() => setPatchDetailTab('description')}
                  className={`px-3 py-2.5 text-sm font-medium border-b-2 -mb-px transition-colors ${
                    patchDetailTab === 'description'
                      ? 'border-blue-600 text-blue-700'
                      : 'border-transparent text-gray-500 hover:text-gray-800'
                  }`}
                >
                  Description
                </button>
                <button
                  type="button"
                  onClick={() => setPatchDetailTab('news')}
                  className={`px-3 py-2.5 text-sm font-medium border-b-2 -mb-px transition-colors ${
                    patchDetailTab === 'news'
                      ? 'border-blue-600 text-blue-700'
                      : 'border-transparent text-gray-500 hover:text-gray-800'
                  }`}
                >
                  News
                </button>
              </div>

              <div className="space-y-4 p-5">
              {patchDetailTab === 'news' ? (
                <PatchNewsEditor
                  stackId={stackId}
                  patchKey={selectedKey}
                  patchStatus={detail?.status}
                  onPreview={() => setShowPatchNewsPreview(true)}
                />
              ) : patchDetailTab === 'files' ? (
                <>
              <PatchFileCategory
                title="Config overrides"
                category="config"
                accept=".json"
                collapseStorageKey={sectionCollapseKey('config')}
                files={filesByCategory['config'] ?? []}
                uploading={uploadingCategory === 'config'}
                error={errorFor('config')}
                headerActions={
                  <PatchConfigOverridesPreviewButton
                    overrides={configOverrides}
                    onOpen={() => setShowConfigOverridesPreview(true)}
                  />
                }
                notice={
                  <span>
                    Upload JSON files (e.g.{' '}
                    <span className="font-mono">worldserver.json</span>) mapping config keys to
                    values. On apply, each key is written to the matching <span className="font-mono">.conf</span> file.
                  </span>
                }
                onUpload={handleUpload}
                onDelete={handleDelete}
              />
              <ContainerFileCategory
                title="Lua scripts"
                accept=".lua,.ext"
                collapseStorageKey={sectionCollapseKey('lua')}
                files={filesByCategory['lua'] ?? []}
                uploading={uploadingCategory === 'lua'}
                error={errorFor('lua')}
                notice={
                  <span>
                    Scripts are copied to the stack&apos;s <span className="font-mono">lua_scripts/</span>{' '}
                    folder on apply and loaded by the worldserver (Eluna / mod-ale).
                  </span>
                }
                onUploadItems={(items) => handleContainerUpload('lua', items)}
                onDelete={(fileName) => handleDelete('lua', fileName)}
              />
              <ContainerFileCategory
                title="SQL — world"
                accept=".sql"
                collapseStorageKey={sectionCollapseKey('sql/world')}
                files={filesByCategory['sql/world'] ?? []}
                uploading={uploadingCategory === 'sql/world'}
                error={errorFor('sql/world')}
                onUploadItems={(items) => handleContainerUpload('sql/world', items)}
                onDelete={(fileName) => handleDelete('sql/world', fileName)}
              />
              <ContainerFileCategory
                title="SQL — auth"
                accept=".sql"
                collapseStorageKey={sectionCollapseKey('sql/auth')}
                files={filesByCategory['sql/auth'] ?? []}
                uploading={uploadingCategory === 'sql/auth'}
                error={errorFor('sql/auth')}
                onUploadItems={(items) => handleContainerUpload('sql/auth', items)}
                onDelete={(fileName) => handleDelete('sql/auth', fileName)}
              />
              <ContainerFileCategory
                title="SQL — characters"
                accept=".sql"
                collapseStorageKey={sectionCollapseKey('sql/characters')}
                files={filesByCategory['sql/characters'] ?? []}
                uploading={uploadingCategory === 'sql/characters'}
                error={errorFor('sql/characters')}
                onUploadItems={(items) => handleContainerUpload('sql/characters', items)}
                onDelete={(fileName) => handleDelete('sql/characters', fileName)}
              />
              <ContainerFileCategory
                title="DBC (CSV / .txt / .dbc)"
                accept=".txt,.csv,.dbc"
                collapseStorageKey={sectionCollapseKey('dbc')}
                files={filesByCategory['dbc'] ?? []}
                uploading={uploadingCategory === 'dbc'}
                error={errorFor('dbc')}
                onUploadItems={(items) => handleContainerUpload('dbc', items)}
                onDelete={(fileName) => handleDelete('dbc', fileName)}
                onEdit={(fileName) => setEditFile(fileName)}
              />
              <ContainerFileCategory
                title="Maps"
                collapseStorageKey={sectionCollapseKey('map')}
                files={filesByCategory['map'] ?? []}
                uploading={uploadingCategory === 'map'}
                error={errorFor('map')}
                onUploadItems={(items) => handleContainerUpload('map', items)}
                onDelete={(fileName) => handleDelete('map', fileName)}
              />
              <div>
                <PatchFileCategory
                  title="MPQ (client patches)"
                  category="mpq"
                  accept=".mpq"
                  collapseStorageKey={sectionCollapseKey('mpq')}
                  files={filesByCategory['mpq'] ?? []}
                  uploading={uploadingCategory === 'mpq'}
                  requireDescription
                  error={errorFor('mpq')}
                  notice={
                    <span>
                      <span className="font-semibold">patch-D.MPQ is reserved.</span> It is built
                      automatically from the DBC section above (compiled CSVs and any uploaded .dbc files)
                      and cannot be uploaded manually.
                    </span>
                  }
                  onUpload={handleUpload}
                  onDelete={handleDelete}
                />
                <MpqRemovalPanel
                  stackId={stackId}
                  patchKey={selectedKey}
                  removals={detail?.mpqRemovals ?? []}
                />
                <MpqManifestPanel
                  files={detail?.files ?? []}
                  mpqRemovals={detail?.mpqRemovals ?? []}
                />
                <PatchLauncherThemePanel stackId={stackId} patchKey={selectedKey} detail={detail} />
                <PatchNewsFilesPanel files={newsFiles} />
              </div>
                </>
              ) : (
                <div className="rounded-lg border border-gray-200 bg-white p-4 space-y-3">
                  <p className="text-xs text-gray-500">
                    Saved to{' '}
                    <span className="font-mono">
                      {detail?.descriptionFile ?? 'description.md'}
                    </span>{' '}
                    in this patch folder. Clear the text and save to remove a custom description.
                  </p>
                  <textarea
                    value={descriptionValue}
                    onChange={(e) => setDescriptionDraft(e.target.value)}
                    rows={12}
                    spellCheck
                    className="w-full resize-y rounded-md border border-gray-300 bg-gray-50/50 p-3 text-sm text-gray-800 leading-relaxed focus:border-blue-400 focus:outline-none focus:ring-1 focus:ring-blue-300"
                  />
                  {descriptionSaveError && (
                    <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{descriptionSaveError}</div>
                  )}
                  <div className="flex items-center gap-3">
                    <button
                      type="button"
                      onClick={handleSaveDescription}
                      disabled={!descriptionDirty || saveDescriptionMutation.isPending}
                      className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                    >
                      {saveDescriptionMutation.isPending ? (
                        <Loader2 className="h-4 w-4 animate-spin" />
                      ) : (
                        <Save className="h-4 w-4" />
                      )}
                      Save description
                    </button>
                    {descriptionSaved && (
                      <span className="inline-flex items-center gap-1 text-sm text-green-600">
                        <CheckCircle2 className="h-4 w-4" /> Saved
                      </span>
                    )}
                    {descriptionDirty && !saveDescriptionMutation.isPending && (
                      <span className="text-sm text-gray-400">Unsaved changes</span>
                    )}
                  </div>
                </div>
              )}
              </div>
            </div>
          )}
        </main>
      </div>

      {editFile && selectedKey && (
        <DbcEditorDialog
          stackId={stackId}
          patchKey={selectedKey}
          fileName={editFile}
          onClose={() => setEditFile(null)}
        />
      )}

      <PatchConfigOverridesPreview
        stackId={stackId}
        patchKey={selectedKey}
        overrides={configOverrides}
        open={showConfigOverridesPreview}
        onClose={() => setShowConfigOverridesPreview(false)}
      />

      <PatchNewsPreview
        stackId={stackId}
        patchKey={selectedKey}
        open={showPatchNewsPreview}
        onClose={() => setShowPatchNewsPreview(false)}
        accentColor={newsPreviewAccent}
      />

      {confirmApply && applyProgressionPreview && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-lg w-full p-6 space-y-4">
            <div className="flex items-start gap-3">
              <TrendingUp className="w-6 h-6 text-violet-600 shrink-0" />
              <div>
                <h3 className="text-lg font-semibold">Apply progression patch?</h3>
                <p className="text-sm text-gray-600 mt-1">
                  <span className="font-mono font-medium">{applyProgressionPreview.meta.slug}</span>
                  {' '}(state {applyProgressionPreview.meta.state})
                </p>
                {!applyProgressionPreview.meta.incrementsProgression && (
                  <p className="text-sm text-amber-700 mt-2">
                    START patch — does not increment progression counters.
                  </p>
                )}
                {applyProgressionPreview.meta.incrementsProgression && (
                  <p className="text-sm text-gray-700 mt-2">
                    Progression counters will be incremented when this patch finishes applying.
                  </p>
                )}
                {applyProgressionPreview.nextExpansion && (
                  <p className="text-sm text-gray-700 mt-2">
                    Expansion will be set to{' '}
                    <strong>{applyProgressionPreview.nextExpansion}</strong>.
                  </p>
                )}
                <p className="text-xs text-gray-500 mt-3">
                  Individual Progression config will be updated automatically when this patch finishes applying.
                </p>
              </div>
            </div>
            <div className="flex justify-end gap-2">
              <button
                onClick={() => setConfirmApply(false)}
                className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                onClick={runApply}
                disabled={applyMutation.isPending}
                className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 flex items-center gap-2 disabled:opacity-50"
              >
                {applyMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                Apply patch
              </button>
            </div>
          </div>
        </div>
      )}

      {confirmReapply && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6 space-y-4">
            <div className="flex items-start gap-3">
              <AlertTriangle className="w-6 h-6 text-amber-500 shrink-0" />
              <div>
                <h3 className="text-lg font-semibold">Reapply all patches?</h3>
                <p className="text-sm text-gray-600 mt-1">
                  This re-applies every applied patch (SQL, DBC, maps and MPQ) on top of the standard
                  AzerothCore updates. It stops the world and auth servers, rebuilds the client DBC and
                  MPQ content, then restarts the stack.
                </p>
                <p className="text-sm font-medium text-gray-800 mt-2">
                  It runs in the background and cannot be cancelled once started.
                </p>
              </div>
            </div>
            <div className="flex justify-end gap-2">
              <button
                onClick={() => setConfirmReapply(false)}
                className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50"
              >
                Cancel
              </button>
              <button
                onClick={handleReapplyAll}
                disabled={reapplyMutation.isPending}
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 flex items-center gap-2 disabled:opacity-50"
              >
                {reapplyMutation.isPending && <Loader2 className="w-4 h-4 animate-spin" />}
                Start reapply
              </button>
            </div>
          </div>
        </div>
      )}

      {confirmDropAll && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-md rounded-lg bg-white p-6 shadow-xl space-y-4">
            <div className="flex items-start gap-3">
              <AlertTriangle className="h-6 w-6 shrink-0 text-red-600" />
              <div>
                <h3 className="text-lg font-semibold text-gray-900">Drop all patches?</h3>
                <p className="mt-1 text-sm text-gray-600">
                  This permanently deletes all {totalPatchCount} patch folder
                  {totalPatchCount === 1 ? '' : 's'} and their contents (SQL, DBC, maps, MPQ, config, lua).
                  This cannot be undone.
                </p>
              </div>
            </div>
            <div>
              <label htmlFor="drop-all-confirm" className="block text-sm font-medium text-gray-700">
                Type <span className="font-mono text-red-700">{DROP_ALL_PATCHES_CONFIRMATION}</span> to confirm
              </label>
              <input
                id="drop-all-confirm"
                type="text"
                autoComplete="off"
                value={dropAllConfirmText}
                onChange={(e) => setDropAllConfirmText(e.target.value)}
                className="mt-2 w-full rounded-md border border-gray-300 px-3 py-2 text-sm font-mono focus:outline-none focus:ring-2 focus:ring-red-500"
                placeholder={DROP_ALL_PATCHES_CONFIRMATION}
              />
            </div>
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={closeDropAllConfirm}
                disabled={dropAllPatchesMutation.isPending}
                className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleDropAllPatches}
                disabled={!dropAllConfirmMatches || dropAllPatchesMutation.isPending}
                className="flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {dropAllPatchesMutation.isPending && <Loader2 className="h-4 w-4 animate-spin" />}
                Drop all patches
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function extractError(err: unknown): string {
  if (err && typeof err === 'object' && 'response' in err) {
    const response = (err as { response?: { data?: { error?: string } } }).response
    if (response?.data?.error) return response.data.error
  }
  return err instanceof Error ? err.message : 'An unexpected error occurred.'
}
