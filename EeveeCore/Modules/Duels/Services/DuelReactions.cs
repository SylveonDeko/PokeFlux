using EeveeCore.Common.Interactive;
using EeveeCore.Modules.Duels.Impl;
using EeveeCore.Modules.Duels.Impl.Helpers;

namespace EeveeCore.Modules.Duels.Services;

/// <summary>
///     Lets duel players pick leads, moves and swaps with reactions.
/// </summary>
/// <remarks>
///     Each turn message carries a number for each move, 🔄 to swap and ⚡ to toggle Mega Evolution when a player can,
///     and 🏳️ to forfeit. Players can also type the number, the move name, swap, mega or forfeit.
///     Each player is listened to on their own, so one player's swap prompt does not hold up the other.
/// </remarks>
/// <param name="client">The Fluxer client, for typed picks.</param>
/// <param name="reactions">The reaction helpers.</param>
public sealed class DuelReactions(FluxerClient client, ReactionService reactions) : INService
{
    /// <summary>
    ///     The numbered reactions, for moves and party slots.
    /// </summary>
    public static readonly string[] Numbers = ["1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣"];

    private const string Swap = "🔄";
    private const string Mega = "⚡";
    private const string Forfeit = "🏳️";

    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SwapTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Waits for both players to react to the team preview with the party slot to lead with. A player who does
    ///     not pick in time leads with their first Pokémon.
    /// </summary>
    /// <param name="battle">The battle.</param>
    /// <param name="preview">The team preview message.</param>
    public async Task PickLeadsAsync(Battle battle, Message preview)
    {
        var slots = Math.Max(battle.Trainer1.Party.Count, battle.Trainer2.Party.Count);
        var adding = ReactionService.AddReactionsAsync(preview, Numbers.Take(slots));
        await Task.WhenAll(PickLead(battle.Trainer1), PickLead(battle.Trainer2), adding);
        await ReactionService.TryClearReactionsAsync(preview);

        async Task PickLead(Trainer trainer)
        {
            if (trainer is not MemberTrainer member)
                return;

            var choices = Numbers.Take(trainer.Party.Count).ToList();
            var pressed = await WaitForChoiceAsync(preview, member.Id, choices, ParseNumber, TurnTimeout);
            trainer.SwitchPoke(pressed is null ? 0 : choices.IndexOf(pressed));
            trainer.Event.TrySetResult(true);
        }
    }

    /// <summary>
    ///     Builds the turn message's field listing a player's moves and options.
    /// </summary>
    /// <param name="trainer">The player.</param>
    /// <param name="opponent">Their opponent.</param>
    /// <param name="battle">The battle.</param>
    /// <returns>The field's name and text.</returns>
    public static (string Name, string Value) ActionsField(Trainer trainer, Trainer opponent, Battle battle)
    {
        var poke = trainer.CurrentPokemon;
        var result = trainer.ValidMoves(opponent.CurrentPokemon);
        var lines = new List<string>();

        if (result.Type == ValidMovesResult.ResultType.ForcedMove)
            lines.Add($"Forced to use **{result.ForcedMove.PrettyName}**");
        else if (result.Type == ValidMovesResult.ResultType.Struggle)
            lines.Add("Any number: **Struggle**");
        else
            for (var i = 0; i < poke.Moves.Count && i < 4; i++)
            {
                var move = poke.Moves[i];
                var label = move.Id == 165 ? move.PrettyName : $"{move.PrettyName} | {move.PP}pp";
                lines.Add(result.ValidMoveIndexes.Contains(i) ? $"{Numbers[i]} {label}" : $"{Numbers[i]} ~~{label}~~");
            }

        var options = new List<string>();
        if (trainer.ValidSwaps(opponent.CurrentPokemon, battle).Count > 0)
            options.Add($"{Swap} swap");
        if (CanMega(trainer))
            options.Add($"{Mega} mega {(poke.ShouldMegaEvolve ? "on" : "off")}");
        options.Add($"{Forfeit} forfeit");
        lines.Add(string.Join(" · ", options));

        return ($"{trainer.Name}: {poke.Name}", string.Join("\n", lines));
    }

