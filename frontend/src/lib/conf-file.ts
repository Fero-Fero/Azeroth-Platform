function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

/** Reads a single `Key = Value` assignment from an AzerothCore-style .conf file. */
export function getConfValue(content: string, key: string): string | null {
  const match = content.match(new RegExp(`^\\s*${escapeRegExp(key)}\\s*=\\s*(.*)$`, 'm'))
  return match?.[1]?.trim() ?? null
}

/** Sets or appends a single `Key = Value` assignment in an AzerothCore-style .conf file. */
export function setConfValue(content: string, key: string, value: string): string {
  const line = `${key} = ${value}`
  const regex = new RegExp(`^\\s*${escapeRegExp(key)}\\s*=.*$`, 'm')
  if (regex.test(content)) {
    return content.replace(regex, line)
  }

  const trimmed = content.trimEnd()
  return trimmed.length > 0 ? `${trimmed}\n${line}\n` : `${line}\n`
}
