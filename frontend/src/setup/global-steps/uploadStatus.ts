import { STEP_IDS } from '@/setup/constants'
import { isStepDoneOrSkipped, type SetupProgressStore } from '@/setup/progress/setupProgressStore'
import type { SetupStepStatus } from '@/setup/types'

export function isClientUploadComplete(status: SetupStepStatus, progress: SetupProgressStore): boolean {
  return isStepDoneOrSkipped(STEP_IDS.uploadClient, status.client.dataUploaded, progress)
}

export function isArmoryDbcUploadComplete(status: SetupStepStatus, progress: SetupProgressStore): boolean {
  return isStepDoneOrSkipped(STEP_IDS.uploadArmoryDbc, status.armory.dbcUploaded, progress)
}

export function clientUploadApplies(status: SetupStepStatus): boolean {
  return !status.client.loading && status.client.containerRunning && !status.client.dataUploaded
}

export function armoryDbcUploadApplies(status: SetupStepStatus): boolean {
  return !status.armory.loading && status.armory.containerRunning && !status.armory.dbcUploaded
}
