using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Games.Models;
using EeveeCore.Modules.Games.Services;
using EeveeCore.Modules.Missions.Services;
using Serilog;

namespace EeveeCore.Modules.Games;

/// <summary>
///     Mini-games to earn credits and have fun: word search and the slot machine.
/// </summary>
/// <param name="slotMachineService">The slot machine.</param>
/// <param name="missionService">The missions, for game progress.</param>
[Group("game")]
[Alias("games")]
[Name("Games")]
public class GamesModule(SlotMachineService slotMachineService, MissionService missionService)
    : EeveeModule<WordSearchService>
{
    private const string Background = "data/backgrounds/bg1.png";

    private static readonly ConcurrentDictionary<ulong, WordSearchGameState> WordSearchGames = new();

    /// <summary>
    ///     Starts a word search with 8 hidden Pokémon names.
    /// </summary>
    [Command("wordsearch")]
    [Alias("ws")]
    [Summary("Start a word search puzzle with hidden Pokémon names.")]
    public async Task WordSearch()
    {
        try
        {
            var gameState = await Service.InitializeGameAsync(ctx.User.Id);
            WordSearchGames[ctx.User.Id] = gameState;

            var prefix = await GetPrefixAsync();
            await SendBoardAsync(gameState,
                "🔍 **Find the Hidden Pokemon!**\n" +
                $"Guess with `{prefix}game guess <name>`, or give up with `{prefix}game quit`.\n\n" +
                Service.FormatPromptMessage(gameState));
        }
        catch (Exception e)
        {
            Log.Error(e, "Error starting word search game for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while starting the game. Please try again.");
        }
    }

    /// <summary>
    ///     Guesses one of the hidden names in your word search.
    /// </summary>
    /// <param name="guess">The Pokémon name.</param>
    [Command("guess")]
    [Alias("g")]
    [Summary("Guess a hidden Pokémon name in your word search.")]
    public async Task Guess([Remainder] string guess)
    {
        if (!WordSearchGames.TryGetValue(ctx.User.Id, out var gameState))
        {
            await ErrorAsync($"You have no word search running. Start one with `{await GetPrefixAsync()}game wordsearch`.");
            return;
        }

        try
        {
            var result = Service.HandleGuess(gameState, guess);

            if (result.TimedOut || result.GameComplete)
            {
                WordSearchGames.TryRemove(ctx.User.Id, out _);

                var finalEmbed = new EmbedBuilder()
                    .WithTitle("Word Search Game - Complete")
                    .WithDescription(result.Message + "\n\n" +
                                     $"Words found: {gameState.FoundWords.Count}/{gameState.Words.Count}\n" +
                                     $"The hidden words were: {string.Join(", ", gameState.Words.Select(w => $"`{w}`"))}")
                    .WithColor(result.GameComplete ? Color.Green : Color.Red);

                if (result.GameComplete)
                    await missionService.TriggerGameWordSearchCompletedAsync(ctx, gameState.FoundWords.Count);

                await SendAsync(embed: finalEmbed);
            }
            else if (result.Success)
            {
                await SendBoardAsync(gameState, $"✅ {result.Message}\n\n{Service.FormatPromptMessage(gameState)}");
            }
            else
            {
                await ErrorAsync(result.Message);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Error handling word search guess for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while processing your guess.");
        }
    }

    /// <summary>
    ///     Ends your word search early and shows the hidden names.
    /// </summary>
    [Command("quit")]
    [Summary("Give up on your word search.")]
    public async Task Quit()
    {
        if (!WordSearchGames.TryRemove(ctx.User.Id, out var gameState))
        {
            await ErrorAsync("You have no word search running.");
            return;
        }

        gameState.IsActive = false;
        var embed = new EmbedBuilder()
            .WithTitle("Word Search Game - Quit")
            .WithDescription(
                $"Game ended early. You found {gameState.FoundWords.Count} out of {gameState.Words.Count} words.\n\n" +
                $"The hidden words were: {string.Join(", ", gameState.Words.Select(w => $"`{w}`"))}")
            .WithColor(Color.Orange);

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Spins the Pokémon slot machine.
    /// </summary>
    [Command("slots")]
    [Summary("Play the Pokémon slot machine.")]
    public async Task Slots()
    {
        try
        {
            var result = slotMachineService.Spin();
            await missionService.TriggerGameSlotsPlayedAsync(ctx, result.IsWin);

            var embed = new EmbedBuilder()
                .WithTitle("🎰 Pokemon Slot Machine 🎰")
                .WithDescription(result.ResultMessage)
                .WithColor(result.IsWin ? Color.Gold : Color.LightGrey);

            if (result.IsWin)
                embed.AddField("🎉 Congratulations!",
                    $"Win Type: {result.WinType}\n" +
                    $"Payout Multiplier: {result.PayoutMultiplier}x");
            else
                embed.WithFooter("Better luck next time! Try again for a chance to win big!");

            await SendAsync(embed: embed);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error running slot machine for user {UserId}", ctx.User.Id);
            await ErrorAsync("An error occurred while spinning the slot machine.");
        }
    }

    /// <summary>
    ///     Shows the slot machine's payouts and win chances.
    /// </summary>
    [Command("slotinfo")]
    [Alias("slot-info")]
    [Summary("View slot machine win probabilities and payouts.")]
    public async Task SlotInfo()
    {
        var probabilities = slotMachineService.GetWinProbabilities();

        var embed = new EmbedBuilder()
            .WithTitle("🎰 Slot Machine Information")
            .WithDescription("**Win Combinations and Payouts:**")
            .WithColor(Color.Purple)
            .AddField("💰 **Jackpot Wins**",
                "🔥 Triple Seven: 1000x (Jackpot!)\n" +
                "⭐ Triple Star: 500x\n" +
                "🪙 Triple Coin: 250x", true)
            .AddField("🎯 **Good Wins**",
                "🍒 Triple Cherry: 100x\n" +
                "⚡ Triple Pikachu: 75x\n" +
                "🎁 Triple Present: 50x\n" +
                "🟣 Triple Purple: 25x", true)
            .AddField("✨ **Special Wins**",
                "🍀 Lucky Seven: 10x (Any seven)\n" +
                "🎲 Triple Match: 5x (Any three)", true)
            .AddField("📊 **Win Probabilities** (Top 5)",
                string.Join("\n", probabilities.Take(5).Select(p => $"{p.Key}: {p.Value:F3}%")))
            .WithFooter("Good luck spinning! 🎰");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Lists the mini-games.
    /// </summary>
    [Command("list")]
    [Summary("Show all available mini-games.")]
    public async Task List()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🎮 Available Mini-Games")
            .WithDescription("Here are all the fun games you can play to earn rewards!")
            .WithColor(Color.Blue)
            .AddField("🔍 **Word Search**",
                "Find hidden Pokemon names in a letter grid!\n" +
                "Time limit: 200 seconds\n" +
                $"Command: `{prefix}game wordsearch`")
            .AddField("🎰 **Slot Machine**",
                "Spin the Pokemon-themed slot machine for big wins!\n" +
                "Various symbols with different payouts\n" +
                $"Command: `{prefix}game slots`")
            .WithFooter("Have fun and good luck! 🍀");

        await SendAsync(embed: embed);
    }

    /// <summary>
    ///     Explains how the mini-games work.
    /// </summary>
    [Command("help")]
    [Summary("Get help with the games system.")]
    public async Task Help()
    {
        var prefix = await GetPrefixAsync();
        var embed = new EmbedBuilder()
            .WithTitle("🎮 Games Help")
            .WithDescription("Welcome to the mini-games system! Here's how to get started:")
            .WithColor(Color.Green)
            .AddField("🎯 **How to Play**",
                $"• Use `{prefix}game list` to see all available games\n" +
                "• Each game has different rules and rewards\n" +
                "• Some games are single-player, others multiplayer\n" +
                "• Read each game's description before playing!")
            .AddField("🏆 **Rewards**",
                "• Earn credits for winning games\n" +
                "• Get special items from certain games\n" +
                "• Unlock achievements for game milestones\n" +
                "• Compete on leaderboards!")
            .AddField("⚡ **Tips**",
                "• Practice makes perfect!\n" +
                "• Some games have time limits\n" +
                "• Pay attention to game instructions\n" +
                "• Don't be afraid to try different strategies")
            .AddField("🆘 **Need More Help?**",
                "• Join our support community\n" +
                "• Ask questions in community channels\n" +
                "• Check out the full guide online\n" +
                "• Contact staff for technical issues")
            .WithFooter("Have fun gaming! 🎮");

        await SendAsync(embed: embed);
    }

    private async Task SendBoardAsync(WordSearchGameState gameState, string description)
    {
        await using var image = await Service.GenerateWordSearchImageAsync(gameState, Background);
        var fileName = $"wordsearch_{gameState.GameId}.png";
        var embed = new EmbedBuilder()
            .WithTitle("Word Search Game - Find the Pokemon!")
            .WithDescription(description)
            .WithColor(Color.Blue)
            .WithImageUrl($"attachment://{fileName}");

        await SendAsync(embed: embed, attachments: [new AttachmentRequest(image, fileName)]);
    }
}
