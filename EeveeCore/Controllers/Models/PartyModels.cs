namespace EeveeCore.Controllers.Models;

/// <summary>
///     The loaded party and the names of the saved parties.
/// </summary>
/// <param name="Slots">The six slots, null where a slot is empty.</param>
/// <param name="Saved">The names of the saved parties.</param>
public record PartyResponse(List<PokemonCard?> Slots, List<string> Saved);

/// <summary>
///     A saved party.
/// </summary>
/// <param name="Name">The party's name.</param>
/// <param name="Slots">The six slots, null where a slot is empty.</param>
public record SavedPartyResponse(string Name, List<PokemonCard?> Slots);

/// <summary>
///     Puts a Pokémon in a party slot or empties it.
/// </summary>
/// <param name="Slot">The slot, from 1 to 6.</param>
/// <param name="PokemonId">The Pokémon's ID, or null to empty the slot.</param>
public record PartySlotRequest(int Slot, ulong? PokemonId);

/// <summary>
///     Swaps two party slots.
/// </summary>
/// <param name="SlotA">The first slot, from 1 to 6.</param>
/// <param name="SlotB">The second slot, from 1 to 6.</param>
public record PartySwapRequest(int SlotA, int SlotB);

/// <summary>
///     Names a saved party.
/// </summary>
/// <param name="Name">The party's name.</param>
public record PartyNameRequest(string Name);

/// <summary>
///     A slot of a hatchery group.
/// </summary>
/// <param name="Slot">The slot, from 1 to 10.</param>
/// <param name="Unlocked">Whether the user can use the slot.</param>
/// <param name="Egg">The egg in the slot, or null.</param>
/// <param name="Counter">The steps left before the egg hatches.</param>
/// <param name="TotalSteps">The steps the egg started with, or 0 when the slot is empty.</param>
public record HatcherySlotInfo(int Slot, bool Unlocked, PokemonCard? Egg, int Counter, int TotalSteps);

/// <summary>
///     A hatchery group.
/// </summary>
/// <param name="Group">The group, from 1 to 3.</param>
/// <param name="SpeedModifier">How many steps each message takes off the eggs in this group.</param>
/// <param name="MaxSlots">How many slots the user can use.</param>
/// <param name="Slots">The ten slots.</param>
public record HatcheryGroupInfo(int Group, int SpeedModifier, int MaxSlots, List<HatcherySlotInfo> Slots);

/// <summary>
///     Puts an egg in a hatchery slot.
/// </summary>
/// <param name="PokemonId">The egg's ID.</param>
public record HatcherySlotRequest(ulong PokemonId);

/// <summary>
///     A female on the breeding list.
/// </summary>
/// <param name="Pokemon">The Pokémon.</param>
/// <param name="CooldownEndsAt">When it can breed again, or null when it can breed now.</param>
public record BreedingFemale(PokemonCard Pokemon, DateTime? CooldownEndsAt);

/// <summary>
///     The breeding list, cooldown and daycare.
/// </summary>
/// <param name="Females">The females on the breeding list.</param>
/// <param name="CooldownSeconds">The seconds left before the user can breed again.</param>
/// <param name="DaycareUsed">How many eggs the user has.</param>
/// <param name="DaycareLimit">How many eggs the daycare holds.</param>
public record BreedingResponse(List<BreedingFemale> Females, int CooldownSeconds, int DaycareUsed, int DaycareLimit);

/// <summary>
///     Sets the breeding list.
/// </summary>
/// <param name="PokemonIds">The females' IDs. An empty list clears the breeding list.</param>
public record BreedingFemalesRequest(List<ulong> PokemonIds);

/// <summary>
///     The result of setting the breeding list.
/// </summary>
/// <param name="Valid">The IDs put on the list.</param>
/// <param name="Invalid">The IDs that are not the user's or not female or Ditto.</param>
/// <param name="Message">What happened.</param>
public record BreedingFemalesResponse(List<ulong> Valid, List<ulong> Invalid, string Message);
