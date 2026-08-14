import { useQuery } from '@tanstack/react-query'
import { Loader2, Trash2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import type { CloudSshKeyDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'

interface SavedSshKeySelectorProps {
  selectedKeyId: string
  onSelectedKeyIdChange: (id: string) => void
  onSelectKey?: (key: CloudSshKeyDto | null) => void
  disabled?: boolean
}

export function SavedSshKeySelector({
  selectedKeyId,
  onSelectedKeyIdChange,
  onSelectKey,
  disabled = false,
}: SavedSshKeySelectorProps) {
  const { data: savedKeys, isLoading, refetch } = useQuery({
    queryKey: ['cloud-ssh-keys'],
    queryFn: async () => (await cloudApi.listSshKeys()).data,
  })

  const handleChange = (value: string) => {
    onSelectedKeyIdChange(value)
    if (!value) {
      onSelectKey?.(null)
      return
    }

    const match = savedKeys?.find((key) => key.id === value) ?? null
    onSelectKey?.(match)
  }

  const handleDelete = async (id: string) => {
    await cloudApi.deleteSshKey(id)
    if (selectedKeyId === id) {
      onSelectedKeyIdChange('')
      onSelectKey?.(null)
    }

    await refetch()
  }

  return (
    <div className="space-y-2">
      <label htmlFor="saved-ssh-key" className="block text-sm font-medium text-gray-800">
        Saved SSH key
      </label>
      <div className="flex flex-wrap items-center gap-2">
        <select
          id="saved-ssh-key"
          value={selectedKeyId}
          disabled={disabled || isLoading}
          onChange={(event) => handleChange(event.target.value)}
          className={cn(
            'min-w-[16rem] flex-1 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            disabled && 'opacity-60'
          )}
        >
          <option value="">Paste a new key below…</option>
          {(savedKeys ?? []).map((key) => (
            <option key={key.id} value={key.id}>
              {key.label} ({key.fingerprint})
            </option>
          ))}
        </select>
        {isLoading && <Loader2 className="h-4 w-4 animate-spin text-gray-500" aria-hidden="true" />}
        {selectedKeyId ? (
          <button
            type="button"
            disabled={disabled}
            onClick={() => void handleDelete(selectedKeyId)}
            className="inline-flex items-center gap-1 rounded-md border border-gray-300 px-2 py-1.5 text-xs text-gray-700 hover:bg-gray-50 disabled:opacity-60"
            title="Remove saved key from platform"
          >
            <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
            Delete
          </button>
        ) : null}
      </div>
      <p className="text-xs text-gray-500">
        Saved keys are encrypted on the platform. The private key is never shown again after saving.
      </p>
    </div>
  )
}
