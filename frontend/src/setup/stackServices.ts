import type { StackDetailsDto, StackJobStatus } from '@/types/stack.types'
import { StackStatus } from '@/types/stack.types'

export function isStackServiceRunning(stack: StackDetailsDto, serviceName: string): boolean {
  return stack.services.some((svc) => svc.service === serviceName && svc.state === 'running')
}

export function isDatabaseRunning(stack: StackDetailsDto): boolean {
  return stack.services.some(
    (svc) =>
      (svc.service === 'ac-database' || svc.service.includes('database')) && svc.state === 'running',
  )
}

export function isDbImportRunning(stack: StackDetailsDto): boolean {
  return stack.services.some(
    (svc) =>
      (svc.service === 'ac-db-import' || svc.service.includes('db-import')) &&
      (svc.state === 'running' || svc.state === 'restarting'),
  )
}

export function isDbImportInProgress(stack: StackDetailsDto, job?: StackJobStatus | null): boolean {
  if (stack.status === StackStatus.Initializing || isDbImportRunning(stack)) {
    return true
  }

  const importService = stack.services.find(
    (svc) => svc.service === 'ac-db-import' || svc.service.includes('db-import'),
  )
  if (importService?.state === 'exited') {
    return false
  }

  if (stack.status === StackStatus.Running || stack.status === StackStatus.Degraded) {
    return false
  }

  if (job?.isRunning && job.action === 'Start') {
    const message = job.message?.toLowerCase() ?? ''
    if (message.includes('service container') || message.includes('game servers')) {
      return false
    }
    if (
      message.includes('first-time') ||
      message.includes('db-import') ||
      message.includes('db import') ||
      message.includes('database and client-data') ||
      stack.status === StackStatus.Starting
    ) {
      return true
    }
  }

  return stack.status === StackStatus.Starting && !importService
}

export function isDbImportFinished(stack: StackDetailsDto, job?: StackJobStatus | null): boolean {
  return !isDbImportInProgress(stack, job)
}

export function hasModule(stack: StackDetailsDto, moduleId: string): boolean {
  return stack.configuration.moduleIds?.includes(moduleId) ?? false
}
