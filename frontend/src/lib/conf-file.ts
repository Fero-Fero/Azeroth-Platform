function splitConfLines(content: string): string[] {
  return content.split(/\r?\n/)
}

function isCommentLine(line: string): boolean {
  return /^\s*#/.test(line)
}

function assignmentValue(line: string, key: string): string | null {
  if (isCommentLine(line)) {
    return null
  }

  const eq = line.indexOf('=')
  if (eq <= 0) {
    return null
  }

  const lineKey = line.slice(0, eq).trim()
  if (lineKey.toLowerCase() !== key.toLowerCase()) {
    return null
  }

  return line
    .slice(eq + 1)
    .replace(/\s+#.*$/, '')
    .trim()
}

/** Reads a single `Key = Value` assignment from an AzerothCore-style .conf file. Comment lines are skipped. */
export function getConfValue(content: string, key: string): string | null {
  for (const line of splitConfLines(content)) {
    const value = assignmentValue(line, key)
    if (value !== null) {
      return value
    }
  }
  return null
}

/** Sets or appends a single `Key = Value` assignment in an AzerothCore-style .conf file. */
export function setConfValue(content: string, key: string, value: string): string {
  const nextLine = `${key} = ${value}`
  const lines = splitConfLines(content)
  const index = lines.findIndex((line) => assignmentValue(line, key) !== null)
  if (index >= 0) {
    lines[index] = nextLine
    return lines.join('\n')
  }

  const trimmed = content.trimEnd()
  return trimmed.length > 0 ? `${trimmed}\n${nextLine}\n` : `${nextLine}\n`
}

/** Sets or appends each `Key = Value` assignment. */
export function setConfValues(content: string, values: Readonly<Record<string, string>>): string {
  let next = content
  for (const [key, value] of Object.entries(values)) {
    next = setConfValue(next, key, value)
  }
  return next
}

/** True when every key is present and equals the expected value. */
export function confValuesMatch(content: string, expected: Readonly<Record<string, string>>): boolean {
  return Object.entries(expected).every(([key, value]) => getConfValue(content, key) === value)
}
