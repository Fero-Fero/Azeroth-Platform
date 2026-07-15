/** Matches the backend ConfigMigrationMode enum names (bound from the query string). */
export type ConfigMigrationMode = 'Skip' | 'Merge' | 'Fresh'

interface ConfigMigrationModeChoiceProps {
  value: ConfigMigrationMode
  onChange: (mode: ConfigMigrationMode) => void
  disabled?: boolean
}

const OPTIONS: Array<{ id: ConfigMigrationMode; label: string; description: string }> = [
  {
    id: 'Merge',
    label: 'Merge & preserve my settings',
    description:
      'Start from the new version defaults and keep your existing values for every setting that still exists. New settings use their defaults; settings removed in the new version are dropped.',
  },
  {
    id: 'Fresh',
    label: 'Reset to new defaults',
    description:
      'Discard my server.conf edits and use the new version defaults. Managed values (database, ports, realmlist IP) are re-applied automatically like during initial setup.',
  },
]

/**
 * Lets the operator choose how existing worldserver/authserver/module .conf files are reconciled with
 * the freshly built version when a stack is updated or rebuilt. AzerothCore adds/removes config keys
 * between versions, so a plain carry-over would leave new keys missing and removed keys lingering.
 */
export default function ConfigMigrationModeChoice({ value, onChange, disabled }: ConfigMigrationModeChoiceProps) {
  return (
    <fieldset className="space-y-2" disabled={disabled}>
      <legend className="mb-1 text-sm font-medium text-gray-900">Server configuration</legend>
      {OPTIONS.map((opt) => (
        <label
          key={opt.id}
          className={`flex cursor-pointer items-start gap-3 rounded-md border p-3 transition ${
            value === opt.id ? 'border-blue-500 bg-blue-50' : 'border-gray-200 hover:bg-gray-50'
          } ${disabled ? 'cursor-not-allowed opacity-60' : ''}`}
        >
          <input
            type="radio"
            name="config-migration-mode"
            value={opt.id}
            checked={value === opt.id}
            onChange={() => onChange(opt.id)}
            className="mt-1 h-4 w-4 border-gray-300 text-blue-600 focus:ring-blue-500"
          />
          <span className="min-w-0">
            <span className="block text-sm font-medium text-gray-800">{opt.label}</span>
            <span className="mt-0.5 block text-xs text-gray-500">{opt.description}</span>
          </span>
        </label>
      ))}
    </fieldset>
  )
}
