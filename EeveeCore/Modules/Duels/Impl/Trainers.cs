using EeveeCore.Modules.Duels.Impl.Helpers;

namespace EeveeCore.Modules.Duels.Impl;

/// <summary>
///     Represents a generic pokemon trainer.
///     This class outlines the methods that Trainer objects
///     should have, but should not be used directly.
/// </summary>
public class Trainer
{
    /// <summary>
    ///     Initializes a new instance of the Trainer class with the specified name and party.
    ///     Sets up all battlefield conditions and effects for the trainer's side.
    /// </summary>
    /// <param name="name">The name of the trainer.</param>
    /// <param name="party">The list of Pokémon in the trainer's party.</param>
    public Trainer(string name, List<DuelPokemon.DuelPokemon> party)
    {
        Name = name;
        Party = party;
        CurrentPokemon = party.Count > 0 ? party[0] : null;
        foreach (var poke in Party) poke.Owner = this;
        Event = new TaskCompletionSource<bool>();
        SelectedAction = null;
        MidTurnRemove = false;
        BatonPass = null;
        Spikes = 0;
        ToxicSpikes = 0;
        StealthRock = false;
        StickyWeb = false;
        LastIdx = 0;
        Wish = new ExpiringWish();
        AuroraVeil = new ExpiringEffect(0);
        LightScreen = new ExpiringEffect(0);
        Reflect = new ExpiringEffect(0);
        Mist = new ExpiringEffect(0);
        Safeguard = new ExpiringEffect(0);
        HealingWish = false;
        LunarDance = false;
        Tailwind = new ExpiringEffect(0);
        MudSport = new ExpiringEffect(0);
        WaterSport = new ExpiringEffect(0);
        Retaliate = new ExpiringEffect(0);
        FutureSight = new ExpiringItem();
        HasMegaEvolved = false;
        NumFainted = 0;
        NextSubstitute = 0;
    }

    /// <summary>
    ///     Gets or sets the name of the trainer.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    ///     Gets or sets the list of Pokémon in the trainer's party.
    /// </summary>
    public List<DuelPokemon.DuelPokemon> Party { get; set; }

    /// <summary>
    ///     Gets or sets the trainer's currently active Pokémon.
    /// </summary>
    public DuelPokemon.DuelPokemon CurrentPokemon { get; set; }

    /// <summary>
    ///     Gets or sets the task completion source used for asynchronous action selection.
    /// </summary>
    public TaskCompletionSource<bool> Event { get; set; }

