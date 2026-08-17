import type { ReactNode } from 'react'
import type { SetupStep, SetupWorkflowBuilder } from '@/setup/types'

export type WizardNoticeContext = {
  serverType: string
  selectedModuleIds: string[]
  browseTab: 'curated' | 'community'
}

export type CustomSetup = {
  id: string
  title: string
  description: string
  /** Wizard / review copy. When set, CustomSetupNotice renders this instead of a generic sentence. */
  notice?: ReactNode
  skippable?: boolean
  /** Module ids that must be selected for this setup to appear in wizard copy. */
  requiresModuleIds: string[]
  buildSteps: () => SetupStep[]
}

export type ServerTypeDefinition = {
  id: string
  wizardModulesNotice?: (ctx: WizardNoticeContext) => ReactNode
  wizardReviewNotes?: (ctx: WizardNoticeContext) => ReactNode
  /** Addon catalog ids — rendered by RecommendedAddonsNotice. */
  recommendedAddonIds: string[]
  customSetups?: CustomSetup[]
  buildSetupSteps: SetupWorkflowBuilder
}
