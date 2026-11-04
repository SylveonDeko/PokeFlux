using System.Diagnostics;
using System.Reflection;
using EeveeCore.Common.ModuleBehaviors;
using Fluxer.Net.Gateway;
using Serilog;

namespace EeveeCore;

/// <summary>
///     Starts the bot: connects to Fluxer, runs database migrations, loads the text command modules and runs the
///     services that wait for the bot to be ready.
/// </summary>
public class EeveeCore
{
    /// <summary>
    ///     Initializes a new instance of the EeveeCore class.
    /// </summary>
    /// <param name="services">The service provider for dependency injection.</param>
    public EeveeCore(IServiceProvider services)
    {
        Services = services;
        Credentials = Services.GetRequiredService<BotCredentials>();
        Client = Services.GetRequiredService<FluxerClient>();
        CommandService = Services.GetRequiredService<CommandService>();
    }

    /// <summary>
    ///     Gets the credentials used by the bot.
    /// </summary>
    public BotCredentials Credentials { get; }

    /// <summary>
    ///     Gets the Fluxer client used by the bot.
    /// </summary>
    public FluxerClient Client { get; }

    private CommandService CommandService { get; }

    /// <summary>
    ///     Gets or sets the color used for successful operations.
    /// </summary>
    public static Color OkColor { get; set; } = new(67, 160, 71);

    /// <summary>
    ///     Gets or sets the color used for error operations.
    /// </summary>
    public static Color ErrorColor { get; set; } = new(229, 57, 53);

    /// <summary>
    ///     Gets a TaskCompletionSource that completes when the bot is ready.
    /// </summary>
    public TaskCompletionSource<bool> Ready { get; } = new();

    private IServiceProvider Services { get; }

    private void LoadTypeReaders(Assembly assembly)
    {
        var sw = Stopwatch.StartNew();

        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.Warning(ex.LoaderExceptions[0], "Error getting types");
            return;
        }

        foreach (var type in allTypes.Where(x => x.IsSubclassOf(typeof(TypeReader))
                                                 && x.BaseType?.GetGenericArguments().Length > 0
                                                 && !x.IsAbstract))
        {
            var reader = (TypeReader)ActivatorUtilities.CreateInstance(Services, type);
            CommandService.AddTypeReader(type.BaseType!.GetGenericArguments()[0], reader);
        }

        Log.Information("TypeReaders loaded in {ElapsedTotalSeconds}s", sw.Elapsed.TotalSeconds);
    }

    private async Task LoginAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Client.Gateway.Ready += _ => ready.TrySetResult();

        Log.Information("Logging in...");
        try
        {
            await Client.Gateway.ConnectAsync(new FluxerGatewayConfig
            {
                Cache = new FluxerCacheConfig
                {
                    Messages = new CacheLimits
                    {
                        MaxPerChannel = 15, Expiry = TimeSpan.FromHours(6)
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Login failed");
            throw;
        }

        await ready.Task.ConfigureAwait(false);
        Client.Gateway.GuildJoined += OnGuildJoinedAsync;
        Client.Gateway.GuildLeft += OnGuildLeftAsync;
        Log.Information("Logged in as {User}", Client.Gateway.CurrentUser?.Username);
    }

    private async Task OnGuildJoinedAsync(SocketGuild guild)
    {
        Log.Information("Joined server: {Name} [{Id}]", guild.Name, guild.Id);
        await SendGuildLogAsync(new EmbedBuilder()
            .WithTitle($"Joined {guild.Name} {guild.Id}")
            .AddField("Members", guild.MemberCount)
            .AddField("Owner", guild.OwnerId)
            .AddField("Total communities", Client.Gateway.Guilds.Count)
            .WithThumbnailUrl(guild.GetIconUrl())
            .WithOkColor());
    }

    private async Task OnGuildLeftAsync(SocketGuild guild)
    {
        Log.Information("Left server: {Name} [{Id}]", guild.Name, guild.Id);
        await SendGuildLogAsync(new EmbedBuilder()
            .WithTitle($"Left community: {guild.Name} [{guild.Id}]")
            .AddField("Total communities", Client.Gateway.Guilds.Count)
            .WithErrorColor());
    }

    private async Task SendGuildLogAsync(EmbedBuilder embed)
    {
        if (Credentials.GuildJoinsChannelId == 0)
            return;

        try
        {
            await Client.Rest.SendMessageAsync(Credentials.GuildJoinsChannelId, embeds: [embed.Build()])
                .ConfigureAwait(false);
        }
        catch (FluxerApiException ex)
        {
            Log.Warning(ex, "Could not post to the server join log channel");
        }
    }

    /// <summary>
    ///     Runs the bot, initializing all necessary components and services.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunAsync()
    {
        var sw = Stopwatch.StartNew();

        await LoginAsync().ConfigureAwait(false);

        Log.Information("Loading Services...");
        try
        {
            LoadTypeReaders(typeof(EeveeCore).Assembly);

            var migrator = Services.GetRequiredService<DatabaseMigrator>();
            if (!await migrator.MigrateAsync())
                throw new InvalidOperationException("Database migration failed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error initializing services");
            throw;
        }

        Log.Information("Connected in {Elapsed:F2}s", sw.Elapsed.TotalSeconds);

        try
        {
            await CommandService.AddModulesAsync(Assembly.GetExecutingAssembly(), Services);
        }
        catch (Exception e)
        {
            Log.Error(e, "Error loading modules");
            throw;
        }

        Services.GetRequiredService<CommandHandler>();
        await ExecuteReadySubscriptions();
        Ready.TrySetResult(true);
        Log.Information("Ready.");
    }

    private async Task ExecuteReadySubscriptions()
    {
        var tasks = Services.GetServices<IReadyExecutor>().Select(async toExec =>
        {
            try
            {
                await toExec.OnReadyAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed running OnReadyAsync method on {Type} type: {Message}",
                    toExec.GetType().Name, ex.Message);
            }
        });
        await Task.WhenAll(tasks);
    }
}
