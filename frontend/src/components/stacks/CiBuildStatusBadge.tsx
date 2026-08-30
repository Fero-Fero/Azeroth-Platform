import { CheckCircle, XCircle, Clock, HelpCircle, AlertTriangle } from 'lucide-react'
import type { CiBuildStatusDto } from '@/types/stack.types'

interface CiBuildStatusBadgeProps {
  status?: CiBuildStatusDto
  showDetails?: boolean
  className?: string
}

export function CiBuildStatusBadge({ 
  status, 
  showDetails = false, 
  className = '' 
}: CiBuildStatusBadgeProps) {
  console.log('[CiBuildStatusBadge] status:', status)
  
  if (!status) {
    console.log('[CiBuildStatusBadge] No status, returning null')
    return null
  }

  const getStatusConfig = () => {
    switch (status.status) {
      case 'success':
        return {
          icon: CheckCircle,
          label: 'CI Passing',
          color: 'text-green-600 bg-green-50',
          borderColor: 'border-green-200',
        }
      case 'failure':
        return {
          icon: XCircle,
          label: 'CI Failing',
          color: 'text-red-600 bg-red-50',
          borderColor: 'border-red-200',
        }
      case 'pending':
        return {
          icon: Clock,
          label: 'CI Running',
          color: 'text-yellow-600 bg-yellow-50',
          borderColor: 'border-yellow-200',
        }
      default:
        return {
          icon: HelpCircle,
          label: 'CI Unknown',
          color: 'text-gray-600 bg-gray-50',
          borderColor: 'border-gray-200',
        }
    }
  }

  const config = getStatusConfig()
  const Icon = config.icon

  return (
    <div className={`inline-flex flex-col gap-2 ${className}`}>
      <div 
        className={`inline-flex items-center gap-1.5 px-2 py-1 rounded-md border ${config.color} ${config.borderColor}`}
      >
        <Icon className="w-4 h-4" />
        <span className="text-sm font-medium">{config.label}</span>
        {showDetails && status.status !== 'unknown' && (
          <span className="text-xs">
            ({status.passedChecks}/{status.totalChecks})
          </span>
        )}
      </div>
      
      {showDetails && status.status === 'failure' && status.failedChecks > 0 && (
        <div className="flex items-start gap-2 p-3 bg-red-50 border border-red-200 rounded-md">
          <AlertTriangle className="w-5 h-5 text-red-600 flex-shrink-0 mt-0.5" />
          <div className="flex-1 min-w-0">
            <p className="text-sm font-medium text-red-900 mb-1">
              Build Failures Detected
            </p>
            <p className="text-sm text-red-700">
              {status.failedChecks} critical {status.failedChecks === 1 ? 'check has' : 'checks have'} failed.
              Updating to this version may result in a broken server.
            </p>
            {status.criticalChecks.filter(c => c.conclusion === 'failure' || c.conclusion === 'timed_out').length > 0 && (
              <ul className="mt-2 space-y-1">
                {status.criticalChecks
                  .filter(c => c.conclusion === 'failure' || c.conclusion === 'timed_out' || c.conclusion === 'action_required')
                  .map((check, idx) => (
                    <li key={idx} className="text-xs text-red-600">
                      {check.htmlUrl ? (
                        <a 
                          href={check.htmlUrl} 
                          target="_blank" 
                          rel="noopener noreferrer"
                          className="hover:underline"
                        >
                          ✗ {check.name}
                        </a>
                      ) : (
                        <span>✗ {check.name}</span>
                      )}
                    </li>
                  ))}
              </ul>
            )}
          </div>
        </div>
      )}
      
      {showDetails && status.status === 'pending' && (
        <div className="flex items-start gap-2 p-3 bg-yellow-50 border border-yellow-200 rounded-md">
          <Clock className="w-5 h-5 text-yellow-600 flex-shrink-0 mt-0.5" />
          <div className="flex-1 min-w-0">
            <p className="text-sm font-medium text-yellow-900 mb-1">
              CI Checks In Progress
            </p>
            <p className="text-sm text-yellow-700">
              Continuous integration checks are still running. Consider waiting for completion before updating.
            </p>
          </div>
        </div>
      )}
    </div>
  )
}
