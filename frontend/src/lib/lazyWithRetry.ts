import { lazy, type ComponentType, type LazyExoticComponent } from 'react'

const RELOAD_KEY = 'vite-chunk-reload'

/**
 * Lazy-load a route chunk; on failure (stale deploy / cached index.html referencing old hashes),
 * reload once so the browser picks up the current bundle manifest.
 */
export function lazyWithRetry<T extends ComponentType<unknown>>(
  factory: () => Promise<{ default: T }>,
): LazyExoticComponent<T> {
  return lazy(async () => {
    try {
      const module = await factory()
      sessionStorage.removeItem(RELOAD_KEY)
      return module
    } catch (error) {
      if (!sessionStorage.getItem(RELOAD_KEY)) {
        sessionStorage.setItem(RELOAD_KEY, '1')
        window.location.reload()
        return new Promise<{ default: T }>(() => {})
      }

      sessionStorage.removeItem(RELOAD_KEY)
      throw error
    }
  })
}
