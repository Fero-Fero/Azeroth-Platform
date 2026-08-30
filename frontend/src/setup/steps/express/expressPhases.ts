import type { ExpressProvisionPhase } from '@/types/stack.types'

/** User-facing Express Setup checkpoints, in order. */
export const EXPRESS_PIPELINE_PHASES = [
  'SaveChoices',
  'DisableBots',
  'StartStack',
  'SoapDbc',
  'AhBot',
  'GameAccount',
  'StopStack',
  'WaitClient',
  'SwpSync',
  'EnableBots',
  'Launcher',
  'Addons',
] as const satisfies readonly ExpressProvisionPhase[]

export type ExpressPipelinePhase = (typeof EXPRESS_PIPELINE_PHASES)[number]

export const EXPRESS_PHASE_TITLE: Record<ExpressPipelinePhase, string> = {
  SaveChoices: 'Save Express choices',
  DisableBots: 'Disable playerbots',
  StartStack: 'Start the stack',
  SoapDbc: 'SOAP admin and DBC baseline',
  AhBot: 'Create Auction House bot',
  GameAccount: 'Create admin game account',
  StopStack: 'Stop the stack',
  SwpSync: 'Sync Server Wide Progression',
  EnableBots: 'Turn playerbots back on',
  Launcher: 'Build the launcher',
  WaitClient: 'Upload a client',
  Addons: 'Install addons',
}

export function expressPhaseStepId(phase: ExpressPipelinePhase): string {
  return `express-phase-${phase}`
}

export function expressPhaseLabel(phase?: ExpressProvisionPhase): string {
  if (!phase || phase === 'None' || phase === 'Done') {
    return EXPRESS_PHASE_TITLE.SaveChoices
  }
  if (phase in EXPRESS_PHASE_TITLE) {
    return EXPRESS_PHASE_TITLE[phase as ExpressPipelinePhase]
  }
  return 'the last step'
}

export function expressCurrentPhaseIndex(
  status?: string,
  phase?: ExpressProvisionPhase,
): number {
  if (status === 'Completed') {
    return EXPRESS_PIPELINE_PHASES.length
  }

  const current =
    !phase || phase === 'None' || phase === 'Done'
      ? 'SaveChoices'
      : phase
  const index = EXPRESS_PIPELINE_PHASES.indexOf(current as ExpressPipelinePhase)
  return index < 0 ? 0 : index
}

export function isExpressProvisionActive(status?: string): boolean {
  return status === 'Running' || status === 'WaitingForClient' || status === 'Failed'
}

const WAIT_CLIENT_INDEX = EXPRESS_PIPELINE_PHASES.indexOf('WaitClient')

/** True when this pipeline row should render for the current Express status. */
export function expressPipelineStepApplies(
  phase: ExpressPipelinePhase,
  status?: string,
  currentPhase?: ExpressProvisionPhase,
): boolean {
  if (!isExpressProvisionActive(status)) {
    return false
  }

  const index = EXPRESS_PIPELINE_PHASES.indexOf(phase)
  const current = expressCurrentPhaseIndex(status, currentPhase)
  if (current >= index) {
    return true
  }

  // After client upload (and while waiting for it), keep the remaining plan on screen so Continue
  // does not make SwpSync/Launcher vanish until those phases start.
  return current >= WAIT_CLIENT_INDEX && phase !== 'Addons'
}
