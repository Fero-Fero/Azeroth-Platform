import { useState } from 'react'
import { Plus, X } from 'lucide-react'
import { ConfigOptionType } from '@/types/module-config.types'

/**
 * Minimal shape shared by ModuleConfigOption and ServiceEnvOption so this schema-driven field can
 * render env vars for both modules and stack services.
 */
export interface DynamicFieldOption {
  key: string
  envVarName: string
  defaultValue: string
  type: ConfigOptionType
  description: string
  enumOptions?: string[] | null
}

interface DynamicFormFieldProps {
  option: DynamicFieldOption
  value: string
  onChange: (value: string) => void
  disabled?: boolean
}

/** Renders the appropriate input for a schema-defined environment variable, with label + description. */
export function DynamicFormField({ option, value, onChange, disabled }: DynamicFormFieldProps) {
  const renderInput = () => {
    switch (option.type) {
      case ConfigOptionType.Boolean:
        return (
          <label className={`flex items-center gap-2 ${disabled ? 'opacity-50' : ''}`}>
            <input
              type="checkbox"
              checked={value === '1' || value.toLowerCase() === 'true'}
              onChange={(e) => onChange(e.target.checked ? '1' : '0')}
              disabled={disabled}
              className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed"
            />
            <span className="text-sm text-gray-700">Enabled</span>
          </label>
        )

      case ConfigOptionType.Number:
        return (
          <input
            type="number"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            disabled={disabled}
            className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
          />
        )

      case ConfigOptionType.Enum:
        if (option.enumOptions && option.enumOptions.length > 0) {
          return (
            <select
              value={value}
              onChange={(e) => onChange(e.target.value)}
              disabled={disabled}
              className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
            >
              {option.enumOptions.map((opt) => {
                const [val, label] = opt.split('=').map((s) => s.trim())
                return (
                  <option key={val} value={val}>
                    {label || val}
                  </option>
                )
              })}
            </select>
          )
        }
        return (
          <input
            type="text"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            disabled={disabled}
            className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
          />
        )

      case ConfigOptionType.StringList:
        return <IdListInput value={value} onChange={onChange} disabled={disabled} />

      case ConfigOptionType.String:
      default:
        return (
          <input
            type="text"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            disabled={disabled}
            className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
          />
        )
    }
  }

  return (
    <div className="rounded-md border border-gray-200 bg-gray-50 p-4">
      <div className="mb-2 flex items-start justify-between gap-3">
        <div className="min-w-0 flex-1">
          <label className="block text-sm font-medium text-gray-900">{option.key}</label>
          {option.description && (
            <p className="mt-1 wrap-break-word text-xs text-gray-600 leading-relaxed">{option.description}</p>
          )}
        </div>
        <code className="shrink-0 rounded border border-blue-200 bg-blue-50 px-2 py-1 font-mono text-xs text-blue-700">
          {option.envVarName}
        </code>
      </div>
      <div className="mt-3">{renderInput()}</div>
    </div>
  )
}

/** True when a stored env-var string represents an enabled boolean ("1" / "true"). */
export function isEnvTruthy(value: string): boolean {
  return value === '1' || value.toLowerCase() === 'true'
}

interface BooleanOptionRowProps {
  option: DynamicFieldOption
  checked: boolean
  onToggle: (checked: boolean) => void
  disabled?: boolean
}

/**
 * A boolean env-var rendered as a single checkbox row (label + description + var name). Used instead of
 * the generic "enable this override" checkbox + value control combo, because for a plain on/off flag one
 * click should be all it takes to turn it on.
 */
export function BooleanOptionRow({ option, checked, onToggle, disabled }: BooleanOptionRowProps) {
  return (
    <label
      className={`flex items-start gap-3 rounded-md border border-gray-200 bg-gray-50 p-4 ${
        disabled ? 'opacity-60' : 'cursor-pointer'
      }`}
    >
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onToggle(e.target.checked)}
        disabled={disabled}
        className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed"
      />
      <div className="min-w-0 flex-1">
        <div className="flex items-start justify-between gap-3">
          <span className="text-sm font-medium text-gray-900">{option.key}</span>
          <code className="shrink-0 rounded border border-blue-200 bg-blue-50 px-2 py-1 font-mono text-xs text-blue-700">
            {option.envVarName}
          </code>
        </div>
        {option.description && (
          <p className="mt-1 wrap-break-word text-xs text-gray-600 leading-relaxed">{option.description}</p>
        )}
      </div>
    </label>
  )
}

interface IdListInputProps {
  value: string
  onChange: (value: string) => void
  disabled?: boolean
}

/** Comma-separated ID/GUID list editor rendered as removable chips plus an add box. */
export function IdListInput({ value, onChange, disabled }: IdListInputProps) {
  const [inputValue, setInputValue] = useState('')

  const ids = value
    ? value.split(',').map((s) => s.trim()).filter(Boolean)
    : []

  const addId = () => {
    const trimmed = inputValue.trim()
    if (!trimmed || ids.includes(trimmed)) return
    onChange([...ids, trimmed].join(','))
    setInputValue('')
  }

  const removeId = (id: string) => {
    onChange(ids.filter((i) => i !== id).join(','))
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault()
      addId()
    }
  }

  return (
    <div className={disabled ? 'pointer-events-none opacity-50' : ''}>
      <div className="mb-2 flex flex-wrap gap-1.5">
        {ids.map((id) => (
          <span
            key={id}
            className="inline-flex items-center gap-1 rounded bg-blue-100 px-2 py-0.5 font-mono text-xs font-medium text-blue-800"
          >
            {id}
            <button
              type="button"
              onClick={() => removeId(id)}
              className="text-blue-500 hover:text-blue-800"
              aria-label={`Remove ${id}`}
            >
              <X className="h-3 w-3" />
            </button>
          </span>
        ))}
        {ids.length === 0 && <span className="text-xs italic text-gray-400">No IDs added</span>}
      </div>
      <div className="flex gap-2">
        <input
          type="text"
          value={inputValue}
          onChange={(e) => setInputValue(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder="Enter GUID and press Enter"
          className="flex-1 rounded-md border border-gray-300 bg-white px-3 py-2 font-mono text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
        />
        <button
          type="button"
          onClick={addId}
          disabled={!inputValue.trim()}
          className="inline-flex items-center gap-1 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-40"
        >
          <Plus className="h-4 w-4" />
          Add
        </button>
      </div>
    </div>
  )
}
