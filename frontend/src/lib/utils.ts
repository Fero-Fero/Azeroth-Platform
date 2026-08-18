import { type ClassValue, clsx } from "clsx"
import { twMerge } from "tailwind-merge"
import { DeploymentTarget, type StackDetailsDto } from '@/types/stack.types'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

/**
 * Best-effort human-readable message from an API/axios error: prefers the server-provided
 * `response.data.error`, then the JS error `message`, then a generic fallback.
 * Pass optional `networkContext` when a slow or remote operation may have timed out.
 */
export function apiErrorMessage(err: unknown, networkContext?: string): string {
  const anyErr = err as {
    response?: {
      data?: unknown
    }
    message?: string
  }
  const data = anyErr?.response?.data
  if (typeof data === 'string' && data.trim().length > 0) {
    return data
  }

  const payload = data as {
    error?: string
    message?: string
    detail?: string
    title?: string
    errors?: Record<string, string[]>
  } | undefined
  if (payload?.detail) return payload.detail
  if (payload?.message) return payload.message
  if (payload?.error) return payload.error
  if (payload?.errors) {
    const first = Object.values(payload.errors).flat()[0]
    if (first) return first
  }
  if (payload?.title) return payload.title
  if (anyErr?.message === 'Network Error' && !anyErr?.response) {
    const base =
      'Could not reach the manager API (connection failed or timed out). Ensure the manager is running and retry.'
    return networkContext ? `${base} ${networkContext}` : base
  }
  const code = (err as { code?: string })?.code
  const timedOut =
    code === 'ECONNABORTED'
    || code === 'ERR_CANCELED'
    || (typeof anyErr?.message === 'string' && anyErr.message.toLowerCase().includes('timeout'))
  if (timedOut) {
    const base = 'The manager did not finish in time. If you just rebuilt it, wait until it is healthy, then retry. A Windows VPC Verify can take several minutes after a reboot.'
    return networkContext ? `${base} ${networkContext}` : base
  }
  return anyErr?.message ?? 'Something went wrong.'
}

/** True when the manager fell back to a cached VPC probe (live SSH refresh timed out). */
export function isStaleVpcProbeCache(message: string | null | undefined): boolean {
  return !!message?.toLowerCase().includes('showing the last successful probe')
}

/** True when a VPC Docker/SSH probe timed out (often load or limited CPU/RAM - not always a dead host). */
export function isVpcProbeSlow(message: string | null | undefined): boolean {
  if (!message) return false
  const lower = message.toLowerCase()
  return (
    lower.includes('timed out refreshing live status') ||
    lower.includes('timed out connecting to the remote docker engine')
  )
}

/** True when stderr/message indicates the manager cannot reach the VPC host (not bad credentials). */
export function isSshConnectivityError(message: string | null | undefined): boolean {
  if (!message || isStaleVpcProbeCache(message) || isVpcProbeSlow(message)) return false
  const lower = message.toLowerCase()
  return (
    lower.includes('timed out') ||
    lower.includes('connection refused') ||
    lower.includes('no route to host') ||
    lower.includes('network is unreachable') ||
    lower.includes('could not resolve') ||
    lower.includes('banner exchange') ||
    lower.includes('connection reset') ||
    lower.includes('host is down') ||
    lower.includes('name or service not known')
  )
}

export type VpcListHostAlert =
  | { kind: 'offline'; message: string }
  | { kind: 'docker-stopped'; message: string }

/** VPC host / Docker reachability for the stacks list (external stacks only). */
export function getVpcListHostAlert(stack: Pick<
  StackDetailsDto,
  'configuration' | 'needsExternalReconnect' | 'dockerEngineAvailable' | 'dockerEngineUnavailableReason'
>): VpcListHostAlert | null {
  if (stack.configuration.deployment?.target !== DeploymentTarget.External) return null
  if (stack.needsExternalReconnect) return null
  if (stack.dockerEngineAvailable !== false) return null

  const reason = stack.dockerEngineUnavailableReason
  if (isVpcProbeSlow(reason) || isStaleVpcProbeCache(reason)) return null

  if (isSshConnectivityError(reason)) {
    return {
      kind: 'offline',
      message:
        'Remote VPC host is unreachable - the cloud instance may be stopped or powered off. Start the instance in your cloud console, then refresh.',
    }
  }

  return {
    kind: 'docker-stopped',
    message: 'VPC host is reachable but Docker is not running. Open the stack to start Docker or bring containers up.',
  }
}
