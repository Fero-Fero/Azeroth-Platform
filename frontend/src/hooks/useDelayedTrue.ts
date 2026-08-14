import { useEffect, useState } from 'react'

/** Returns true only after <paramref name="value"/> has stayed true for <paramref name="delayMs"/>. */
export function useDelayedTrue(value: boolean, delayMs: number): boolean {
  const [delayed, setDelayed] = useState(false)

  useEffect(() => {
    if (!value) {
      setDelayed(false)
      return
    }

    const timer = window.setTimeout(() => setDelayed(true), delayMs)
    return () => window.clearTimeout(timer)
  }, [value, delayMs])

  return delayed
}
