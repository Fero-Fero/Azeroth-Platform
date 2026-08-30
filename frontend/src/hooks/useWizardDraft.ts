import { useCallback } from 'react'
import type { WizardFormData } from '@/schemas/wizard.schemas'

const DRAFT_KEY = 'azp-wizard-draft'
const STEP_KEY = 'azp-wizard-step'

export function saveWizardDraft(data: Partial<WizardFormData>, step: number) {
  try {
    localStorage.setItem(DRAFT_KEY, JSON.stringify(data))
    localStorage.setItem(STEP_KEY, String(step))
  } catch {
    // storage unavailable
  }
}

export function loadWizardDraft(): { data: Partial<WizardFormData>; step: number } | null {
  try {
    const raw = localStorage.getItem(DRAFT_KEY)
    const stepRaw = localStorage.getItem(STEP_KEY)

    if (!raw) {
      return null
    }

    return {
      data: JSON.parse(raw) as Partial<WizardFormData>,
      step: Number(stepRaw ?? 0),
    }
  } catch {
    return null
  }
}

export function clearWizardDraft() {
  try {
    localStorage.removeItem(DRAFT_KEY)
    localStorage.removeItem(STEP_KEY)
  } catch {
    // storage unavailable
  }
}

export function useWizardDraft() {
  const save = useCallback((data: Partial<WizardFormData>, step: number) => {
    saveWizardDraft(data, step)
  }, [])

  const load = useCallback(() => loadWizardDraft(), [])
  const clear = useCallback(() => clearWizardDraft(), [])

  return { save, load, clear }
}
