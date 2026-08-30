import { useState, useEffect } from 'react'
import { X, Loader2, AlertCircle } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { moduleApi } from '@/services/api'
import type { ModuleConfigOption } from '@/types/module-config.types'
import { DynamicFormField } from '@/components/wizard/common/DynamicFormField'

interface ModuleConfigModalProps {
  selectedModuleIds: string[]
  moduleNames: Record<string, string> // moduleId -> moduleName mapping
  envVars: Record<string, string>
  onSave: (envVars: Record<string, string>) => void
  onClose: () => void
}

type TabId = string | 'custom'

export function ModuleConfigModal({
  selectedModuleIds,
  moduleNames,
  envVars,
  onSave,
  onClose,
}: ModuleConfigModalProps) {
  const [activeTab, setActiveTab] = useState<TabId>(selectedModuleIds[0] || 'custom')
  const [localEnvVars, setLocalEnvVars] = useState<Record<string, string>>(envVars)
  const [customPairs, setCustomPairs] = useState<Array<{ key: string; value: string }>>([])

  // Initialize local state ONLY on mount (when modal opens)
  // Don't reset when envVars prop changes during editing
  // The prop only updates after we save and close, then reopen
  useEffect(() => {
    setLocalEnvVars(envVars)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []) // Empty deps = only run on mount

  // Handle Escape key
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose()
      }
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [onClose])

  // Parse custom env vars (ones not from modules)
  useEffect(() => {
    const custom = Object.entries(envVars).filter(([key]) => {
      // Check if this key belongs to any module
      return !selectedModuleIds.some(moduleId => {
        // Match against actual module config keys
        // For playerbots: AC_AI_PLAYERBOT_* 
        // For autobalance: AC_AUTO_BALANCE_*
        // For transmog: AC_TRANSMOGRIFICATION_*
        // For ah-bot: AC_AUCTION_HOUSE_BOT_*
        
        // Generate patterns based on module ID
        if (moduleId === 'mod-playerbots') {
          return key.startsWith('AC_AI_PLAYERBOT_')
        }
        
        // Generic pattern for other modules
        const modulePart = moduleId.replace('mod-', '').replace(/-/g, '_').toUpperCase()
        return key.startsWith(`AC_${modulePart}_`)
      })
    }).map(([key, value]) => ({ key, value }))
    
    setCustomPairs(custom)
  }, [envVars, selectedModuleIds])

  const handleSave = () => {
    // localEnvVars already contains all module config values
    // customPairs contains truly custom (non-module) env vars
    // Merge them together
    const customEnv = customPairs.reduce<Record<string, string>>((acc, { key, value }) => {
      if (key.trim()) {
        acc[key.trim()] = value
      }
      return acc
    }, {})
    
    onSave({ ...localEnvVars, ...customEnv })
    onClose()
  }

  // Count unique env vars (localEnvVars already includes module vars, customPairs are separate)
  const envVarCount = Object.keys(localEnvVars).length

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="flex flex-col w-full max-w-4xl max-h-[90vh] rounded-lg bg-white shadow-xl">
        {/* Header */}
        <div className="flex-shrink-0 border-b border-gray-200 px-6 py-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-xl font-semibold text-gray-900">Module Configuration</h2>
              <p className="mt-1 text-sm text-gray-500">
                Configure modules or add custom environment variables
              </p>
            </div>
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600"
              aria-label="Close modal"
            >
              <X className="h-6 w-6" />
            </button>
          </div>
        </div>

        {/* Tabs */}
        <div className="flex-shrink-0 border-b border-gray-200 bg-gray-50">
          <div className="flex gap-1 overflow-x-auto px-6">
            {selectedModuleIds.map((moduleId) => (
              <button
                key={moduleId}
                onClick={() => setActiveTab(moduleId)}
                className={`whitespace-nowrap border-b-2 px-4 py-3 text-sm font-medium transition-colors ${
                  activeTab === moduleId
                    ? 'border-blue-600 text-blue-600'
                    : 'border-transparent text-gray-600 hover:border-gray-300 hover:text-gray-900'
                }`}
              >
                {moduleNames[moduleId] || moduleId}
              </button>
            ))}
            <button
              onClick={() => setActiveTab('custom')}
              className={`whitespace-nowrap border-b-2 px-4 py-3 text-sm font-medium transition-colors ${
                activeTab === 'custom'
                  ? 'border-blue-600 text-blue-600'
                  : 'border-transparent text-gray-600 hover:border-gray-300 hover:text-gray-900'
              }`}
            >
              Custom Variables
            </button>
          </div>
        </div>

        {/* Content */}
        <div className="flex-1 overflow-y-auto px-6 py-4">
          {/* Render all module tabs but only show active one */}
          {selectedModuleIds.map((moduleId) => (
            <div key={moduleId} className={activeTab === moduleId ? '' : 'hidden'}>
              <ModuleConfigTab
                moduleId={moduleId}
                envVars={localEnvVars}
                onChange={setLocalEnvVars}
              />
            </div>
          ))}
          
          {/* Custom variables tab */}
          <div className={activeTab === 'custom' ? '' : 'hidden'}>
            <CustomEnvVarsTab
              pairs={customPairs}
              onChange={setCustomPairs}
            />
          </div>
        </div>

        {/* Footer */}
        <div className="flex-shrink-0 border-t border-gray-200 bg-gray-50 px-6 py-4">
          <div className="flex items-center justify-between">
            <p className="text-sm text-gray-500">
              {envVarCount} environment variable{envVarCount !== 1 ? 's' : ''} configured
            </p>
            <div className="flex gap-3">
              <button
                onClick={onClose}
                className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                Cancel
              </button>
              <button
                onClick={handleSave}
                className="rounded-md border border-transparent bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                Save Configuration
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}

