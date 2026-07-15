// Character inventory types

export interface ItemSlotDto {
  slot: number
  bag: number
  itemGuid: number
  itemEntry: number
  itemName: string
  displayId: number
  /** 0=Poor 1=Common 2=Uncommon 3=Rare 4=Epic 5=Legendary 6=Artifact 7=Heirloom */
  quality: number
  itemLevel: number
  requiredLevel: number
  stackCount: number
  durability: number
  maxDurability: number
}

export interface BagDto {
  containerSlot: number
  containerGuid: number
  containerEntry: number
  containerName: string
  items: ItemSlotDto[]
}

export interface CharacterInventoryDto {
  equippedItems: ItemSlotDto[]
  backpackItems: ItemSlotDto[]
  bagItems: BagDto[]
  bankItems: ItemSlotDto[]
  bankBagItems: BagDto[]
}

/** Equipment slot index → readable label mapping */
export const EQUIPMENT_SLOT_LABELS: Record<number, string> = {
  0: 'Head',
  1: 'Neck',
  2: 'Shoulder',
  3: 'Shirt',
  4: 'Chest',
  5: 'Waist',
  6: 'Legs',
  7: 'Feet',
  8: 'Wrists',
  9: 'Hands',
  10: 'Finger 1',
  11: 'Finger 2',
  12: 'Trinket 1',
  13: 'Trinket 2',
  14: 'Back',
  15: 'Main Hand',
  16: 'Off Hand',
  17: 'Ranged',
  18: 'Tabard',
}

/** Item quality → TailwindCSS text colour class */
export const QUALITY_COLORS: Record<number, string> = {
  0: 'text-gray-400',
  1: 'text-gray-700',
  2: 'text-green-400',
  3: 'text-blue-400',
  4: 'text-purple-400',
  5: 'text-orange-400',
  6: 'text-red-400',
  7: 'text-cyan-400',
}

/** WoW race id → display name */
export const RACES: Record<number, string> = {
  1: 'Human', 2: 'Orc', 3: 'Dwarf', 4: 'Night Elf', 5: 'Undead',
  6: 'Tauren', 7: 'Gnome', 8: 'Troll', 10: 'Blood Elf', 11: 'Draenei',
}

/** WoW class id → display name */
export const CLASSES: Record<number, string> = {
  1: 'Warrior', 2: 'Paladin', 3: 'Hunter', 4: 'Rogue', 5: 'Priest',
  6: 'Death Knight', 7: 'Shaman', 8: 'Mage', 9: 'Warlock', 11: 'Druid',
}
