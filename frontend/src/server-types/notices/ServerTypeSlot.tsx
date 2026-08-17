import { getServerTypeDefinition } from '@/server-types/registry'
import type { WizardNoticeContext } from '@/server-types/types'

export function ServerTypeSlot(ctx: WizardNoticeContext) {
  const definition = getServerTypeDefinition(ctx.serverType)
  if (!definition?.wizardModulesNotice) {
    return null
  }
  return <>{definition.wizardModulesNotice(ctx)}</>
}

export function ServerTypeReviewNotes(ctx: WizardNoticeContext) {
  const definition = getServerTypeDefinition(ctx.serverType)
  if (!definition?.wizardReviewNotes) {
    return null
  }
  return <>{definition.wizardReviewNotes(ctx)}</>
}
