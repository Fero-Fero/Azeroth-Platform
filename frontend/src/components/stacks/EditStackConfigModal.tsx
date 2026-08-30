import { useState, useCallback } from 'react'
import { X, Save, Loader2, ChevronDown, ChevronRight } from 'lucide-react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { StackDetailsDto, StackConfigurationDto } from '@/types/stack.types'
import { stackApi, validationApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { useServiceEnvTemplates } from '@/hooks/useModules'
import { ServiceEnvVarsEditor } from '@/components/wizard/ServiceEnvVarsEditor'
import type { ServiceEnvVars } from '@/types/service-env.types'

interface EditStackConfigModalProps {
  stack: StackDetailsDto
  onClose: () => void
}

export default function EditStackConfigModal({ stack, onClose }: EditStackConfigModalProps) {
  const queryClient = useQueryClient()
  const [config, setConfig] = useState<StackConfigurationDto>(stack.configuration)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [isValidating, setIsValidating] = useState(false)
  const [expandedSections, setExpandedSections] = useState<Record<string, boolean>>({
    ports: true,
    database: true,
    advanced: true,
    environment: false,
  })

  // Fetch per-service env templates
  const { data: envTemplates } = useServiceEnvTemplates()

  const serviceEnvVars: ServiceEnvVars = config.advanced.serviceEnvVars ?? {}
  const envVarCount = Object.values(serviceEnvVars).reduce(
    (sum, bucket) => sum + Object.keys(bucket ?? {}).length,
    0,
  )

  const setServiceEnvVars = useCallback((next: ServiceEnvVars) => {
    setConfig((prev) => ({ ...prev, advanced: { ...prev.advanced, serviceEnvVars: next } }))
  }, [])

  const updateMutation = useMutation({
    mutationFn: () =>
      stackApi.updateConfig(stack.stackId, {
        ...config,
        advanced: { ...config.advanced },
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      onClose()
    },
    onError: (error: any) => {
      if (error.response?.data?.errors) {
        const validationErrors: Record<string, string> = {}
        error.response.data.errors.forEach((err: any) => {
          validationErrors[err.field] = err.message
        })
        setErrors(validationErrors)
      }
    },
  })

  const handleSave = async () => {
    setErrors({})
    setIsValidating(true)

    try {
      const validationResult = await validationApi.validate(config, stack.stackId)
      if (!validationResult.data.isValid) {
        const validationErrors: Record<string, string> = {}
        validationResult.data.errors.forEach((err) => {
          validationErrors[err.field] = err.message
        })
        setErrors(validationErrors)
        return
      }

      updateMutation.mutate()
    } catch (error) {
      console.error('Validation error:', error)
    } finally {
      setIsValidating(false)
    }
  }

  const toggleSection = (section: string) => {
    setExpandedSections(prev => ({
      ...prev,
      [section]: !prev[section]
    }))
  }

  const isProcessing = isValidating || updateMutation.isPending

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="w-full max-w-3xl max-h-[90vh] overflow-y-auto rounded-lg bg-white shadow-xl">
        {/* Header */}
        <div className="border-b border-gray-200 px-6 py-4 sticky top-0 bg-white">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-xl font-semibold text-gray-900">Edit Configuration</h2>
              <p className="mt-1 text-sm text-gray-500">{stack.stackName}</p>
            </div>
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600"
              disabled={isProcessing}
            >
              <X className="h-6 w-6" />
            </button>
          </div>
        </div>

        {/* Form */}
        <div className="space-y-2 p-6">
          {/* Ports Section */}
          <CollapsibleSection 
            title="Ports" 
            expanded={expandedSections.ports}
            onToggle={() => toggleSection('ports')}
          >
            <div className="grid grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Auth Server Port</label>
                <input type="number" value={config.ports.authServer} onChange={(e) => setConfig({...config, ports: {...config.ports, authServer: parseInt(e.target.value) || 0}})} className={`w-full rounded-md border ${errors['ports.authServer'] ? 'border-red-500' : 'border-gray-300'} px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500`} />
                {errors['ports.authServer'] && <p className="mt-1 text-sm text-red-600">{errors['ports.authServer']}</p>}
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">World Server Port</label>
                <input type="number" value={config.ports.worldServer} onChange={(e) => setConfig({...config, ports: {...config.ports, worldServer: parseInt(e.target.value) || 0}})} className={`w-full rounded-md border ${errors['ports.worldServer'] ? 'border-red-500' : 'border-gray-300'} px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500`} />
                {errors['ports.worldServer'] && <p className="mt-1 text-sm text-red-600">{errors['ports.worldServer']}</p>}
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">SOAP Port</label>
                <input type="number" value={config.ports.soapPort} onChange={(e) => setConfig({...config, ports: {...config.ports, soapPort: parseInt(e.target.value) || 0}})} className={`w-full rounded-md border ${errors['ports.soapPort'] ? 'border-red-500' : 'border-gray-300'} px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500`} />
                {errors['ports.soapPort'] && <p className="mt-1 text-sm text-red-600">{errors['ports.soapPort']}</p>}
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Database Port</label>
                <input type="number" value={config.database.port} onChange={(e) => setConfig({...config, database: {...config.database, port: parseInt(e.target.value) || 0}})} className={`w-full rounded-md border ${errors['database.port'] ? 'border-red-500' : 'border-gray-300'} px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500`} />
                {errors['database.port'] && <p className="mt-1 text-sm text-red-600">{errors['database.port']}</p>}
              </div>
            </div>
          </CollapsibleSection>

          {/* Database Section */}
          <CollapsibleSection title="Database" expanded={expandedSections.database} onToggle={() => toggleSection('database')}>
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Root Password</label>
              <input type="password" value={config.database.rootPassword} placeholder="Leave blank to keep current password" onChange={(e) => setConfig({...config, database: {...config.database, rootPassword: e.target.value}})} className="w-full rounded-md border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
              <p className="mt-1 text-sm text-gray-500">For security the current password is not shown; leave blank to keep it. Changing it requires manual database updates.</p>
            </div>
          </CollapsibleSection>

          {/* Server Settings */}
          <CollapsibleSection title="Server Settings" expanded={expandedSections.advanced} onToggle={() => toggleSection('advanced')}>
            <div className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Realm Name</label>
                <input type="text" value={config.advanced.realmName} onChange={(e) => setConfig({...config, advanced: {...config.advanced, realmName: e.target.value}})} className="w-full rounded-md border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Max Players</label>
                <input type="number" value={config.advanced.maxPlayers} onChange={(e) => setConfig({...config, advanced: {...config.advanced, maxPlayers: parseInt(e.target.value) || 100}})} className="w-full rounded-md border border-gray-300 px-3 py-2 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500" />
              </div>
            </div>
          </CollapsibleSection>

          {/* Environment Variables & Module Settings */}
          <CollapsibleSection title={`Environment Variables (${envVarCount})`} expanded={expandedSections.environment} onToggle={() => toggleSection('environment')}>
            <div className="space-y-3">
              <p className="text-xs text-gray-500">
                Environment variables are per-container. Compile-time module selection lives under Server → Modules.
              </p>

              <ServiceEnvVarsEditor
                templates={envTemplates ?? []}
                value={serviceEnvVars}
                onChange={setServiceEnvVars}
              />
            </div>
          </CollapsibleSection>

          <div className="rounded-md border border-gray-200 bg-gray-50 p-4 mt-6">
            <p className="text-sm font-medium text-gray-700 mb-2">Important Notes:</p>
            <ul className="list-inside list-disc space-y-1 text-sm text-gray-600">
              <li>Stack name and server type cannot be changed</li>
              <li>The stack will be restarted after saving (if running)</li>
              <li>Compiled modules are managed from Server → Modules</li>
            </ul>
          </div>
        </div>

        {/* Footer */}
        <div className="flex items-center justify-end gap-3 border-t border-gray-200 px-6 py-4 sticky bottom-0 bg-white">
          <button onClick={onClose} disabled={isProcessing} className="rounded-md bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-200 disabled:opacity-50">Cancel</button>
          <button onClick={handleSave} disabled={isProcessing} className="flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50">
            {isProcessing ? (<><Loader2 className="h-4 w-4 animate-spin" />{isValidating ? 'Validating...' : 'Saving...'}</>) : (<><Save className="h-4 w-4" />Save Changes</>)}
          </button>
        </div>

      </div>
    </div>
  )
}

function CollapsibleSection({ title, expanded, onToggle, children }: { title: string; expanded: boolean; onToggle: () => void; children: React.ReactNode }) {
  return (
    <div className="border border-gray-200 rounded-lg">
      <button onClick={onToggle} className="w-full flex items-center justify-between px-4 py-3 bg-gray-50 hover:bg-gray-100 transition">
        <h3 className="font-medium text-gray-900">{title}</h3>
        {expanded ? <ChevronDown className="h-5 w-5 text-gray-400" /> : <ChevronRight className="h-5 w-5 text-gray-400" />}
      </button>
      {expanded && <div className="px-4 py-3 border-t border-gray-200 space-y-4">{children}</div>}
    </div>
  )
}