    /// <summary>
    ///     Gets or sets the action selected by the trainer for the current turn.
    /// </summary>
    public TrainerAction SelectedAction { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether this trainer's Pokémon was removed in such a way
    ///     that it needs to return mid-turn.
    /// </summary>
    public bool MidTurnRemove { get; set; }

    /// <summary>
    ///     Gets or sets the data baton passed from the previous Pokémon to the next, if applicable.
    /// </summary>
    public BatonPass BatonPass { get; set; }

    /// <summary>
    ///     Gets or sets the number of layers of Spikes on this trainer's side of the field.
    /// </summary>
    public int Spikes { get; set; }

    /// <summary>
    ///     Gets or sets the number of layers of Toxic Spikes on this trainer's side of the field.
    /// </summary>
    public int ToxicSpikes { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether Stealth Rock is on this trainer's side of the field.
    /// </summary>
    public bool StealthRock { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether Sticky Web is on this trainer's side of the field.
    /// </summary>
    public bool StickyWeb { get; set; }

    /// <summary>
    ///     Gets or sets the index of the last Pokémon that was selected in the party.
    /// </summary>
    public int LastIdx { get; set; }

    /// <summary>
    ///     Gets or sets the Wish effect active on this trainer's side of the field.
    /// </summary>
    public ExpiringWish Wish { get; set; }

    /// <summary>
    ///     Gets or sets the Aurora Veil effect active on this trainer's side of the field.
    ///     Reduces damage from physical and special attacks for 5 turns.
    /// </summary>
    public ExpiringEffect AuroraVeil { get; set; }

    /// <summary>
    ///     Gets or sets the Light Screen effect active on this trainer's side of the field.
    ///     Reduces damage from special attacks for 5 turns.
    /// </summary>
    public ExpiringEffect LightScreen { get; set; }

    /// <summary>
    ///     Gets or sets the Reflect effect active on this trainer's side of the field.
    ///     Reduces damage from physical attacks for 5 turns.
    /// </summary>
    public ExpiringEffect Reflect { get; set; }

    /// <summary>
    ///     Gets or sets the Mist effect active on this trainer's side of the field.
    ///     Prevents stat reductions from opposing Pokémon for 5 turns.
    /// </summary>
    public ExpiringEffect Mist { get; set; }

    /// <summary>
    ///     Gets or sets the Safeguard effect active on this trainer's side of the field.
    ///     Prevents non-volatile status conditions for 5 turns.
    /// </summary>
    public ExpiringEffect Safeguard { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the next Pokémon to swap in
    ///     should be restored via Healing Wish.
    /// </summary>
    public bool HealingWish { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the next Pokémon to swap in
    ///     should be restored via Lunar Dance.
    /// </summary>
    public bool LunarDance { get; set; }

    /// <summary>
    ///     Gets or sets the Tailwind effect active on this trainer's side of the field.
    ///     Doubles the Speed of all Pokémon for 4 turns.
    /// </summary>
    public ExpiringEffect Tailwind { get; set; }

    /// <summary>
    ///     Gets or sets the Mud Sport effect active on this trainer's side of the field.
    ///     Reduces the power of Electric-type moves to 1/3 for 5 turns.
    /// </summary>
    public ExpiringEffect MudSport { get; set; }

    /// <summary>
    ///     Gets or sets the Water Sport effect active on this trainer's side of the field.
    ///     Reduces the power of Fire-type moves to 1/3 for 5 turns.
    /// </summary>
    public ExpiringEffect WaterSport { get; set; }

    /// <summary>
    ///     Gets or sets the Retaliate effect that tracks when a party member recently fainted.
    ///     Doubles the power of the move Retaliate for 1 turn.
    /// </summary>
    public ExpiringEffect Retaliate { get; set; }

    /// <summary>
    ///     Gets or sets the Future Sight effect that stores the turns until
    ///     Future Sight attacks this trainer's Pokémon.
    /// </summary>
    public ExpiringItem FutureSight { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether any of this trainer's Pokémon
    ///     have Mega Evolved yet this battle.
    /// </summary>
    public bool HasMegaEvolved { get; set; }

    /// <summary>
    ///     Gets or sets the number of times a Pokémon in this trainer's party has fainted,
    ///     including after being revived.
    /// </summary>
    public int NumFainted { get; set; }

    /// <summary>
    ///     Gets or sets the HP of the Substitute this trainer's next Pokémon
    ///     on the field will receive.
    /// </summary>
    public int NextSubstitute { get; set; }

    /// <summary>
    ///     Determines whether this trainer still has at least one Pokémon that is alive.
    /// </summary>
    /// <returns>
    ///     True if the trainer has at least one Pokémon with HP greater than 0,
    ///     False otherwise.
    /// </returns>
    public bool HasAlivePokemon()
    {
        return Party.Any(poke => poke.Hp > 0);
    }

    /// <summary>
    ///     Updates this trainer for a new turn, handling all turn-based effects
    ///     on the trainer's side of the field.
    /// </summary>
    /// <param name="battle">The current battle context.</param>
    /// <returns>A formatted message describing effects that occurred or expired.</returns>
    public string NextTurn(Battle battle)
    {
        var msg = "";
        SelectedAction = null;
        MidTurnRemove = false;
        var hp = Wish.NextTurn();
        if (hp > 0 && CurrentPokemon != null) msg += CurrentPokemon.Heal(hp, "its wish");
        if (AuroraVeil.NextTurn()) msg += $"{Name}'s aurora veil wore off!\n";
        if (LightScreen.NextTurn()) msg += $"{Name}'s light screen wore off!\n";
        if (Reflect.NextTurn()) msg += $"{Name}'s reflect wore off!\n";
        if (Mist.NextTurn()) msg += $"{Name}'s mist wore off!\n";
        if (Safeguard.NextTurn()) msg += $"{Name}'s safeguard wore off!\n";
        if (Tailwind.NextTurn()) msg += $"{Name}'s tailwind died down!\n";
        if (MudSport.NextTurn()) msg += $"{Name}'s mud sport wore off!\n";
        if (WaterSport.NextTurn()) msg += $"{Name}'s water sport evaporated!\n";
        Retaliate.NextTurn();

        var futureSightData = FutureSight.Item;
        if (FutureSight.NextTurn() && CurrentPokemon != null)
        {
            msg += $"{CurrentPokemon.Name} took the future sight attack!\n";
            var futureSightAttacker = ((Tuple<DuelPokemon.DuelPokemon, Move.Move>)futureSightData).Item1;
            var futureSightMove = ((Tuple<DuelPokemon.DuelPokemon, Move.Move>)futureSightData).Item2;
            var futureSightResult = futureSightMove.Attack(futureSightAttacker, CurrentPokemon, battle);
            msg += futureSightResult.Item1;
        }

        return msg;
    }

    /// <summary>
    ///     Switches the currently active Pokémon to the one at the specified party slot.
    /// </summary>
    /// <param name="slot">The index of the party slot to switch to.</param>
    /// <param name="midTurn">
    ///     Whether the switch is occurring in the middle of a turn rather than as a turn action.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     Thrown when the slot is out of bounds or the Pokémon has no HP.
    /// </exception>
    public void SwitchPoke(int slot, bool midTurn = false)
    {
        if (slot < 0 || slot >= Party.Count) throw new ArgumentException("out of bounds");
        if (!(Party[slot].Hp > 0)) throw new ArgumentException("no hp");
        CurrentPokemon = Party[slot];
        MidTurnRemove = false;
        LastIdx = slot;
        if (midTurn) CurrentPokemon.SwappedIn = true;
    }

    /// <summary>
    ///     Determines whether this trainer is a human player or an AI.
    ///     This method should be overridden by derived classes.
    /// </summary>
    /// <returns>
    ///     True if the trainer is a human player, False if it is an AI.
    /// </returns>
    public virtual bool IsHuman()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    ///     Determines which Pokémon in the party can be legally swapped to, accounting for
    ///     trapping effects, abilities, and other battle conditions.
    /// </summary>
    /// <param name="defender">The opposing Pokémon.</param>
    /// <param name="battle">The current battle context.</param>
    /// <param name="checkTrap">Whether to check for trapping effects that prevent switching.</param>
    /// <returns>
    ///     A list of valid party indexes that can be switched to.
    /// </returns>
    public List<int> ValidSwaps(DuelPokemon.DuelPokemon defender, Battle battle, bool checkTrap = true)
    {
        if (CurrentPokemon != null)
        {
            if (CurrentPokemon.TypeIds.Contains(ElementType.GHOST)) checkTrap = false;
            if (CurrentPokemon.HeldItem.Get() == "shed-shell") checkTrap = false;

            if (checkTrap)
            {
                if (CurrentPokemon.Trapping) return new List<int>();
                if (CurrentPokemon.Ingrain) return new List<int>();
                if (CurrentPokemon.FairyLock.Active() || defender.FairyLock.Active()) return new List<int>();
                if (CurrentPokemon.NoRetreat) return new List<int>();
                if (CurrentPokemon.Bind.Active() && CurrentPokemon.Substitute == 0) return new List<int>();
                if (defender.Ability() == Ability.SHADOW_TAG && CurrentPokemon.Ability() != Ability.SHADOW_TAG)
                    return new List<int>();
                if (defender.Ability() == Ability.MAGNET_PULL && CurrentPokemon.TypeIds.Contains(ElementType.STEEL))
                    return new List<int>();
                if (defender.Ability() == Ability.ARENA_TRAP && CurrentPokemon.Grounded(battle)) return new List<int>();
            }
        }

        var result = new List<int>();
        for (var idx = 0; idx < Party.Count; idx++)
            if (Party[idx].Hp > 0)
                result.Add(idx);

        if (result.Contains(LastIdx)) result.Remove(LastIdx);

        return result;
    }

    /// <summary>
    ///     Determines which moves the current Pokémon can legally use, accounting for
    ///     restrictions like PP, Taunt, Choice items, and other battle conditions.
    /// </summary>
    /// <param name="defender">The opposing Pokémon.</param>
    /// <returns>
    ///     A ValidMovesResult object describing the moves that can be used:
    ///     - Forced: When the Pokémon must use a specific move (e.g., due to being locked into it)
    ///     - ValidIndexes: A list of valid move indexes to choose from
    ///     - Struggle: When no moves can be used, and Struggle must be used instead
    /// </returns>
    public ValidMovesResult ValidMoves(DuelPokemon.DuelPokemon defender)
    {
        // Check if they are FORCED to use a certain move
        if (CurrentPokemon.LockedMove != null) return ValidMovesResult.Forced(CurrentPokemon.LockedMove.Move);

        // Remove all moves not matching a restriction
        var result = new List<int>();
        for (var idx = 0; idx < CurrentPokemon.Moves.Count; idx++)
        {
            var move = CurrentPokemon.Moves[idx];

            if (move.PP <= 0) continue;
            switch (move.DamageClass)
            {
                case DamageClass.STATUS when CurrentPokemon.HeldItem.Get() == "assault-vest":
                case DamageClass.STATUS when CurrentPokemon.Taunt.Active():
                    continue;
            }

            if (move.Effect == 247 && !CurrentPokemon.Moves.Where(m => m.Effect != 247).All(m => m.Used)) continue;
            if (CurrentPokemon.Disable.Active() && move == CurrentPokemon.Disable.Item) continue;
            if ((CurrentPokemon.HeldItem.Get() == "choice-scarf" ||
                 CurrentPokemon.HeldItem.Get() == "choice-band" ||
                 CurrentPokemon.HeldItem.Get() == "choice-specs" ||
                 CurrentPokemon.Ability() == Ability.GORILLA_TACTICS) &&
                CurrentPokemon.ChoiceMove != null && move != CurrentPokemon.ChoiceMove)
                continue;
            if (CurrentPokemon.Torment && CurrentPokemon.LastMove == move) continue;
            if (CurrentPokemon.LastMove is { Effect: 492 } &&
                CurrentPokemon.LastMove.Id == move.Id &&
                !CurrentPokemon.LastMoveFailed)
                continue;
            if (defender.Imprison && defender.Moves.Any(x => x.Id == move.Id)) continue;
            if (CurrentPokemon.HealBlock.Active() && move.IsAffectedByHealBlock()) continue;
            if (CurrentPokemon.Silenced.Active() && move.IsSoundBased()) continue;
            switch (move.Effect)
            {
                case 339 when !CurrentPokemon.AteBerry:
                case 453 when !CurrentPokemon.HeldItem.IsBerry():
                    continue;
            }

            if (CurrentPokemon.Encore.Active() && move != CurrentPokemon.Encore.Item) continue;

            result.Add(idx);
        }

        if (result.Count == 0) return ValidMovesResult.Struggle();

        return ValidMovesResult.ValidIndexes(result);
    }

    /// <summary>
    ///     Represents an action a trainer can take during their turn.
    ///     Base class for specific action types like moves and switches.
    /// </summary>
    public abstract class TrainerAction
    {
        /// <summary>
        ///     Gets a value indicating whether this action is a switch action.
        /// </summary>
        public abstract bool IsSwitch { get; }
    }

    /// <summary>
    ///     Represents a move action chosen by a trainer.
    /// </summary>
    public class MoveAction : TrainerAction
    {
        /// <summary>
        ///     Initializes a new instance of the MoveAction class with the specified move.
        /// </summary>
        /// <param name="move">The move to use.</param>
        public MoveAction(Move.Move move)
        {
            Move = move;
        }

        /// <summary>
        ///     Gets or sets the move to use.
        /// </summary>
        public Move.Move Move { get; set; }

        /// <summary>
        ///     Gets a value indicating whether this action is a switch action.
        ///     Always returns false for move actions.
        /// </summary>
        public override bool IsSwitch => false;
    }

    /// <summary>
    ///     Represents a switch action chosen by a trainer.
    /// </summary>
    public class SwitchAction : TrainerAction
    {
        /// <summary>
        ///     Initializes a new instance of the SwitchAction class with the specified switch index.
        /// </summary>
        /// <param name="switchIndex">The index of the party slot to switch to.</param>
        public SwitchAction(int switchIndex)
        {
            SwitchIndex = switchIndex;
        }

        /// <summary>
        ///     Gets or sets the index of the party slot to switch to.
        /// </summary>
        public int SwitchIndex { get; set; }

        /// <summary>
        ///     Gets a value indicating whether this action is a switch action.
        ///     Always returns true for switch actions.
        /// </summary>
        public override bool IsSwitch => true;
    }
}

/// <summary>
///     Represents a Pokémon trainer that is a player, who picks actions with reactions.
/// </summary>
public class MemberTrainer : Trainer
{
    /// <summary>
    ///     Initializes a new instance of the MemberTrainer class for a player and their party.
    /// </summary>
    /// <param name="id">The player's user ID.</param>
    /// <param name="name">The player's username.</param>
    /// <param name="party">The list of Pokémon in the trainer's party.</param>
    public MemberTrainer(ulong id, string name, List<DuelPokemon.DuelPokemon> party) : base(name, party)
    {
        Id = id;
    }

    /// <summary>
    ///     Gets or sets the player's user ID.
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>
    ///     Determines whether this trainer is a human player or an AI.
    ///     Always returns true for MemberTrainer since it represents a human player.
    /// </summary>
    /// <returns>
    ///     Always returns true.
    /// </returns>
    public override bool IsHuman()
    {
        return true;
    }
}

/// <summary>
///     Represents a Pokémon trainer that is an NPC (Non-Player Character).
///     Provides AI-controlled behavior for battles.
/// </summary>
public class NPCTrainer : Trainer
{
    /// <summary>
    ///     Initializes a new instance of the NPCTrainer class with the specified party.
    ///     Uses a default trainer name "Trainer John".
    /// </summary>
    /// <param name="party">The list of Pokémon in the trainer's party.</param>
    public NPCTrainer(List<DuelPokemon.DuelPokemon> party) : base("Trainer John", party)
    {
    }

    /// <summary>
    ///     Selects and sets a move action for this AI trainer.
    ///     Currently uses a simple random selection among valid moves.
    /// </summary>
    /// <param name="defender">The opposing Pokémon.</param>
    /// <param name="battle">The current battle context.</param>
    public void Move(DuelPokemon.DuelPokemon defender, Battle battle)
    {
        var moveResult = ValidMoves(defender);

        switch (moveResult.Type)
        {
            case ValidMovesResult.ResultType.ForcedMove:
                SelectedAction = new MoveAction(moveResult.ForcedMove);
                break;

            case ValidMovesResult.ResultType.Struggle:
                SelectedAction = new MoveAction(Impl.Move.Move.Struggle());
                break;

            case ValidMovesResult.ResultType.ValidIndexes:
                var moveData = moveResult.ValidMoveIndexes;
                var randomMoveIdx = moveData[new Random().Next(moveData.Count)];
                SelectedAction = new MoveAction(CurrentPokemon.Moves[randomMoveIdx]);
                break;
        }

        Event.SetResult(true);
        // TODO: npc ai?
    }

    /// <summary>
    ///     Selects a Pokémon to switch to and performs the switch for this AI trainer.
    ///     Currently uses a simple random selection among valid switches.
    /// </summary>
    /// <param name="defender">The opposing Pokémon.</param>
    /// <param name="battle">The current battle context.</param>
    /// <param name="midTurn">
    ///     Whether the switch is occurring in the middle of a turn rather than as a turn action.
    /// </param>
    public void Swap(DuelPokemon.DuelPokemon defender, Battle battle, bool midTurn = false)
    {
        var validSwaps = ValidSwaps(defender, battle, false);
        var pokeIdx = validSwaps[new Random().Next(validSwaps.Count)];
        SwitchPoke(pokeIdx, midTurn);

        // Also set this as the selected action if it's not a mid-turn swap
        if (!midTurn) SelectedAction = new SwitchAction(pokeIdx);

        Event.SetResult(true);
        // TODO: npc ai?
    }

    /// <summary>
    ///     Determines whether this trainer is a human player or an AI.
    ///     Always returns false for NPCTrainer since it represents an AI-controlled character.
    /// </summary>
    /// <returns>
    ///     Always returns false.
    /// </returns>
    public override bool IsHuman()
    {
        return false;
    }
}