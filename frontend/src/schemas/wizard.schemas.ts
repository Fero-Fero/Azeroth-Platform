import { z } from 'zod'
import { ServerType, DeploymentTarget } from '@/types/stack.types'
import { DEFAULT_ARMORY_EMAIL } from '@/lib/armory-email-defaults'

export const serverConfigSchema = z.object({
  stackName: z
    .string()
    .min(2, 'Stack name must be at least 2 characters')
    .max(64, 'Stack name must be at most 64 characters')
    .regex(
      /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/,
      'Lowercase letters, numbers, hyphens only; cannot start or end with a hyphen'
    ),
  serverType: z.nativeEnum(ServerType),
  // Only used when the selected server type allows a custom repository (e.g. Custom).
  customFork: z
    .object({
      repositoryUrl: z.string().optional().default(''),
      branch: z.string().optional().default(''),
    })
    .optional()
    .default({ repositoryUrl: '', branch: 'master' }),
})

export const modulesSchema = z.object({
  moduleIds: z.array(z.string()),
})

export const databaseSchema = z.object({
  database: z.object({
    rootPassword: z.string().min(8, 'Password must be at least 8 characters'),
    port: z.coerce
      .number()
      .int('Port must be a whole number')
      .min(1024, 'Port must be 1024 or higher')
      .max(65535, 'Port must be 65535 or lower'),
  }),
})

export const portsSchema = z.object({
  ports: z
    .object({
      authServer: z.coerce.number().int().min(1024).max(65535),
      worldServer: z.coerce.number().int().min(1024).max(65535),
      soapPort: z.coerce.number().int().min(1024).max(65535),
    })
    .refine(
      (ports) => new Set([ports.authServer, ports.worldServer, ports.soapPort]).size === 3,
      { message: 'All ports must be unique', path: ['authServer'] }
    ),
})

export const advancedSchema = z.object({
  advanced: z.object({
    maxPlayers: z.coerce
      .number()
      .int()
      .min(1, 'At least 1 player required')
      .max(10000, 'Maximum 10,000 players'),
    realmName: z.string().min(1, 'Realm name is required').max(64),
    realmlistHost: z.string().max(255).optional(),
    customEnvVars: z.record(z.string(), z.string()).optional(),
    serviceEnvVars: z.record(z.string(), z.record(z.string(), z.string())).optional(),
  }),
})

export const deploymentSchema = z.object({
  deployment: z
    .object({
      target: z.nativeEnum(DeploymentTarget),
      externalHost: z.string().max(255).optional().default(''),
      externalSshPort: z.coerce.number().int().min(1).max(65535).default(22),
      externalSshUser: z.string().max(64).optional().default(''),
      externalSshPrivateKey: z.string().optional().default(''),
    })
    .superRefine((deployment, ctx) => {
      if (deployment.target === DeploymentTarget.External) {
        if (!deployment.externalHost?.trim()) {
          ctx.addIssue({ code: z.ZodIssueCode.custom, message: 'Remote host is required for external stacks', path: ['externalHost'] })
        }
        if (!deployment.externalSshUser?.trim()) {
          ctx.addIssue({ code: z.ZodIssueCode.custom, message: 'SSH user is required for external stacks', path: ['externalSshUser'] })
        }
        if (!deployment.externalSshPrivateKey?.trim()) {
          ctx.addIssue({ code: z.ZodIssueCode.custom, message: 'SSH private key is required for external stacks', path: ['externalSshPrivateKey'] })
        }
      }
    }),
})

export const armoryEmailSchema = z.object({
  smtpHost: z.string(),
  smtpPort: z.coerce.number().int().min(1).max(65535),
  smtpSecurity: z.enum(['none', 'starttls', 'tls']),
  smtpUsername: z.string(),
  smtpPassword: z.string(),
  fromAddress: z.string(),
  fromName: z.string(),
  verificationSubject: z.string(),
  verificationBodyHtml: z.string(),
})

export const armoryAccountsSchema = z.object({
  armoryAccounts: z.object({
    useEmailConfirmation: z.boolean(),
    emailConfigured: z.boolean(),
    email: armoryEmailSchema.optional().nullable(),
  }),
})

export const wizardSchema = serverConfigSchema
  .merge(modulesSchema)
  .merge(databaseSchema)
  .merge(portsSchema)
  .merge(advancedSchema)
  .merge(deploymentSchema)
  .merge(armoryAccountsSchema)
  .superRefine((values, ctx) => {
    // The Custom server type builds from a user-supplied fork; require a valid http(s) URL for it.
    if (values.serverType === ServerType.Custom) {
      const url = values.customFork?.repositoryUrl?.trim() ?? ''
      if (!url) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'A repository URL is required for a custom fork',
          path: ['customFork', 'repositoryUrl'],
        })
      } else if (!/^https?:\/\/.+/i.test(url)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'Enter a valid http(s) repository URL',
          path: ['customFork', 'repositoryUrl'],
        })
      }
    }

    if (values.armoryAccounts.useEmailConfirmation && values.armoryAccounts.emailConfigured) {
      const email = values.armoryAccounts.email
      if (!email?.smtpHost?.trim()) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'SMTP host is required',
          path: ['armoryAccounts', 'email', 'smtpHost'],
        })
      }
      if (!email?.fromAddress?.trim()) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'From address is required',
          path: ['armoryAccounts', 'email', 'fromAddress'],
        })
      } else if (!email.fromAddress.includes('@')) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'Enter a valid email address',
          path: ['armoryAccounts', 'email', 'fromAddress'],
        })
      }
      if (!email?.verificationSubject?.trim()) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'Verification subject is required',
          path: ['armoryAccounts', 'email', 'verificationSubject'],
        })
      }
      if (!email?.verificationBodyHtml?.trim()) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: 'Verification email body is required',
          path: ['armoryAccounts', 'email', 'verificationBodyHtml'],
        })
      }
    }
  })

export type WizardFormData = z.infer<typeof wizardSchema>

export const WIZARD_DEFAULTS: WizardFormData = {
  stackName: '',
  serverType: ServerType.Standard,
  customFork: { repositoryUrl: '', branch: 'master' },
  moduleIds: [],
  database: { rootPassword: '', port: 3306 },
  ports: { authServer: 3724, worldServer: 8085, soapPort: 7878 },
  advanced: { maxPlayers: 100, realmName: 'AzerothCore', realmlistHost: '', customEnvVars: {}, serviceEnvVars: {} },
  deployment: {
    target: DeploymentTarget.Local,
    externalHost: '',
    externalSshPort: 22,
    externalSshUser: '',
    externalSshPrivateKey: '',
  },
  armoryAccounts: {
    useEmailConfirmation: false,
    emailConfigured: false,
    email: { ...DEFAULT_ARMORY_EMAIL },
  },
}

export const STEP_TRIGGER_FIELDS_BY_ID: Record<string, Array<keyof WizardFormData>> = {
  'server-config': ['stackName', 'serverType', 'customFork'],
  modules: ['moduleIds'],
  database: ['database'],
  ports: ['ports'],
  advanced: ['advanced', 'deployment', 'armoryAccounts'],
  email: ['armoryAccounts'],
  review: [],
}

export const EMAIL_STEP_TRIGGER_FIELDS = [
  'armoryAccounts.email.smtpHost',
  'armoryAccounts.email.smtpPort',
  'armoryAccounts.email.smtpSecurity',
  'armoryAccounts.email.fromAddress',
  'armoryAccounts.email.verificationSubject',
  'armoryAccounts.email.verificationBodyHtml',
] as const
