import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { CheckCircle2, LayoutDashboard, Loader2, Play, ShieldCheck } from 'lucide-react'
import { useApplyPatch, useApplyStatus } from '@/hooks/usePatches'
import { useSetupProgressStore } from '@/setup/progress/setupProgressStore'
import { STEP_IDS } from '@/setup/constants'
import type { PatchSummaryDto } from '@/types/patch.types'

type FirstPatchSetupBannerProps = {
  stackId: string
  currentLevel: number
  hasCompletedSync: boolean
  validationCurrent: boolean
  patches: PatchSummaryDto[]
  isApplying: boolean
  onValidate: () => void
  validatePending: boolean
}

export default function FirstPatchSetupBanner({
  stackId,
  currentLevel,
  hasCompletedSync,
  validationCurrent,
  patches,
  isApplying,
  onValidate,
  validatePending,
}: FirstPatchSetupBannerProps) {
  const navigate = useNavigate()
  const progress = useSetupProgressStore(stackId)
  const applyMutation = useApplyPatch(stackId)
  const [pollApply, setPollApply] = useState(false)
  const { data: applyStatus } = useApplyStatus(stackId, isApplying || applyMutation.isPending || pollApply)
  const guide = progress.getIpPostSyncGuide()
  const firstPatch = patches.find((patch) => patch.status === 'Next') ?? patches[0] ?? null

  useEffect(() => {
    if (!hasCompletedSync || progress.isSkipped(STEP_IDS.prepareProgression)) {
      return
    }
    if (guide === 'done') {
      return
    }
    if (currentLevel > 0) {
      if (guide !== 'return') {
        progress.setIpPostSyncGuide('return')
      }
      return
    }
    if (validationCurrent) {
      if (guide !== 'apply' && guide !== 'return') {
        progress.setIpPostSyncGuide('apply')
      }
      return
    }
    if (!guide) {
      progress.setIpPostSyncGuide('validate')
    }
  }, [currentLevel, guide, hasCompletedSync, progress, validationCurrent])

  useEffect(() => {
    if (guide !== 'apply') {
      return
    }
    const applyFinished =
      applyStatus?.isApplying === false &&
      applyStatus.success === true &&
      (applyStatus.patchKey === firstPatch?.key || currentLevel > 0)
    if (applyFinished || currentLevel > 0) {
      progress.setIpPostSyncGuide('return')
    }
  }, [applyStatus, currentLevel, firstPatch?.key, guide, progress])

  if (!hasCompletedSync || progress.isSkipped(STEP_IDS.prepareProgression) || !guide || guide === 'done') {
    return null
  }

  if (guide === 'validate') {
    return (
      <section className="rounded-lg border border-amber-200 bg-amber-50 px-5 py-4 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <p className="text-sm font-semibold text-amber-950">Validate patches</p>
            <p className="mt-1 max-w-2xl text-sm text-amber-900">
              Progression sync finished. Validate the imported patch folders before applying the first patch.
            </p>
          </div>
          <button
            type="button"
            onClick={onValidate}
            disabled={validatePending}
            className="inline-flex shrink-0 items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50"
          >
            {validatePending ? <Loader2 className="h-4 w-4 animate-spin" /> : <ShieldCheck className="h-4 w-4" />}
            Validate patches
          </button>
        </div>
      </section>
    )
  }

  if (guide === 'apply') {
    return (
      <section className="rounded-lg border border-green-200 bg-green-50 px-5 py-4 shadow-sm">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <p className="text-sm font-semibold text-green-950">Apply the first patch</p>
            <p className="mt-1 max-w-2xl text-sm text-green-900">
              Validation passed. Apply{' '}
              <span className="font-mono">{firstPatch?.key ?? 'the first patch'}</span> to finish this one-time
              setup.
            </p>
          </div>
          <button
            type="button"
            onClick={() => {
              if (!firstPatch) return
              setPollApply(true)
              void applyMutation.mutateAsync(firstPatch.key)
            }}
            disabled={!firstPatch || applyMutation.isPending || isApplying}
            className="inline-flex shrink-0 items-center gap-2 rounded-md bg-green-600 px-4 py-2 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50"
          >
            {applyMutation.isPending || isApplying ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <Play className="h-4 w-4" />
            )}
            Apply first patch
          </button>
        </div>
      </section>
    )
  }

  return (
    <section className="rounded-lg border border-violet-200 bg-violet-50 px-5 py-4 shadow-sm">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-sm font-semibold text-violet-950">First patch applied</p>
          <p className="mt-1 max-w-2xl text-sm text-violet-900">
            Return to the overview to continue setup, including re-enabling playerbots after Individual
            Progression is ready.
          </p>
        </div>
        <button
          type="button"
          onClick={() => {
            progress.setIpPostSyncGuide('done')
            navigate(`/stacks/${stackId}`)
          }}
          className="inline-flex shrink-0 items-center gap-2 rounded-md bg-violet-600 px-4 py-2 text-sm font-medium text-white hover:bg-violet-700"
        >
          <LayoutDashboard className="h-4 w-4" />
          Back to overview
        </button>
      </div>
      <p className="mt-3 flex items-center gap-1.5 text-sm text-violet-800">
        <CheckCircle2 className="h-4 w-4 shrink-0" />
        You can dismiss this after opening the overview.
      </p>
    </section>
  )
}
