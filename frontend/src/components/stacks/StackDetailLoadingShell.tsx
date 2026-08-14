import { Link } from 'react-router-dom'
import { Loader2 } from 'lucide-react'
import StackConnectionBanner from '@/components/stacks/StackConnectionBanner'
import StackRefreshBar from '@/components/stacks/StackRefreshBar'
import { DeploymentTarget, type StackDetailsDto } from '@/types/stack.types'

export default function StackDetailLoadingShell({
  stackId,
  stack,
  isRefreshing,
}: {
  stackId: string
  stack?: StackDetailsDto | null
  isRefreshing?: boolean
}) {
  const isExternal = stack?.configuration.deployment?.target === DeploymentTarget.External
  const title = stack?.stackName ?? 'Loading stack…'

  return (
    <div className="max-w-7xl mx-auto">
      <StackRefreshBar
        active={isRefreshing ?? true}
        className="mb-4"
        label={stack ? 'Updating stack details…' : 'Loading stack details…'}
      />

      <div className="mb-6">
        <Link to="/stacks" className="text-sm text-blue-600 hover:text-blue-800 mb-2 inline-block">
          ← Back to Stacks
        </Link>
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="text-3xl font-bold text-gray-900">{title}</h1>
            <p className="mt-1 text-sm text-gray-500">
              {stack ? (
                <>
                  Cached overview —{' '}
                  {isExternal ? 'VPC stack' : 'local stack'}
                  {stack.status ? ` · last known status: ${stack.status}` : ''}
                </>
              ) : (
                <>Stack ID {stackId}</>
              )}
            </p>
          </div>
          {stack?.status && (
            <span className="rounded-full border border-gray-200 bg-gray-100 px-4 py-2 text-sm font-medium text-gray-700">
              {stack.status}
            </span>
          )}
        </div>
      </div>

      {(isRefreshing || !stack) && (
        <StackConnectionBanner
          deploymentTarget={stack?.configuration.deployment?.target}
          needsReconnect={stack?.needsExternalReconnect}
        />
      )}

      <div className="mb-8 flex flex-wrap gap-2">
        {['Overview', 'Accounts', 'Client', 'Armory', 'Advanced'].map((label) => (
          <div key={label} className="h-9 w-24 animate-pulse rounded-md bg-gray-200" />
        ))}
      </div>

      <div className="space-y-6">
        <div className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm">
          <div className="mb-4 flex items-center gap-2 text-sm text-gray-600">
            <Loader2 className="h-4 w-4 animate-spin" />
            Loading services and containers…
          </div>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
            {Array.from({ length: 6 }, (_, index) => (
              <div key={index} className="animate-pulse rounded-lg border border-gray-200 p-4">
                <div className="mb-3 h-5 w-32 rounded bg-gray-200" />
                <div className="h-4 w-24 rounded bg-gray-100" />
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
