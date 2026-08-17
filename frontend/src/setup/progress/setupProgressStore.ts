import { useEffect, useMemo, useState } from 'react'
import { STEP_IDS } from '@/setup/constants'

export type PlayerbotsSetupPhase = 'awaiting-start' | 'awaiting-reenable'

type PersistedProgress = {
  skipped: string[]
  dismissed: string[]
  playerbotsPhase: PlayerbotsSetupPhase | null
  playerbotsSetupComplete: boolean
}

export type SetupProgressStore = {
  isSkipped: (stepId: string) => boolean
  skip: (stepId: string) => void
  isDismissed: (stepId: string) => boolean
  dismiss: (stepId: string) => void
  getPlayerbotsPhase: () => PlayerbotsSetupPhase | null
  setPlayerbotsPhase: (phase: PlayerbotsSetupPhase | null) => void
  isPlayerbotsSetupComplete: () => boolean
  markPlayerbotsSetupComplete: () => void
  getSessionFlag: (key: string) => string | null
  setSessionFlag: (key: string, value: string | null) => void
}

const storageKey = (stackId: string) => `azp_setup_progress_${stackId}`
const legacySetupKey = (stackId: string) => `azp_ip_playerbots_setup_${stackId}`
const legacyPhaseKey = (stackId: string) => `azp_ip_playerbots_phase_${stackId}`
const legacyHintKey = (stackId: string) => `azp_ip_sync_hint_dismissed_${stackId}`

const cache = new Map<string, PersistedProgress>()
const sessionFlags = new Map<string, Record<string, string>>()
const listeners = new Map<string, Set<() => void>>()

function emptyProgress(): PersistedProgress {
  return {
    skipped: [],
    dismissed: [],
    playerbotsPhase: null,
    playerbotsSetupComplete: false,
  }
}

function readJson(stackId: string): PersistedProgress | null {
  try {
    const raw = localStorage.getItem(storageKey(stackId))
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<PersistedProgress>
    return {
      skipped: parsed.skipped ?? [],
      dismissed: parsed.dismissed ?? [],
      playerbotsPhase:
        parsed.playerbotsPhase === 'awaiting-start' || parsed.playerbotsPhase === 'awaiting-reenable'
          ? parsed.playerbotsPhase
          : null,
      playerbotsSetupComplete: parsed.playerbotsSetupComplete === true,
    }
  } catch {
    return null
  }
}

function migrateLegacy(stackId: string, current: PersistedProgress): PersistedProgress {
  const next = { ...current, skipped: [...current.skipped], dismissed: [...current.dismissed] }
  try {
    if (localStorage.getItem(legacySetupKey(stackId)) === '1') {
      next.playerbotsSetupComplete = true
    }
    const phase = localStorage.getItem(legacyPhaseKey(stackId))
    if (phase === 'awaiting-start' || phase === 'awaiting-reenable') {
      next.playerbotsPhase = phase
    }
    if (localStorage.getItem(legacyHintKey(stackId)) === '1' && !next.dismissed.includes(STEP_IDS.ipSyncHint)) {
      next.dismissed.push(STEP_IDS.ipSyncHint)
    }
  } catch {
    /* ignore */
  }
  return next
}

function loadProgress(stackId: string): PersistedProgress {
  const cached = cache.get(stackId)
  if (cached) return cached
  const loaded = migrateLegacy(stackId, readJson(stackId) ?? emptyProgress())
  cache.set(stackId, loaded)
  try {
    localStorage.setItem(storageKey(stackId), JSON.stringify(loaded))
  } catch {
    /* ignore */
  }
  return loaded
}

function persist(stackId: string, next: PersistedProgress) {
  cache.set(stackId, next)
  try {
    localStorage.setItem(storageKey(stackId), JSON.stringify(next))
  } catch {
    /* ignore */
  }
  listeners.get(stackId)?.forEach((listener) => listener())
}

function notify(stackId: string) {
  listeners.get(stackId)?.forEach((listener) => listener())
}

function subscribe(stackId: string, listener: () => void) {
  const set = listeners.get(stackId) ?? new Set()
  set.add(listener)
  listeners.set(stackId, set)
  return () => {
    set.delete(listener)
  }
}

export function isStepDoneOrSkipped(
  stepId: string,
  done: boolean,
  progress: Pick<SetupProgressStore, 'isSkipped'>,
): boolean {
  return done || progress.isSkipped(stepId)
}

export function createSetupProgressStore(stackId: string): SetupProgressStore {
  const read = () => loadProgress(stackId)

  return {
    isSkipped: (stepId) => read().skipped.includes(stepId),
    skip: (stepId) => {
      const current = read()
      if (current.skipped.includes(stepId)) return
      persist(stackId, { ...current, skipped: [...current.skipped, stepId] })
    },
    isDismissed: (stepId) => read().dismissed.includes(stepId),
    dismiss: (stepId) => {
      const current = read()
      if (current.dismissed.includes(stepId)) return
      persist(stackId, { ...current, dismissed: [...current.dismissed, stepId] })
    },
    getPlayerbotsPhase: () => read().playerbotsPhase,
    setPlayerbotsPhase: (phase) => {
      persist(stackId, { ...read(), playerbotsPhase: phase })
    },
    isPlayerbotsSetupComplete: () => read().playerbotsSetupComplete,
    markPlayerbotsSetupComplete: () => {
      persist(stackId, { ...read(), playerbotsSetupComplete: true, playerbotsPhase: null })
    },
    getSessionFlag: (key) => sessionFlags.get(stackId)?.[key] ?? null,
    setSessionFlag: (key, value) => {
      const flags = { ...(sessionFlags.get(stackId) ?? {}) }
      if (value === null) {
        delete flags[key]
      } else {
        flags[key] = value
      }
      sessionFlags.set(stackId, flags)
      notify(stackId)
    },
  }
}

export function useSetupProgressStore(stackId: string): SetupProgressStore {
  const [, setVersion] = useState(0)

  useEffect(() => subscribe(stackId, () => setVersion((value) => value + 1)), [stackId])

  return useMemo(() => createSetupProgressStore(stackId), [stackId])
}