    /// <summary>
    ///     Adds the turn reactions to the turn message and waits until every player has picked an action. A player
    ///     who does not pick in time forfeits.
    /// </summary>
    /// <param name="battle">The battle.</param>
    /// <param name="turnMessage">The turn message.</param>
    public async Task RunTurnAsync(Battle battle, Message turnMessage)
    {
        var players = new[] { (battle.Trainer1, battle.Trainer2), (battle.Trainer2, battle.Trainer1) }
            .Where(p => p.Item1 is MemberTrainer)
            .ToList();
        var moveCount = players.Max(p => Math.Min(p.Item1.CurrentPokemon.Moves.Count, 4));
        var controls = Numbers.Take(Math.Max(moveCount, 1)).ToList();
        if (players.Any(p => p.Item1.ValidSwaps(p.Item2.CurrentPokemon, battle).Count > 0))
            controls.Add(Swap);
        if (players.Any(p => CanMega(p.Item1)))
            controls.Add(Mega);
        controls.Add(Forfeit);
        var adding = ReactionService.AddReactionsAsync(turnMessage, controls);
        await Task.WhenAll(
            Listen(battle.Trainer1, battle.Trainer2),
            Listen(battle.Trainer2, battle.Trainer1),
            adding);
        await ReactionService.TryClearReactionsAsync(turnMessage);

        async Task Listen(Trainer trainer, Trainer opponent)
        {
            if (trainer is not MemberTrainer member)
                return;

            var result = trainer.ValidMoves(opponent.CurrentPokemon);
            if (result.Type == ValidMovesResult.ResultType.ForcedMove)
            {
                Choose(trainer, new Trainer.MoveAction(result.ForcedMove));
                return;
            }

            while (!trainer.Event.Task.IsCompleted)
            {
                var pressed = await WaitForChoiceAsync(turnMessage, member.Id, controls,
                    text => ParseTurnAction(text, trainer), TurnTimeout);
                switch (pressed)
                {
                    case null:
                        Choose(trainer, null);
                        return;
                    case Swap:
                        if (await PickSwapAsync(battle, trainer, opponent, true) is { } slot)
                            Choose(trainer, new Trainer.SwitchAction(slot));
                        break;
                    case Mega:
                        if (CanMega(trainer))
                        {
                            trainer.CurrentPokemon.ShouldMegaEvolve = !trainer.CurrentPokemon.ShouldMegaEvolve;
                            await battle.Channel.SendMessageAsync(
                                $"{trainer.Name}: Mega Evolution {(trainer.CurrentPokemon.ShouldMegaEvolve ? "on" : "off")}.");
                        }

                        break;
                    case Forfeit:
                        var confirm = await battle.Channel.SendMessageAsync($"{trainer.Name}, forfeit the battle?");
                        if (await reactions.ConfirmAsync(confirm, member.Id, SwapTimeout) == true)
                            Choose(trainer, null);
                        confirm.DeleteAfter(0);
                        break;
                    default:
                        var index = Array.IndexOf(Numbers, pressed);
                        if (result.Type == ValidMovesResult.ResultType.Struggle)
                            Choose(trainer, new Trainer.MoveAction(Impl.Move.Move.Struggle()));
                        else if (result.ValidMoveIndexes.Contains(index) && index < trainer.CurrentPokemon.Moves.Count)
                            Choose(trainer, new Trainer.MoveAction(trainer.CurrentPokemon.Moves[index]));
                        break;
                }
            }
        }
    }

    /// <summary>
    ///     Asks a player which Pokémon to swap to, with a numbered reaction for each Pokémon they can swap to.
    /// </summary>
    /// <param name="battle">The battle.</param>
    /// <param name="trainer">The player.</param>
    /// <param name="opponent">Their opponent.</param>
    /// <param name="checkTrap">Whether trapping effects stop the swap.</param>
    /// <returns>The party slot picked, or null when they did not pick in time or cannot swap.</returns>
    public async Task<int?> PickSwapAsync(Battle battle, Trainer trainer, Trainer opponent, bool checkTrap)
    {
        if (trainer is not MemberTrainer member)
            return null;

        var valid = trainer.ValidSwaps(opponent.CurrentPokemon, battle, checkTrap);
        if (valid.Count == 0)
            return null;

        var list = string.Join("\n", trainer.Party.Select((p, i) =>
            valid.Contains(i) ? $"{Numbers[i]} {p.Name} | {p.Hp}hp" : $"{Numbers[i]} ~~{p.Name} | {p.Hp}hp~~"));
        var prompt = await battle.Channel.SendMessageAsync(
            $"{trainer.Name}, pick a Pokémon to swap to by reacting or typing its number:\n{list}");

        var choices = valid.Select(i => Numbers[i]).ToList();
        var adding = ReactionService.AddReactionsAsync(prompt, choices);
        var picked = await WaitForChoiceAsync(prompt, member.Id, choices, ParseNumber, SwapTimeout);
        await adding;
        prompt.DeleteAfter(0);
        return picked is null ? null : valid[choices.IndexOf(picked)];
    }

    private async Task<string?> WaitForChoiceAsync(Message message, ulong userId, IReadOnlyList<string> choices,
        Func<string, string?> parseTyped, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource();
        var press = reactions.WaitForPressAsync(message.Id, userId, choices, timeout, cancel.Token);
        var typed = client.Gateway.MessageCreated.WaitAsync(
            m => m.ChannelId == message.ChannelId && m.Author?.Id == userId
                                                  && parseTyped(m.Content) is { } choice && choices.Contains(choice),
            timeout, cancel.Token);

        var first = await Task.WhenAny(press, typed);
        await cancel.CancelAsync();
        if (first == press)
            return await press;

        try
        {
            return await typed is { } reply ? parseTyped(reply.Content) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static string? ParseNumber(string text) =>
        int.TryParse(text.Trim(), out var n) && n >= 1 && n <= Numbers.Length ? Numbers[n - 1] : null;

    private static string? ParseTurnAction(string text, Trainer trainer)
    {
        var input = text.Trim().ToLowerInvariant();
        switch (input)
        {
            case "swap" or "switch":
                return Swap;
            case "mega":
                return Mega;
            case "forfeit" or "ff":
                return Forfeit;
        }

        if (ParseNumber(input) is { } number)
            return number;

        static string Normalize(string s) => s.ToLowerInvariant().Replace(" ", "").Replace("-", "");
        var moves = trainer.CurrentPokemon?.Moves ?? [];
        var index = moves.FindIndex(m => Normalize(m.PrettyName) == Normalize(input));
        return index is >= 0 and < 4 ? Numbers[index] : null;
    }

    private static bool CanMega(Trainer trainer) =>
        trainer is { CurrentPokemon.MegaTypeIds: not null, HasMegaEvolved: false };

    private static void Choose(Trainer trainer, Trainer.TrainerAction? action)
    {
        if (trainer.Event.Task.IsCompleted)
            return;
        trainer.SelectedAction = action;
        trainer.Event.TrySetResult(true);
    }
}
