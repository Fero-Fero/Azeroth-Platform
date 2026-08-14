import { type ClassValue, clsx } from "clsx"
import { twMerge } from "tailwind-merge"

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
      data?: {
        error?: string
        title?: string
        errors?: Record<string, string[]>
      }
    }
    message?: string
  }
  const data = anyErr?.response?.data as { error?: string; message?: string; title?: string; errors?: Record<string, string[]> } | undefined
  if (data?.message) return data.message
  if (data?.error) return data.error
  if (data?.errors) {
    const first = Object.values(data.errors).flat()[0]
    if (first) return first
  }
  if (data?.title) return data.title
  if (anyErr?.message === 'Network Error' && !anyErr?.response) {
    const base =
      'Could not reach the manager API (connection failed or timed out). Ensure the manager is running and retry.'
    return networkContext ? `${base} ${networkContext}` : base
  }
  return anyErr?.message ?? 'Something went wrong.'
}

/** True when the manager fell back to a cached VPC probe (live SSH refresh timed out). */
export function isStaleVpcProbeCache(message: string | null | undefined): boolean {
  return !!message?.toLowerCase().includes('showing the last successful probe')
}

/** True when a VPC Docker/SSH probe timed out (often load or limited CPU/RAM — not always a dead host). */
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
