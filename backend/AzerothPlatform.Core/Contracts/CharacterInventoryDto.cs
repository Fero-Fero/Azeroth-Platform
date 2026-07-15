namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Full inventory snapshot for a character (equipment, bags, bank)
/// </summary>
public class CharacterInventoryDto
{
    /// <summary>Equipped gear — slots 0–18 (bag = 0)</summary>
    public List<ItemSlotDto> EquippedItems { get; set; } = [];

    /// <summary>Backpack items — slots 23–38 (bag = 0)</summary>
    public List<ItemSlotDto> BackpackItems { get; set; } = [];

    /// <summary>Contents of the 4 equipped bag containers</summary>
    public List<BagDto> BagItems { get; set; } = [];

    /// <summary>Bank main storage — slots 39–66 (bag = 0)</summary>
    public List<ItemSlotDto> BankItems { get; set; } = [];

    /// <summary>Contents of the 7 bank bag containers</summary>
    public List<BagDto> BankBagItems { get; set; } = [];
}

/// <summary>
/// A single item occupying a specific inventory slot
/// </summary>
public class ItemSlotDto
{
    /// <summary>Slot index within the bag (or character paper-doll)</summary>
    public int Slot { get; set; }

    /// <summary>Bag guid (0 = direct on character)</summary>
    public int Bag { get; set; }

    /// <summary>Item instance GUID</summary>
    public int ItemGuid { get; set; }

    /// <summary>Item template entry ID</summary>
    public int ItemEntry { get; set; }

    /// <summary>Localised item name from item_template</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Display ID used for icons</summary>
    public int DisplayId { get; set; }

    /// <summary>0=Poor 1=Common 2=Uncommon 3=Rare 4=Epic 5=Legendary 6=Artifact</summary>
    public int Quality { get; set; }

    public int ItemLevel { get; set; }

    public int RequiredLevel { get; set; }

    public int StackCount { get; set; }

    public int Durability { get; set; }

    public int MaxDurability { get; set; }
}

/// <summary>
/// A bag container and its contents
/// </summary>
public class BagDto
{
    /// <summary>Slot of the bag container on the character (19–22 for bags, 67–74 for bank bags)</summary>
    public int ContainerSlot { get; set; }

    /// <summary>Item guid of the container itself</summary>
    public int ContainerGuid { get; set; }

    /// <summary>Item entry of the container</summary>
    public int ContainerEntry { get; set; }

    /// <summary>Display name of the container</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>Items inside this bag</summary>
    public List<ItemSlotDto> Items { get; set; } = [];
}

/// <summary>
/// Request to ban a character
/// </summary>
public class BanCharacterRequest
{
    /// <summary>Ban duration string (e.g. "30m", "7d", "-1" for permanent)</summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>Reason for the ban</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request to mute a character's chat
/// </summary>
public class MuteCharacterRequest
{
    /// <summary>Mute duration in minutes</summary>
    public int Minutes { get; set; }

    /// <summary>Reason for the mute</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Request to modify a character's gold directly (positive = add, negative = remove)
/// </summary>
public class ModifyMoneyRequest
{
    /// <summary>Amount in copper (positive to add, negative to remove)</summary>
    public long CopperAmount { get; set; }
}

/// <summary>
/// Request to add an item directly to a character's inventory
/// </summary>
public class AddItemRequest
{
    /// <summary>Item template entry ID</summary>
    public int ItemId { get; set; }

    /// <summary>Stack count to add</summary>
    public int Count { get; set; } = 1;
}
