import { soapAdminStep } from '@/setup/global-steps/soapAdminStep'
import { dbcBaselineStep } from '@/setup/global-steps/dbcBaselineStep'
import { uploadArmoryDbcStep } from '@/setup/global-steps/uploadArmoryDbcStep'
import { uploadClientStep } from '@/setup/global-steps/uploadClientStep'
import type { SetupStep } from '@/setup/types'

/** Fixed onboarding order - do not reorder without updating docs and tests. */
export const globalSteps: SetupStep[] = [
  soapAdminStep(),
  dbcBaselineStep(),
  uploadClientStep(),
  uploadArmoryDbcStep(),
]
