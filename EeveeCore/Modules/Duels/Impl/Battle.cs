using EeveeCore.Database.Models.Mongo.Pokemon;
using EeveeCore.Modules.Duels.Services;
using EeveeCore.Modules.Duels.Utils;
using EeveeCore.Services.Impl;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using SkiaSharp;

namespace EeveeCore.Modules.Duels.Impl;

/// <summary>
///     Represents a battle between two trainers and their Pokémon.
///     This class holds all necessary information for a battle and manages its execution.
/// </summary>
public class Battle
{
    private readonly IMongoService _mongoService;

    /// <summary>
    ///     Initializes a new instance of the Battle class.
    ///     Sets up the battle environment, trainers, and their Pokémon.
    /// </summary>
    /// <param name="reactions">The reaction prompts players pick their actions with.</param>
    /// <param name="channel">The message channel where battle messages will be sent.</param>
    /// <param name="trainer1">The first trainer in the battle.</param>
    /// <param name="trainer2">The second trainer in the battle.</param>
    /// <param name="mongoService">The MongoDB service for accessing game data.</param>
    /// <param name="inverseBattle">Whether this is an inverse battle where type effectiveness is reversed.</param>
    public Battle(DuelReactions reactions, Channel channel, Trainer? trainer1, Trainer? trainer2,
        IMongoService mongoService, bool inverseBattle = false)
    {
        Reactions = reactions;
        Channel = channel;
        Trainer1 = trainer1;
        Trainer2 = trainer2;
        _mongoService = mongoService;

        // Initialize items for each Pokemon
        foreach (var poke in trainer1.Party) poke.HeldItem.Battle = this;
        foreach (var poke in trainer2.Party) poke.HeldItem.Battle = this;

        BgNum = new Random().Next(1, 5);
        TrickRoom = new ExpiringEffect(0);
        MagicRoom = new ExpiringEffect(0);
        WonderRoom = new ExpiringEffect(0);
        Gravity = new ExpiringEffect(0);
        Weather = new Weather(this);
        Terrain = new Terrain(this);
        PlasmaFists = false;
        Turn = 0;
        LastMoveEffect = null;
        MetronomeMoves = new List<dynamic>();
        TypeEffectiveness = new Dictionary<(ElementType, ElementType), int>();
        InverseBattle = inverseBattle;
        Msg = "";
    }

    /// <summary>
    ///     Gets the reaction prompts players pick their actions with.
    /// </summary>
    public DuelReactions Reactions { get; }

    /// <summary>
    ///     Gets the message channel where battle messages will be sent.
    /// </summary>
    public Channel Channel { get; }

    /// <summary>
    ///     Gets the first trainer in the battle.
    /// </summary>
    public Trainer? Trainer1 { get; }

    /// <summary>
    ///     Gets the second trainer in the battle.
    /// </summary>
    public Trainer? Trainer2 { get; }

    /// <summary>
    ///     Gets the background number used for the battle scene.
    /// </summary>
    public int BgNum { get; private set; }

    /// <summary>
    ///     Gets the Trick Room effect, which reverses speed priority.
    /// </summary>
    public ExpiringEffect TrickRoom { get; }

    /// <summary>
    ///     Gets the Magic Room effect, which nullifies held items.
    /// </summary>
    public ExpiringEffect MagicRoom { get; }

    /// <summary>
    ///     Gets the Wonder Room effect, which swaps Defense and Special Defense.
    /// </summary>
    public ExpiringEffect WonderRoom { get; }

    /// <summary>
    ///     Gets the Gravity effect, which affects certain moves and abilities.
    /// </summary>
    public ExpiringEffect Gravity { get; }

    /// <summary>
    ///     Gets the current weather condition in the battle.
    /// </summary>
    public Weather Weather { get; }

    /// <summary>
    ///     Gets the current terrain effect in the battle.
    /// </summary>
    public Terrain Terrain { get; }

    /// <summary>
    ///     Gets or sets a value indicating whether the Plasma Fists effect is active,
    ///     which changes Normal-type moves to Electric-type.
    /// </summary>
    public bool PlasmaFists { get; set; }

