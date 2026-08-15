import { useState } from 'react'
import { ChevronDown, ChevronUp, Shield } from 'lucide-react'
import { VpcSecurityRolesCard } from '@/components/stacks/VpcSecurityRolesCard'
import { cn } from '@/lib/utils'

export function VpcSecurityOverviewSection({ defaultExpanded = false }: { defaultExpanded?: boolean }) {
  const [expanded, setExpanded] = useState(defaultExpanded)

  return (
    <div className="rounded-lg border border-indigo-200 bg-indigo-50/50">
      <button
        type="button"
        onClick={() => setExpanded((value) => !value)}
        className="flex w-full items-start justify-between gap-3 px-4 py-3 text-left"
      >
        <span className="flex items-start gap-2">
          <Shield className="mt-0.5 h-4 w-4 shrink-0 text-indigo-700" aria-hidden="true" />
          <span>
            <span className="block text-sm font-semibold text-indigo-950">VPC &amp; security roles</span>
            <span className="mt-0.5 block text-xs font-normal text-indigo-900/90">
              Which ports are public, manager-only, or blocked — before you connect.
            </span>
          </span>
        </span>
        {expanded ? (
          <ChevronUp className="mt-0.5 h-4 w-4 shrink-0 text-indigo-700" aria-hidden="true" />
        ) : (
          <ChevronDown className="mt-0.5 h-4 w-4 shrink-0 text-indigo-700" aria-hidden="true" />
        )}
      </button>
      {expanded ? (
        <div className={cn('border-t border-indigo-200 px-4 pb-4 pt-3')}>
          <VpcSecurityRolesCard compact />
        </div>
      ) : null}
    </div>
  )
}
