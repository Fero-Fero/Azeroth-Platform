import type { UseFormReturn } from 'react-hook-form'
import type { WizardFormData } from '@/schemas/wizard.schemas'

export type WizardForm = Pick<
  UseFormReturn<WizardFormData>,
  'clearErrors' | 'formState' | 'getValues' | 'register' | 'reset' | 'setError' | 'setValue' | 'trigger' | 'watch'
>