    /// <summary>
    ///     Gets or sets the current turn number of the battle.
    /// </summary>
    public int Turn { get; set; }

    /// <summary>
    ///     Gets or sets the effect ID of the last move used, for moves that check previous move effects.
    /// </summary>
    public int? LastMoveEffect { get; set; }

    /// <summary>
    ///     Gets the list of moves available for Metronome to select from.
    /// </summary>
    public List<dynamic> MetronomeMoves { get; private set; }

    /// <summary>
    ///     Gets the type effectiveness chart for this battle.
    ///     Maps (attacking type, defending type) tuples to effectiveness percentages.
    /// </summary>
    public Dictionary<(ElementType, ElementType), int> TypeEffectiveness { get; }

    /// <summary>
    ///     Gets a value indicating whether this is an inverse battle where type effectiveness is reversed.
    /// </summary>
    public bool InverseBattle { get; private set; }

    /// <summary>
    ///     Gets or sets the current message buffer for battle text.
    /// </summary>
    public string Msg { get; set; }

    /// <summary>
    ///     Sends the turn message: the battle image and each player's moves and options, with reactions to pick
    ///     them, and starts listening for the players' picks.
    /// </summary>
    /// <param name="renderer">The renderer used to generate the battle image.</param>
    /// <returns>The sent message for later reference.</returns>
    public async Task<Message> GenerateMainBattleMessage(DuelRenderer renderer)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"Battle between {Trainer1.Name} and {Trainer2.Name}")
            .WithColor(new Color(255, 182, 193))
            .WithFooter("React or type a move number or name, swap, mega or forfeit")
            .WithImageUrl("attachment://battle.png");

        foreach (var (trainer, opponent) in new[] { (Trainer1, Trainer2), (Trainer2, Trainer1) })
        {
            if (!trainer.IsHuman())
                continue;
            var (name, value) = DuelReactions.ActionsField(trainer, opponent, this);
            embed.AddField(name, value, true);
        }

        using var battleImage = await renderer.GenerateBattleImage(this);
        var memoryStream = new MemoryStream();
        battleImage.Encode(SKEncodedImageFormat.Png, 100).SaveTo(memoryStream);
        memoryStream.Position = 0;

        var message = await Channel.SendMessageAsync(embeds: [embed.Build()],
            attachments: [new AttachmentRequest(memoryStream, "battle.png")]);
        _ = Reactions.RunTurnAsync(this, message);
        return message;
    }

    /// <summary>
    ///     Runs the entire battle from start to finish.
    ///     Manages turn flow, move execution, and determines the winner.
    /// </summary>
    /// <returns>The winning trainer, or null if there was no clear winner.</returns>
    public async Task<Trainer?> Run()
    {
        Msg = "";

        // Moves which are immune to metronome (implementation pending)
        var immuneIds = new List<int>
        {
            68, 102, 119, 144, 165, 166, 168, 173, 182, 194, 197, 203, 214, 243, 264, 266,
            267, 270, 271, 274, 289, 343, 364, 382, 383, 415, 448, 469, 476, 495, 501, 511,
            516, 546, 547, 548, 553, 554, 555, 557, 561, 562, 578, 588, 591, 592, 593, 596,
            606, 607, 614, 615, 617, 621, 661, 671, 689, 690, 704, 705, 712, 720, 721, 722
        };

        // Moves which are not coded in the bot (implementation pending)
        var uncodedIds = new List<int>
        {
            266, 270, 476, 495, 502, 511, 597, 602, 603, 607, 622, 623, 624, 625, 626, 627,
            628, 629, 630, 631, 632, 633, 634, 635, 636, 637, 638, 639, 640, 641, 642, 643,
            644, 645, 646, 647, 648, 649, 650, 651, 652, 653, 654, 655, 656, 657, 658, 671,
            695, 696, 697, 698, 699, 700, 701, 702, 703, 719, 723, 724, 725, 726, 727, 728,
            811, 10001, 10002, 10003, 10004, 10005, 10006, 10007, 10008, 10009, 10010, 10011,
            10012, 10013, 10014, 10015, 10016, 10017, 10018
        };

        // Combine ignored move IDs
        var ignoredIds = immuneIds.Union(uncodedIds).ToList();

        // Load move data and type effectiveness
        var moves = await _mongoService.Moves.AsQueryable()
            .Where(m => !ignoredIds.Contains(m.MoveId))
            .ToListAsync();
        
        // Convert to dynamic list for compatibility with existing code
        MetronomeMoves = moves.Cast<dynamic>().ToList();

        var typeEffectivenessData = await _mongoService.TypeEffectiveness.Find(Builders<TypeEffectiveness>.Filter.Empty)
            .ToListAsync();

        // Populate the type effectiveness dictionary
        foreach (var te in typeEffectivenessData)
            TypeEffectiveness[(
                (ElementType)te.DamageTypeId,
                (ElementType)te.TargetTypeId
            )] = te.DamageFactor;

        // Initial Pokemon send-out
        if (Trainer1.CurrentPokemon.GetRawSpeed() > Trainer2.CurrentPokemon.GetRawSpeed())
        {
            Msg += Trainer1.CurrentPokemon.SendOut(Trainer2.CurrentPokemon, this);
            Msg += Trainer2.CurrentPokemon.SendOut(Trainer1.CurrentPokemon, this);
        }
        else
        {
            Msg += Trainer2.CurrentPokemon.SendOut(Trainer1.CurrentPokemon, this);
            Msg += Trainer1.CurrentPokemon.SendOut(Trainer2.CurrentPokemon, this);
        }

        await SendMsg();

        Trainer? winner = null;

        // Main battle loop
        while (true)
        {
            // Swap pokes for any users w/o an active poke
            while (Trainer1.CurrentPokemon == null || Trainer2.CurrentPokemon == null)
            {
                var swapped1 = false;
                var swapped2 = false;

                if (Trainer1.CurrentPokemon == null)
                {
                    swapped1 = true;
                    winner = await RunSwap(Trainer1, Trainer2);
                    if (winner != null)
                        break;
                }

                if (Trainer2.CurrentPokemon == null)
                {
                    swapped2 = true;
                    winner = await RunSwap(Trainer2, Trainer1);
                    if (winner != null)
                        break;
                }

                // Send out the pokes that were just swapped to
                if (swapped1 && swapped2)
                {
                    if (Trainer1.CurrentPokemon.GetRawSpeed() > Trainer2.CurrentPokemon.GetRawSpeed())
                    {
                        Msg += Trainer1.CurrentPokemon.SendOut(Trainer2.CurrentPokemon, this);
                        if (!Trainer1.HasAlivePokemon())
                        {
                            Msg += $"{Trainer2.Name} wins!\n";
                            winner = Trainer2;
                            break;
                        }

                        Msg += Trainer2.CurrentPokemon.SendOut(Trainer1.CurrentPokemon, this);
                        if (!Trainer2.HasAlivePokemon())
                        {
                            Msg += $"{Trainer1.Name} wins!\n";
                            winner = Trainer1;
                            break;
                        }
                    }
                    else
                    {
                        Msg += Trainer2.CurrentPokemon.SendOut(Trainer1.CurrentPokemon, this);
                        if (!Trainer2.HasAlivePokemon())
                        {
                            Msg += $"{Trainer1.Name} wins!\n";
                            winner = Trainer1;
                            break;
                        }

                        Msg += Trainer1.CurrentPokemon.SendOut(Trainer2.CurrentPokemon, this);
                        if (!Trainer1.HasAlivePokemon())
                        {
                            Msg += $"{Trainer2.Name} wins!\n";
                            winner = Trainer2;
                            break;
                        }
                    }
                }
                else if (swapped1)
                {
                    Msg += Trainer1.CurrentPokemon.SendOut(Trainer2.CurrentPokemon, this);
                    if (!Trainer1.HasAlivePokemon())
                    {
                        Msg += $"{Trainer2.Name} wins!\n";
                        winner = Trainer2;
                        break;
                    }
                }
                else if (swapped2)
                {
                    Msg += Trainer2.CurrentPokemon.SendOut(Trainer1.CurrentPokemon, this);
                    if (!Trainer2.HasAlivePokemon())
                    {
                        Msg += $"{Trainer1.Name} wins!\n";
                        winner = Trainer1;
                        break;
                    }
                }
            }

            // Handle breaking out of the main game loop when a winner happens in the poke select loop
            if (winner != null)
                break;

            // Get trainer actions
            await SendMsg();

            Trainer1.Event = new TaskCompletionSource<bool>();
            Trainer2.Event = new TaskCompletionSource<bool>();

            if (!Trainer1.IsHuman()) ((NPCTrainer)Trainer1).Move(Trainer2.CurrentPokemon, this);
            if (!Trainer2.IsHuman()) ((NPCTrainer)Trainer2).Move(Trainer1.CurrentPokemon, this);

            var renderer = new DuelRenderer(_mongoService);

            var battleView = await GenerateMainBattleMessage(renderer);
            await Trainer1.Event.Task;
            await Trainer2.Event.Task;

            // Check for forfeits
            if (Trainer1.SelectedAction == null && Trainer2.SelectedAction == null)
            {
                await Channel.SendMessageAsync("Both players forfeited...");
                return null; // TODO: handle this case better
            }

            if (Trainer1.SelectedAction == null)
            {
                Msg += $"{Trainer1.Name} forfeited, {Trainer2.Name} wins!\n";
                winner = Trainer2;
                break;
            }

            if (Trainer2.SelectedAction == null)
            {
                Msg += $"{Trainer2.Name} forfeited, {Trainer1.Name} wins!\n";
                winner = Trainer1;
                break;
            }

            // Run setup for both pokemon
            var (t1, t2) = WhoFirst();
            if (t1.CurrentPokemon != null && t2.CurrentPokemon != null)
                if (!t1.SelectedAction.IsSwitch)
                    Msg += ((Trainer.MoveAction)t1.SelectedAction).Move.Setup(t1.CurrentPokemon, t2.CurrentPokemon,
                        this);

            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            if (t1.CurrentPokemon != null && t2 is { CurrentPokemon: not null, SelectedAction.IsSwitch: false })
                Msg += ((Trainer.MoveAction)t2.SelectedAction).Move.Setup(t2.CurrentPokemon, t1.CurrentPokemon,
                    this);

            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            // Run moves for both pokemon
            // Trainer 1's move
            var ranMegas = false;
            if (!t1.SelectedAction.IsSwitch)
            {
                HandleMegas(t1, t2);
                ranMegas = true;
            }

            if (t1.CurrentPokemon != null && t2.CurrentPokemon != null)
            {
                if (t1.SelectedAction is Trainer.SwitchAction action)
                {
                    Msg += t1.CurrentPokemon.Remove(this);
                    t1.SwitchPoke(action.SwitchIndex, true);
                    Msg += t1.CurrentPokemon.SendOut(t2.CurrentPokemon, this);
                    if (t1.CurrentPokemon != null) t1.CurrentPokemon.HasMoved = true;
                }
                else
                {
                    Msg += ((Trainer.MoveAction)t1.SelectedAction).Move.Use(t1.CurrentPokemon, t2.CurrentPokemon, this);
                }
            }

            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            // Pokes who die do NOT get attacked, but pokes who retreat *do*
            if (t1.MidTurnRemove)
            {
                winner = await RunSwap(t1, t2, true);
                if (winner != null)
                    break;
            }

            // EDGE CASE - Moves that DO NOT target the opponent (and swapping) SHOULD run
            // even if there is no other poke on the field.
            if (t1.CurrentPokemon == null && t2.CurrentPokemon != null &&
                (t2.SelectedAction.IsSwitch || !(t2.SelectedAction as Trainer.MoveAction).Move.TargetsOpponent()))
            {
                winner = await RunSwap(t1, t2, true);
                if (winner != null)
                    break;
            }

            Msg += "\n";

            // Trainer 2's move
            if (!ranMegas && !t2.SelectedAction.IsSwitch)
            {
                HandleMegas(t1, t2);
                ranMegas = true;
            }

            if (t1.CurrentPokemon != null && t2.CurrentPokemon != null)
            {
                if (t2.SelectedAction.IsSwitch)
                {
                    Msg += t2.CurrentPokemon.Remove(this);
                    t2.SwitchPoke((t2.SelectedAction as Trainer.SwitchAction).SwitchIndex, true);
                    Msg += t2.CurrentPokemon.SendOut(t1.CurrentPokemon, this);
                    if (t2.CurrentPokemon != null) t2.CurrentPokemon.HasMoved = true;
                }
                else
                {
                    Msg += ((Trainer.MoveAction)t2.SelectedAction).Move.Use(t2.CurrentPokemon, t1.CurrentPokemon, this);
                }
            }

            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            Msg += "\n";
            if (t2.MidTurnRemove)
            {
                // This DOES need to be here, otherwise end of turn effects aren't handled right
                winner = await RunSwap(t2, t1, true);
                if (winner != null)
                    break;
            }

            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            if (!ranMegas) HandleMegas(t1, t2);

            // Progress turns
            Turn += 1;
            PlasmaFists = false;
            if (Weather.NextTurn()) Msg += "The weather cleared!\n";
            if (Terrain.NextTurn()) Msg += "The terrain cleared!\n";
            LastMoveEffect = null;

            (t1, t2) = WhoFirst(false);
            Msg += t1.NextTurn(this);
            if (t1.CurrentPokemon != null) Msg += t1.CurrentPokemon.NextTurn(t2.CurrentPokemon, this);
            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            Msg += t2.NextTurn(this);
            if (t2.CurrentPokemon != null) Msg += t2.CurrentPokemon.NextTurn(t1.CurrentPokemon, this);
            if (!t2.HasAlivePokemon())
            {
                Msg += $"{t1.Name} wins!\n";
                winner = t1;
                break;
            }

            if (!t1.HasAlivePokemon())
            {
                Msg += $"{t2.Name} wins!\n";
                winner = t2;
                break;
            }

            if (TrickRoom.NextTurn()) Msg += "The Dimensions returned back to normal!\n";
            if (Gravity.NextTurn()) Msg += "Gravity returns to normal!\n";
            if (MagicRoom.NextTurn()) Msg += "The room returns to normal, and held items regain their effect!\n";
            if (WonderRoom.NextTurn())
                Msg += "The room returns to normal, and stats swap back to what they were before!\n";
        }

        // The game is over, and we broke out before sending, send the remaining cache
        await SendMsg();
        return winner;
    }

    /// <summary>
    ///     Determines which trainer's move should be executed first based on speed, priority, and other factors.
    ///     Returns the two trainers in the order they should act.
    /// </summary>
    /// <param name="checkMove">Whether to check move properties when determining order.</param>
    /// <returns>A tuple of trainers in the order they should act.</returns>
    public (Trainer?, Trainer) WhoFirst(bool checkMove = true)
    {
        (Trainer? Trainer1, Trainer Trainer2) T1FIRST = (Trainer1, Trainer2);
        (Trainer? Trainer2, Trainer Trainer1) T2FIRST = (Trainer2, Trainer1);

        if (Trainer1.CurrentPokemon == null || Trainer2.CurrentPokemon == null) return T1FIRST;

        var speed1 = Trainer1.CurrentPokemon.GetSpeed(this);
        var speed2 = Trainer2.CurrentPokemon.GetSpeed(this);

        // Pokes that are switching go before pokes making other moves
        if (checkMove)
        {
            switch (Trainer1.SelectedAction)
            {
                case Trainer.SwitchAction when Trainer2.SelectedAction is Trainer.SwitchAction:
                {
                    if (Trainer1.CurrentPokemon.GetRawSpeed() > Trainer2.CurrentPokemon.GetRawSpeed()) return T1FIRST;
                    return T2FIRST;
                }
                case Trainer.SwitchAction:
                    return T1FIRST;
            }

            if (Trainer2.SelectedAction.IsSwitch) return T2FIRST;
        }

        // Priority brackets & abilities
        if (checkMove)
        {
            var prio1 = (Trainer1.SelectedAction as Trainer.MoveAction).Move.GetPriority(Trainer1.CurrentPokemon,
                Trainer2.CurrentPokemon, this);
            var prio2 = (Trainer2.SelectedAction as Trainer.MoveAction).Move.GetPriority(Trainer2.CurrentPokemon,
                Trainer1.CurrentPokemon, this);

            if (prio1 > prio2) return T1FIRST;
            if (prio2 > prio1) return T2FIRST;

            var t1Quick = false;
            var t2Quick = false;

            // Quick draw/claw
            if (Trainer1.CurrentPokemon.Ability() == Ability.QUICK_DRAW &&
                (Trainer1.SelectedAction as Trainer.MoveAction).Move.DamageClass != DamageClass.STATUS &&
                new Random().Next(1, 101) <= 30)
                t1Quick = true;
            if (Trainer2.CurrentPokemon.Ability() == Ability.QUICK_DRAW &&
                (Trainer2.SelectedAction as Trainer.MoveAction).Move.DamageClass != DamageClass.STATUS &&
                new Random().Next(1, 101) <= 30)
                t2Quick = true;
            if (Trainer1.CurrentPokemon.HeldItem == "quick-claw" &&
                new Random().Next(1, 101) <= 20)
                t1Quick = true;
            if (Trainer2.CurrentPokemon.HeldItem == "quick-claw" &&
                new Random().Next(1, 101) <= 20)
                t2Quick = true;

            // If both pokemon activate a quick, priority bracket proceeds as normal
            if (t1Quick && !t2Quick) return T1FIRST;
            if (t2Quick && !t1Quick) return T2FIRST;

            // OldMove last in prio bracket
            var t1Slow = false;
            var t2Slow = false;

            if (Trainer1.CurrentPokemon.Ability() == Ability.STALL) t1Slow = true;
            if (Trainer1.CurrentPokemon.Ability() == Ability.MYCELIUM_MIGHT &&
                (Trainer1.SelectedAction as Trainer.MoveAction).Move.DamageClass == DamageClass.STATUS)
                t1Slow = true;
            if (Trainer2.CurrentPokemon.Ability() == Ability.STALL) t2Slow = true;
            if (Trainer2.CurrentPokemon.Ability() == Ability.MYCELIUM_MIGHT &&
                (Trainer2.SelectedAction as Trainer.MoveAction).Move.DamageClass == DamageClass.STATUS)
                t2Slow = true;

            switch (t1Slow)
            {
                case true when t2Slow:
                {
                    if (speed1 == speed2) return new Random().Next(2) == 0 ? T1FIRST : T2FIRST;
                    return speed1 > speed2 ? T2FIRST : T1FIRST;
                }
                case true:
                    return T2FIRST;
            }

            if (t2Slow) return T1FIRST;
        }

        // Equal speed
        if (speed1 == speed2) return new Random().Next(2) == 0 ? T1FIRST : T2FIRST;

        // Trick room
        if (TrickRoom.Active()) return speed1 > speed2 ? T2FIRST : T1FIRST;

        // Default handling
        return speed1 > speed2 ? T1FIRST : T2FIRST;
    }

    /// <summary>
    ///     Sends the accumulated battle message as embeds.
    ///     Handles splitting messages that exceed Discord's character limit.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SendMsg()
    {
        if (string.IsNullOrEmpty(Msg))
            return;

        var page = "";
        var pages = new List<EmbedRequest>();
        var baseEmbed = new EmbedBuilder().WithColor(new Color(255, 182, 193));
        var rawLines = Msg.Trim().Split('\n');

        foreach (var part in rawLines)
        {
            if (page.Length + part.Length > 2000)
            {
                var embed = new EmbedBuilder()
                    .WithColor(new Color(255, 182, 193))
                    .WithDescription(page.Trim());
                pages.Add(embed.Build());
                page = "";
            }

            page += part + "\n";
        }

        page = page.Trim();
        if (!string.IsNullOrEmpty(page))
        {
            var embed = new EmbedBuilder()
                .WithColor(new Color(255, 182, 193))
                .WithDescription(page);
            pages.Add(embed.Build());
        }

        foreach (var embedPage in pages) await Channel.SendMessageAsync(embeds: [embedPage]);

        Msg = ""; // Clear the message after sending
    }

    /// <summary>
    ///     Handles Pokémon swapping when a trainer needs to select a new Pokémon.
    ///     Prompts the trainer to choose and manages the swap process.
    /// </summary>
    /// <param name="swapper">The trainer who needs to select a new Pokémon.</param>
    /// <param name="otherTrainer">The opposing trainer.</param>
    /// <param name="midTurn">Whether the swap is occurring mid-turn rather than at the beginning of a turn.</param>
    /// <returns>The winning trainer if the swapper fails to select a Pokémon, or null if the swap succeeds.</returns>
    public async Task<Trainer?> RunSwap(Trainer? swapper, Trainer? otherTrainer, bool midTurn = false)
    {
        await SendMsg();

        swapper.Event = new TaskCompletionSource<bool>();

        if (swapper.IsHuman())
        {
            if (await Reactions.PickSwapAsync(this, swapper, otherTrainer, midTurn) is not { } slot)
            {
                Msg += $"{swapper.Name} did not select a poke, {otherTrainer.Name} wins!\n";
                return otherTrainer;
            }

            swapper.SwitchPoke(slot, midTurn);
            swapper.Event.TrySetResult(true);
        }
        else
        {
            // NPC trainers use their own swap logic
            ((NPCTrainer)swapper).Swap(otherTrainer.CurrentPokemon, this, midTurn);

            try
            {
                await swapper.Event.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException)
            {
                Msg += $"{swapper.Name} did not select a poke, {otherTrainer.Name} wins!\n";
                return otherTrainer;
            }
        }

        // Check if a Pokemon was actually selected
        if (swapper.CurrentPokemon == null)
        {
            Msg += $"{swapper.Name} did not select a poke, {otherTrainer.Name} wins!\n";
            return otherTrainer;
        }

        // Handle mid-turn effects
        if (midTurn)
        {
            Msg += swapper.CurrentPokemon.SendOut(otherTrainer.CurrentPokemon, this);
            if (swapper.CurrentPokemon != null) swapper.CurrentPokemon.HasMoved = true;
        }

        return null;
    }

    /// <summary>
    ///     Handles Mega Evolution of eligible Pokémon.
    ///     Applies form changes, ability changes, and type changes.
    /// </summary>
    /// <param name="t1">The first trainer in the battle.</param>
    /// <param name="t2">The second trainer in the battle.</param>
    public void HandleMegas(Trainer? t1, Trainer? t2)
    {
        foreach (var (at, dt) in new[] { (t1, t2), (t2, t1) })
            if (at.CurrentPokemon is { ShouldMegaEvolve: true })
            {
                // Bit of a hack, since it is in its mega form and dashes are removed from `name`, it will show as "<poke> mega evolved!".
                if ((at.CurrentPokemon.HeldItem.Name == "mega-stone" || at.CurrentPokemon._name == "Rayquaza") &&
                    at.CurrentPokemon.Form(at.CurrentPokemon._name + "-mega"))
                    Msg += $"{at.CurrentPokemon.DisplayName} evolved!\n";
                else if (at.CurrentPokemon.HeldItem.Name == "mega-stone-x" &&
                         at.CurrentPokemon.Form(at.CurrentPokemon._name + "-mega-x"))
                    Msg += $"{at.CurrentPokemon.DisplayName} evolved!\n";
                else if (at.CurrentPokemon.HeldItem.Name == "mega-stone-y" &&
                         at.CurrentPokemon.Form(at.CurrentPokemon._name + "-mega-y"))
                    Msg += $"{at.CurrentPokemon.DisplayName} evolved!\n";
                else
                    throw new InvalidOperationException("expected to mega evolve but no valid mega condition");

                at.CurrentPokemon.AbilityId = at.CurrentPokemon.MegaAbilityId;
                at.CurrentPokemon.StartingAbilityId = at.CurrentPokemon.MegaAbilityId;
                at.CurrentPokemon.TypeIds = new List<ElementType>(at.CurrentPokemon.MegaTypeIds);
                at.CurrentPokemon.StartingTypeIds = new List<ElementType>(at.CurrentPokemon.MegaTypeIds);
                Msg += at.CurrentPokemon.SendOutAbility(dt.CurrentPokemon, this);
                at.HasMegaEvolved = true;
            }
    }
}