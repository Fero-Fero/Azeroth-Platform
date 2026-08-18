import { ExternalLink } from 'lucide-react'
import { useGlobalAddonCatalog } from '@/hooks/useAddons'

export function RecommendedAddonsNotice({ ids }: { ids: string[] }) {
  const { data: catalog, isLoading, isError } = useGlobalAddonCatalog()

  if (ids.length === 0) {
    return null
  }

  if (isLoading) {
    return <p className="text-violet-800">Loading recommended addons…</p>
  }

  if (isError) {
    return (
      <p className="text-red-800">
        Could not load the addon catalog. Recommended addons: {ids.join(', ')}.
      </p>
    )
  }

  return (
    <div className="space-y-2">
      {ids.map((id) => {
        const addon = catalog?.find((entry) => entry.id === id)
        if (!addon) {
          return (
            <p key={id} className="text-red-800">
              Addon catalog is missing <code className="rounded bg-red-100 px-1 text-xs">{id}</code>.
            </p>
          )
        }

        const href = addon.website || addon.downloadUrl
        return (
          <p key={id} className="text-violet-800">
            It is recommended to install <strong>{addon.name}</strong> from the <strong>Addons</strong> tab after creation
            - {addon.description}{' '}
            {href && (
              <a
                href={href}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 font-medium text-violet-700 underline hover:text-violet-900"
              >
                {addon.website ? 'Project page' : 'Download'}
                <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              </a>
            )}
          </p>
        )
      })}
    </div>
  )
}