interface ModuleConfigTabProps {
  moduleId: string
  envVars: Record<string, string>
  onChange: (envVars: Record<string, string>) => void
}

function ModuleConfigTab({ moduleId, envVars, onChange }: ModuleConfigTabProps) {
  const { data: schema, isLoading, error } = useQuery({
    queryKey: ['moduleConfig', moduleId],
    queryFn: async () => {
      const response = await moduleApi.getConfig(moduleId)
      return response.data
    },
  })

  // Track which options are selected (to be added to custom variables)
  const [selectedOptions, setSelectedOptions] = useState<Set<string>>(() => {
    // Pre-select options that already have custom values
    return new Set(Object.keys(envVars))
  })

  // Sync selectedOptions when envVars changes (when modal reopens with existing values)
  useEffect(() => {
    setSelectedOptions(new Set(Object.keys(envVars)))
  }, [envVars])

  // Don't pre-populate defaults - only save values user actually changes
  // The form fields will show defaults via the ?? operator
  
  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-12">
        <Loader2 className="h-8 w-8 animate-spin text-blue-500" />
      </div>
    )
  }

  if (error) {
    return (
      <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
        Failed to load configuration for this module. It may not have a configuration file available.
      </div>
    )
  }

  if (!schema || schema.options.length === 0) {
    return (
      <div className="rounded-md border border-gray-200 bg-gray-50 px-4 py-3 text-sm text-gray-600">
        No configuration options available for this module.
      </div>
    )
  }

  const handleOptionChange = (option: ModuleConfigOption, value: string) => {
    // If option is selected, always save the value (even if it matches default)
    if (selectedOptions.has(option.envVarName)) {
      onChange({
        ...envVars,
        [option.envVarName]: value,
      })
    }
  }

  const toggleOption = (envVarName: string, option: ModuleConfigOption) => {
    const newSelected = new Set(selectedOptions)
    
    if (newSelected.has(envVarName)) {
      // Deselecting - remove from envVars
      newSelected.delete(envVarName)
      const newEnvVars = { ...envVars }
      delete newEnvVars[envVarName]
      onChange(newEnvVars)
    } else {
      // Selecting - add current value to envVars immediately
      newSelected.add(envVarName)
      const currentValue = envVars[envVarName] ?? option.defaultValue
      onChange({
        ...envVars,
        [envVarName]: currentValue,
      })
    }
    
    setSelectedOptions(newSelected)
  }

  const handleSelectAll = () => {
    const newSelected = new Set(schema.options.map(opt => opt.envVarName))
    setSelectedOptions(newSelected)
    
    // Add all current values (defaults or custom) to envVars
    const newEnvVars = { ...envVars }
    schema.options.forEach(option => {
      const currentValue = envVars[option.envVarName] ?? option.defaultValue
      newEnvVars[option.envVarName] = currentValue
    })
    onChange(newEnvVars)
  }

  const handleClearAll = () => {
    setSelectedOptions(new Set())
    
    // Remove only this module's env vars, keep others
    const newEnvVars = { ...envVars }
    schema.options.forEach(option => {
      delete newEnvVars[option.envVarName]
    })
    onChange(newEnvVars)
  }

  return (
    <div className="space-y-4">
      {/* Warning banner */}
      <div className="rounded-md border border-amber-200 bg-amber-50 px-4 py-3">
        <div className="flex items-start gap-3">
          <AlertCircle className="h-5 w-5 shrink-0 text-amber-600 mt-0.5" aria-hidden="true" />
          <div>
            <p className="text-sm font-medium text-amber-800">Advanced Configuration</p>
            <p className="mt-1 text-sm text-amber-700">
              These settings are for advanced users only. Changing default values may affect server stability and gameplay balance. Only modify settings if you understand their impact.
            </p>
          </div>
        </div>
      </div>
      
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600">
          {schema.options.length} configuration option{schema.options.length !== 1 ? 's' : ''} available
          {selectedOptions.size > 0 && (
            <span className="ml-2 font-medium text-blue-600">
              ({selectedOptions.size} selected)
            </span>
          )}
        </p>
        
        <div className="flex gap-2">
          <button
            type="button"
            onClick={handleSelectAll}
            className="rounded-md px-3 py-1 text-sm font-medium text-blue-600 hover:bg-blue-50"
          >
            Select All
          </button>
          <button
            type="button"
            onClick={handleClearAll}
            className="rounded-md px-3 py-1 text-sm font-medium text-gray-600 hover:bg-gray-50"
          >
            Clear All
          </button>
        </div>
      </div>
      
      <div className="space-y-4">
        {schema.options.map((option) => (
          <div key={option.envVarName} className="flex items-start gap-3">
            <input
              type="checkbox"
              checked={selectedOptions.has(option.envVarName)}
              onChange={() => toggleOption(option.envVarName, option)}
              className="mt-2 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            />
            <div className="flex-1">
              <DynamicFormField
                option={option}
                value={envVars[option.envVarName] ?? option.defaultValue}
                onChange={(value) => handleOptionChange(option, value)}
                disabled={!selectedOptions.has(option.envVarName)}
              />
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

interface CustomEnvVarsTabProps {
  pairs: Array<{ key: string; value: string }>
  onChange: (pairs: Array<{ key: string; value: string }>) => void
}

function CustomEnvVarsTab({ pairs, onChange }: CustomEnvVarsTabProps) {
  const addPair = () => {
    onChange([...pairs, { key: '', value: '' }])
  }

  const removePair = (index: number) => {
    onChange(pairs.filter((_, i) => i !== index))
  }

  const updatePair = (index: number, field: 'key' | 'value', value: string) => {
    onChange(
      pairs.map((pair, i) => (i === index ? { ...pair, [field]: value } : pair))
    )
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <p className="text-sm text-gray-600">
          Add custom environment variables that aren't module-specific
        </p>
        <button
          onClick={addPair}
          className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
        >
          Add Variable
        </button>
      </div>

      {pairs.length === 0 && (
        <div className="rounded-md border border-dashed border-gray-300 py-8 text-center text-sm text-gray-500">
          No custom variables. Click "Add Variable" to create one.
        </div>
      )}

      {pairs.length > 0 && (
        <div className="space-y-2">
          {pairs.map((pair, index) => (
            <div key={index} className="flex items-center gap-2">
              <input
                type="text"
                placeholder="VARIABLE_NAME"
                value={pair.key}
                onChange={(e) => updatePair(index, 'key', e.target.value)}
                className="flex-1 rounded-md border border-gray-300 bg-white px-3 py-2 font-mono text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
              <span className="text-gray-400">=</span>
              <input
                type="text"
                placeholder="value"
                value={pair.value}
                onChange={(e) => updatePair(index, 'value', e.target.value)}
                className="flex-1 rounded-md border border-gray-300 bg-white px-3 py-2 font-mono text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
              <button
                onClick={() => removePair(index)}
                className="rounded p-2 text-gray-400 hover:bg-red-50 hover:text-red-600 focus:outline-none focus:ring-2 focus:ring-red-500"
                aria-label="Remove variable"
              >
                <X className="h-4 w-4" />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
