import { useState } from 'react'
import { ChevronDown, ChevronRight, Plus, Trash2 } from 'lucide-react'
import { BooleanOptionRow, DynamicFormField, isEnvTruthy } from '@/components/wizard/common/DynamicFormField'
import { ConfigOptionType } from '@/types/moduleConfig'
import type { ServiceEnvOption, ServiceEnvTemplate, ServiceEnvVars } from '@/types/serviceEnv'

interface ServiceEnvVarsEditorProps {
  templates: ServiceEnvTemplate[]
  value: ServiceEnvVars
  onChange: (value: ServiceEnvVars) => void
  /** Optional case-insensitive search term; sections with no matching var are hidden and expanded. */
  filter?: string
}

/**
 * Per-service environment-variable editor. Environment variables are per-container, so each service
 * gets its own collapsible section with the variables it accepts (schema-driven fields) plus a
 * free-form custom escape hatch. Nothing is written for a service until the operator enables a
 * variable, so unchanged services stay on their managed defaults.
 */
export function ServiceEnvVarsEditor({ templates, value, onChange, filter }: ServiceEnvVarsEditorProps) {
  const setBucket = (serviceId: string, bucket: Record<string, string>) => {
    const next = { ...value }
    if (Object.keys(bucket).length === 0) {
      delete next[serviceId]
    } else {
      next[serviceId] = bucket
    }
    onChange(next)
  }

  return (
    <div className="space-y-3">
      {templates.map((template) => (
        <ServiceEnvSection
          key={template.serviceId}
          template={template}
          bucket={value[template.serviceId] ?? {}}
          onChange={(bucket) => setBucket(template.serviceId, bucket)}
          filter={filter}
        />
      ))}
    </div>
  )
}

interface ServiceEnvSectionProps {
  template: ServiceEnvTemplate
  bucket: Record<string, string>
  onChange: (bucket: Record<string, string>) => void
  /** Optional case-insensitive search term; when set, only matching vars render and the section auto-expands. */
  filter?: string
}

/** True when a template option matches the (already lower-cased) search term. */
function optionMatches(option: ServiceEnvOption, term: string): boolean {
  return (
    option.envVarName.toLowerCase().includes(term) ||
    option.key.toLowerCase().includes(term) ||
    option.description.toLowerCase().includes(term)
  )
}

/** True when a custom key/value pair matches the (already lower-cased) search term. */
function customMatches(key: string, val: string, term: string): boolean {
  return key.toLowerCase().includes(term) || val.toLowerCase().includes(term)
}

