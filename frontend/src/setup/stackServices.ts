import type { StackDetailsDto } from '@/types/stack.types'

export function isStackServiceRunning(stack: StackDetailsDto, serviceName: string): boolean {
  return stack.services.some((svc) => svc.service === serviceName && svc.state === 'running')
}

export function isDatabaseRunning(stack: StackDetailsDto): boolean {
  return stack.services.some(
    (svc) =>
      (svc.service === 'ac-database' || svc.service.includes('database')) && svc.state === 'running',
  )
}

export function hasModule(stack: StackDetailsDto, moduleId: string): boolean {
  return stack.configuration.moduleIds?.includes(moduleId) ?? false
}
