import { describe, expect, it } from 'vitest'
import { normalizeStackNameInput } from './stack-name'

describe('normalizeStackNameInput', () => {
  it('lowercases letters and joins words with a single hyphen', () => {
    expect(normalizeStackNameInput('My WotLK Server')).toBe('my-wotlk-server')
  })

  it('collapses multiple spaces to one hyphen', () => {
    expect(normalizeStackNameInput('Test    1')).toBe('test-1')
  })

  it('does not turn multiple spaces into multiple hyphens', () => {
    expect(normalizeStackNameInput('Test---1')).toBe('test-1')
    expect(normalizeStackNameInput('Hello   World   2')).toBe('hello-world-2')
  })

  it('keeps a single trailing hyphen while the user is still typing the next word', () => {
    expect(normalizeStackNameInput('Test    ')).toBe('test-')
    expect(normalizeStackNameInput('test-')).toBe('test-')
  })

  it('trims edge hyphens when asked', () => {
    expect(normalizeStackNameInput('  Test    1  ', { trimEdges: true })).toBe('test-1')
    expect(normalizeStackNameInput('test-', { trimEdges: true })).toBe('test')
  })

  it('treats punctuation as a word separator', () => {
    expect(normalizeStackNameInput('Test@#1')).toBe('test-1')
  })
})