export function ServiceEnvSection({ template, bucket, onChange, filter }: ServiceEnvSectionProps) {
  const [open, setOpen] = useState(false)

  const term = (filter ?? '').trim().toLowerCase()
  const filtering = term.length > 0

  const templateKeys = new Set(template.options.map((o) => o.envVarName))
  const customPairs = Object.entries(bucket).filter(([key]) => !templateKeys.has(key))
  const setCount = Object.keys(bucket).length

  const visibleOptions = filtering
    ? template.options.filter((o) => optionMatches(o, term))
    : template.options
  const visibleCustomPairs = filtering
    ? customPairs.filter(([key, val]) => customMatches(key, val, term))
    : customPairs

  // While searching, hide sections with no matching variable so the results narrow down.
  if (filtering && visibleOptions.length === 0 && visibleCustomPairs.length === 0) {
    return null
  }

  const expanded = filtering || open

  const setVar = (key: string, val: string) => onChange({ ...bucket, [key]: val })
  const removeVar = (key: string) => {
    const next = { ...bucket }
    delete next[key]
    onChange(next)
  }

  const toggleOption = (option: ServiceEnvOption) => {
    if (option.envVarName in bucket) {
      removeVar(option.envVarName)
    } else {
      setVar(option.envVarName, option.defaultValue)
    }
  }

  return (
    <div className="rounded-lg border border-gray-200">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        disabled={filtering}
        className="flex w-full items-center justify-between gap-3 px-4 py-3 text-left disabled:cursor-default"
      >
        <span className="flex items-center gap-2">
          {expanded ? (
            <ChevronDown className="h-4 w-4 text-gray-400" />
          ) : (
            <ChevronRight className="h-4 w-4 text-gray-400" />
          )}
          <span className="text-sm font-medium text-gray-800">{template.serviceName}</span>
          {setCount > 0 && (
            <span className="rounded-full bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-700">
              {setCount} set
            </span>
          )}
        </span>
      </button>

      {expanded && (
        <div className="space-y-4 border-t border-gray-100 px-4 py-4">
          {!filtering && <p className="wrap-break-word text-xs text-gray-500">{template.description}</p>}

          {visibleOptions.length > 0 && (
            <div className="space-y-3">
              {visibleOptions.map((option) => {
                // Plain on/off flags get a single checkbox: one click enables them. We only store the
                // value when it differs from the template default, so unchecking a default-off flag (or
                // re-checking a default-on one) simply drops the override.
                if (option.type === ConfigOptionType.Boolean) {
                  const defaultOn = isEnvTruthy(option.defaultValue)
                  const current = option.envVarName in bucket
                    ? isEnvTruthy(bucket[option.envVarName])
                    : defaultOn
                  return (
                    <BooleanOptionRow
                      key={option.envVarName}
                      option={option}
                      checked={current}
                      onToggle={(next) => {
                        if (next === defaultOn) removeVar(option.envVarName)
                        else setVar(option.envVarName, next ? '1' : '0')
                      }}
                    />
                  )
                }

                const enabled = option.envVarName in bucket
                return (
                  <div key={option.envVarName} className="flex items-start gap-3">
                    <input
                      type="checkbox"
                      checked={enabled}
                      onChange={() => toggleOption(option)}
                      className="mt-2 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                      aria-label={`Enable ${option.key}`}
                    />
                    <div className="min-w-0 flex-1">
                      <DynamicFormField
                        option={option}
                        value={bucket[option.envVarName] ?? option.defaultValue}
                        onChange={(val) => setVar(option.envVarName, val)}
                        disabled={!enabled}
                      />
                    </div>
                  </div>
                )
              })}
            </div>
          )}

          <CustomVars
            pairs={visibleCustomPairs}
            hideAdd={filtering}
            onAdd={() => setVar('', '')}
            onUpdateKey={(oldKey, newKey) => {
              const next = { ...bucket }
              const v = next[oldKey]
              delete next[oldKey]
              next[newKey] = v
              onChange(next)
            }}
            onUpdateValue={(key, val) => setVar(key, val)}
            onRemove={(key) => removeVar(key)}
          />
        </div>
      )}
    </div>
  )
}

interface CustomVarsProps {
  pairs: Array<[string, string]>
  onAdd: () => void
  onUpdateKey: (oldKey: string, newKey: string) => void
  onUpdateValue: (key: string, value: string) => void
  onRemove: (key: string) => void
  /** Hide the Add button (e.g. while a search filter is active). */
  hideAdd?: boolean
}

function CustomVars({ pairs, onAdd, onUpdateKey, onUpdateValue, onRemove, hideAdd }: CustomVarsProps) {
  return (
    <div className="rounded-md border border-dashed border-gray-200 p-3">
      <div className="mb-2 flex items-center justify-between">
        <span className="text-xs font-medium text-gray-600">Custom variables</span>
        {!hideAdd && (
          <button
            type="button"
            onClick={onAdd}
            className="inline-flex items-center gap-1 rounded-md border border-gray-300 bg-white px-2 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
          >
            <Plus className="h-3 w-3" />
            Add
          </button>
        )}
      </div>
      {pairs.length === 0 && (
        <p className="text-xs text-gray-400">No custom variables for this service.</p>
      )}
      {pairs.length > 0 && (
        <ul className="space-y-2">
          {pairs.map(([key, val], index) => (
            <li key={index} className="flex items-center gap-2">
              <input
                type="text"
                placeholder="KEY"
                value={key}
                onChange={(e) => onUpdateKey(key, e.target.value)}
                className="min-w-0 flex-1 rounded-md border border-gray-300 px-3 py-1.5 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
              <span className="text-sm text-gray-400">=</span>
              <input
                type="text"
                placeholder="value"
                value={val}
                onChange={(e) => onUpdateValue(key, e.target.value)}
                className="min-w-0 flex-1 rounded-md border border-gray-300 px-3 py-1.5 font-mono text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
              <button
                type="button"
                onClick={() => onRemove(key)}
                className="shrink-0 rounded p-1 text-gray-400 hover:bg-red-50 hover:text-red-600"
                aria-label="Remove variable"
              >
                <Trash2 className="h-4 w-4" />
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
