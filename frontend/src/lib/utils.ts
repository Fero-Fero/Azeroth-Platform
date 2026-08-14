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
