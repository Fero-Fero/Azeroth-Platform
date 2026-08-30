namespace AzerothPlatform.Core.Contracts;

/// <summary>
/// Result returned after creating the AH Bot account and characters.
/// </summary>
public record AhBotSetupResultDto(
    int AccountId,
    int AllianceGuid,
    int HordeGuid,
    bool CharactersCreated
);
