import { useRef } from 'react'
import { Upload } from 'lucide-react'
import { FormField } from '@/components/wizard/common/FormField'
import { cn } from '@/lib/utils'

interface SshPrivateKeyFieldProps {
  id: string
  value: string
  onChange: (value: string) => void
  error?: string
  hint?: string
  required?: boolean
}

export function SshPrivateKeyField({
  id,
  value,
  onChange,
  error,
  hint,
  required,
}: SshPrivateKeyFieldProps) {
  const fileInputRef = useRef<HTMLInputElement>(null)

  const handleFileSelect = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0]
    if (!file) {
      return
    }

    const reader = new FileReader()
    reader.onload = () => {
      onChange(String(reader.result ?? ''))
    }
    reader.readAsText(file)
    event.target.value = ''
  }

  return (
    <FormField
      label="SSH Private Key"
      htmlFor={id}
      error={error}
      hint={hint}
      required={required}
    >
      <textarea
        id={id}
        rows={5}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
        className={cn(
          'block w-full rounded-md border px-3 py-2 font-mono text-xs shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
          error ? 'border-red-400' : 'border-gray-300'
        )}
      />
      <div className="mt-2">
        <input
          ref={fileInputRef}
          type="file"
          accept=".pem,.key,.ppk,text/plain"
          className="sr-only"
          onChange={handleFileSelect}
          aria-label="Select SSH private key file"
        />
        <button
          type="button"
          onClick={() => fileInputRef.current?.click()}
          className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
        >
          <Upload className="h-3.5 w-3.5" aria-hidden="true" />
          Select key file from this machine
        </button>
      </div>
    </FormField>
  )
}
