import { useState, useEffect } from 'react'
import { X, Loader2, Server, HardDrive, Network, AlertCircle, CheckCircle, Package } from 'lucide-react'
import { stackApi } from '@/services/api'
import type { DiscoveredStackDto, ImportStackRequestDto, ServerType, StackStatus } from '@/types/stack.types'
import { toast } from 'sonner'

interface ImportStacksDialogProps {
  isOpen: boolean
  onClose: () => void
  onImportSuccess: () => void
}

export function ImportStacksDialog({ isOpen, onClose, onImportSuccess }: ImportStacksDialogProps) {
  const [loading, setLoading] = useState(true)
  const [discovered, setDiscovered] = useState<DiscoveredStackDto[]>([])
  const [importing, setImporting] = useState<Record<string, boolean>>({})
  const [importNames, setImportNames] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (isOpen) {
      loadDiscoveredStacks()
    }
  }, [isOpen])

  const loadDiscoveredStacks = async () => {
    setLoading(true)
    setError(null)
    
    try {
      const response = await stackApi.discover()
      const stacks = response.data
      setDiscovered(stacks)
      
      // Initialize names with suggested names
      const names: Record<string, string> = {}
      stacks.forEach(stack => {
        names[stack.stackId] = stack.suggestedName
      })
      setImportNames(names)
    } catch (err) {
      console.error('Failed to discover stacks:', err)
      setError('Failed to discover stacks. Please check logs for details.')
    } finally {
      setLoading(false)
    }
  }

  const handleImport = async (stack: DiscoveredStackDto) => {
    const stackId = stack.stackId
    const stackName = importNames[stackId] || stack.suggestedName
    
    setImporting(prev => ({ ...prev, [stackId]: true }))
    
    try {
      const request: ImportStackRequestDto = {
        stackName
      }
      
      await stackApi.import(stackId, request)
      
      toast.success(`Successfully imported ${stackName}`)
      
      // Remove from list
      setDiscovered(prev => prev.filter(s => s.stackId !== stackId))
      
      onImportSuccess()
      
      // Close dialog if no more stacks
      if (discovered.length === 1) {
        onClose()
      }
    } catch (err: any) {
      console.error('Failed to import stack:', err)
      const errorMessage = err.response?.data?.error || 'Failed to import stack'
      toast.error(errorMessage)
    } finally {
      setImporting(prev => ({ ...prev, [stackId]: false }))
    }
  }

  const handleImportAll = async () => {
    for (const stack of discovered) {
      if (!importing[stack.stackId]) {
        await handleImport(stack)
      }
    }
  }

  const getStatusBadge = (status: StackStatus) => {
    switch (status) {
      case 'Running':
        return <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium bg-green-100 text-green-800 rounded-full">
          <span className="w-2 h-2 bg-green-600 rounded-full" />
          Running
        </span>
      case 'Degraded':
        return <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium bg-yellow-100 text-yellow-800 rounded-full">
          <span className="w-2 h-2 bg-yellow-600 rounded-full" />
          Degraded
        </span>
      case 'Stopped':
        return <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-800 rounded-full">
          <span className="w-2 h-2 bg-gray-600 rounded-full" />
          Stopped
        </span>
      default:
        return <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-800 rounded-full">
          {status}
        </span>
    }
  }

  const getServerTypeBadge = (serverType: ServerType) => {
    return serverType === 'Playerbots' ? (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium bg-purple-100 text-purple-800 rounded-full">
        <Package className="w-3 h-3" />
        Playerbots
      </span>
    ) : (
      <span className="inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium bg-blue-100 text-blue-800 rounded-full">
        <Server className="w-3 h-3" />
        Standard
      </span>
    )
  }

  if (!isOpen) return null

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-4xl max-h-[90vh] overflow-hidden flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b">
          <h2 className="text-xl font-semibold text-gray-900">Import Existing Stacks</h2>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600 transition-colors"
          >
            <X className="w-6 h-6" />
          </button>
        </div>

        {/* Content */}
        <div className="flex-1 overflow-y-auto px-6 py-4">
          {loading && (
            <div className="flex flex-col items-center justify-center py-12">
              <Loader2 className="w-8 h-8 text-blue-600 animate-spin mb-4" />
              <p className="text-gray-600">Discovering stacks...</p>
            </div>
          )}

          {error && (
            <div className="flex items-start gap-3 p-4 bg-red-50 border border-red-200 rounded-lg mb-4">
              <AlertCircle className="w-5 h-5 text-red-600 flex-shrink-0 mt-0.5" />
              <div>
                <p className="text-sm font-medium text-red-900">Error</p>
                <p className="text-sm text-red-700">{error}</p>
              </div>
            </div>
          )}

          {!loading && !error && discovered.length === 0 && (
            <div className="flex flex-col items-center justify-center py-12">
              <Package className="w-16 h-16 text-gray-400 mb-4" />
              <h3 className="text-lg font-semibold text-gray-900 mb-2">No Stacks Found</h3>
              <p className="text-gray-600 text-center max-w-md">
                No existing stacks were discovered that can be imported into the manager.
              </p>
            </div>
          )}

          {!loading && !error && discovered.length > 0 && (
            <div className="space-y-4">
              <p className="text-sm text-gray-600">
                Found {discovered.length} stack{discovered.length !== 1 ? 's' : ''} that can be imported
              </p>

              {discovered.map(stack => (
                <div
                  key={stack.stackId}
                  className="border border-gray-200 rounded-lg p-4 hover:border-gray-300 transition-colors"
                >
                  {/* Header Row */}
                  <div className="flex items-start justify-between mb-3">
                    <div className="flex items-center gap-3">
                      <div className="flex items-center gap-2 text-sm font-mono text-gray-700">
                        <span className="font-semibold">ID:</span>
                        {stack.stackId.substring(0, 8)}
                      </div>
                      {getServerTypeBadge(stack.inferredServerType)}
                      {getStatusBadge(stack.currentStatus)}
                    </div>
                  </div>

                  {stack.isOrphaned && (
                    <div className="flex items-start gap-2 p-3 mb-3 bg-yellow-50 border border-yellow-200 rounded-md">
                      <AlertCircle className="w-4 h-4 text-yellow-600 flex-shrink-0 mt-0.5" />
                      <div className="text-sm text-yellow-800">
                        <strong>Orphaned Stack:</strong> No Docker containers found. You can import this stack and rebuild it later.
                      </div>
                    </div>
                  )}

                  {/* Ports */}
                  <div className="flex flex-wrap gap-4 mb-3 text-sm text-gray-600">
                    <div className="flex items-center gap-1.5">
                      <HardDrive className="w-4 h-4" />
                      <span>DB: {stack.databasePort}</span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <Server className="w-4 h-4" />
                      <span>Auth: {stack.authServerPort}</span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <Server className="w-4 h-4" />
                      <span>World: {stack.worldServerPort}</span>
                    </div>
                    <div className="flex items-center gap-1.5">
                      <Network className="w-4 h-4" />
                      <span>SOAP: {stack.soapPort}</span>
                    </div>
                  </div>

                  {/* Containers */}
                  <div className="text-sm text-gray-600 mb-3">
                    Containers: {stack.containerNames.length} ({stack.containerNames.filter(n => !n.includes('exited')).length} running)
                  </div>

                  {/* Repository Info */}
                  {stack.coreRepositoryUrl && (
                    <div className="text-sm text-gray-600 mb-4">
                      <div className="font-medium text-gray-700 mb-1">Repository:</div>
                      <div className="font-mono text-xs bg-gray-50 p-2 rounded">
                        {stack.coreRepositoryUrl.split('/').slice(-2).join('/')}
                        {stack.coreCommitSha && ` @ ${stack.coreCommitSha.substring(0, 7)}`}
                      </div>
                    </div>
                  )}

                  {/* Import Form - Always show, orphaned stacks can be imported too */}
                  <div className="flex items-center gap-3 pt-3 border-t border-gray-200">
                    <label className="flex-1">
                      <span className="text-sm font-medium text-gray-700 mb-1 block">
                        Stack Name
                      </span>
                      <input
                        type="text"
                        value={importNames[stack.stackId] || stack.suggestedName}
                        onChange={(e) => setImportNames(prev => ({
                          ...prev,
                          [stack.stackId]: e.target.value
                        }))}
                        disabled={importing[stack.stackId]}
                        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-gray-100"
                        placeholder="Enter stack name"
                      />
                    </label>
                    <button
                      onClick={() => handleImport(stack)}
                      disabled={importing[stack.stackId] || !importNames[stack.stackId]?.trim()}
                      className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed transition-colors flex items-center gap-2 mt-6"
                    >
                      {importing[stack.stackId] ? (
                        <>
                          <Loader2 className="w-4 h-4 animate-spin" />
                          Importing...
                        </>
                      ) : (
                        <>
                          <CheckCircle className="w-4 h-4" />
                          Import
                        </>
                      )}
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Footer */}
        {!loading && !error && discovered.length > 0 && (
          <div className="flex items-center justify-end gap-3 px-6 py-4 border-t bg-gray-50">
            <button
              onClick={onClose}
              className="px-4 py-2 text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={handleImportAll}
              disabled={Object.values(importing).some(v => v) || discovered.filter(s => !s.isOrphaned).length === 0}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed transition-colors flex items-center gap-2"
            >
              {Object.values(importing).some(v => v) ? (
                <>
                  <Loader2 className="w-4 h-4 animate-spin" />
                  Importing...
                </>
              ) : (
                'Import All'
              )}
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
