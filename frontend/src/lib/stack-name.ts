const STACK_NAME_MAX_LENGTH = 64

/**
 * Lowercases a stack name and joins words with a single hyphen.
 * Multiple spaces or other separators collapse to one hyphen: "Test    1" → "test-1".
 */
export function normalizeStackNameInput(
  value: string,
  options?: { trimEdges?: boolean },
): string {
  const trimEdges = options?.trimEdges ?? false
  const keepTrailingHyphen = !trimEdges && /[\s-]$/.test(value)

  let slug = value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+/, '')

  slug = slug.replace(/-+$/, '')
  if (keepTrailingHyphen && slug.length > 0) {
    slug += '-'
  }

  if (slug.length > STACK_NAME_MAX_LENGTH) {
    slug = slug.slice(0, STACK_NAME_MAX_LENGTH).replace(/-+$/, '')
    if (keepTrailingHyphen && slug.length > 0 && slug.length < STACK_NAME_MAX_LENGTH) {
      slug += '-'
    }
  }

  return slug
}
