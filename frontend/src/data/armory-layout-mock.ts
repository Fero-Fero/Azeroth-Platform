export const MOCK_NEWS = [
  {
    id: '1',
    title: 'Welcome to the Realm',
    excerpt: 'The gates are open - create your hero and begin your adventure across Azeroth.',
    date: 'Jul 8, 2026',
    tag: 'announcement',
    imageUrl: null as string | null,
  },
  {
    id: '2',
    title: 'Patch 3.3.5a Notes',
    excerpt: 'Class balance tweaks, dungeon fixes, and quality-of-life improvements for raiders.',
    date: 'Jul 5, 2026',
    tag: 'patch',
    imageUrl: null,
  },
  {
    id: '3',
    title: 'Weekend PvP Event',
    excerpt: 'Double honor this Saturday and Sunday. Queue up and fight for glory!',
    date: 'Jul 3, 2026',
    tag: 'event',
    imageUrl: null,
  },
]

export const MOCK_CHARACTERS = [
  { name: 'Thrallion', level: 80, classIcon: 'shaman', guild: 'Horde Vanguard' },
  { name: 'Lyraeth', level: 79, classIcon: 'mage', guild: 'Arcane Collective' },
  { name: 'Boulderfist', level: 80, classIcon: 'warrior', guild: null },
  { name: 'Moonpetal', level: 77, classIcon: 'druid', guild: 'Circle of Cenarius' },
  { name: 'Shadowveil', level: 80, classIcon: 'rogue', guild: 'The Syndicate' },
]

export const MOCK_SEARCH_ROWS = [
  { name: 'Thrallion', online: true, level: 80, class: 'shaman', race: 'orc_male', guild: 'Horde Vanguard' },
  { name: 'Lyraeth', online: false, level: 79, class: 'mage', race: 'bloodelf_female', guild: 'Arcane Collective' },
  { name: 'Boulderfist', online: true, level: 80, class: 'warrior', race: 'dwarf_male', guild: null },
  { name: 'Frostwhisper', online: true, level: 80, class: 'mage', race: 'human_female', guild: 'Kirin Tor' },
  { name: 'Ironjaw', online: false, level: 78, class: 'warrior', race: 'orc_male', guild: null },
  { name: 'Sunbloom', online: true, level: 80, class: 'paladin', race: 'bloodelf_female', guild: 'Silver Hand' },
]

export const MOCK_CHARACTER = {
  name: 'Thrallion',
  level: 80,
  className: 'Shaman',
  raceName: 'Orc',
  guild: 'Horde Vanguard',
  ilvl: 245,
  faction: 'Horde',
}

export const MOCK_GUILD = {
  name: 'Horde Vanguard',
  realm: 'AzerothCore',
  members: 128,
}

export const MOCK_GUILD_ROWS = [
  { name: 'Thrallion', level: 80, class: 'shaman', rank: 'Guild Master' },
  { name: 'Lyraeth', level: 79, class: 'mage', rank: 'Officer' },
  { name: 'Boulderfist', level: 80, class: 'warrior', rank: 'Member' },
]

export const MOCK_LOG_ROWS = [
  { rank: 1, name: 'Lyraeth', encounter: 'The Lich King', value: '12:34' },
  { rank: 2, name: 'Thrallion', encounter: 'The Lich King', value: '13:02' },
  { rank: 3, name: 'Shadowveil', encounter: 'Lord Marrowgar', value: '2:18' },
]

export const MOCK_REALMS = ['AzerothCore', 'Development']
