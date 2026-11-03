using EeveeCore.Modules.Duels.Impl;
using EeveeCore.Modules.Duels.Impl.DuelPokemon;
using EeveeCore.Modules.Duels.Utils;
using EeveeCore.Services.Impl;
using MongoDB.Driver;
using SkiaSharp;

namespace EeveeCore.Modules.Duels.Services;

/// <summary>
///     Provides functionality for rendering Pokémon battles as images.
///     Handles generating visual elements like team previews, battle scenes, weather effects,
///     and Pokémon status displays using SkiaSharp graphics.
/// </summary>
public class DuelRenderer(IMongoService mongoService) : INService
{
    private const string ResourcePath = "data/";
    private readonly Dictionary<string, SKBitmap> _imageCache = new();


    /// <summary>
    ///     Generates and sends a team preview image as a Discord message.
    ///     Creates an embed with the team preview image and components for selecting a lead Pokémon.
    /// </summary>
    /// <param name="battle">The Battle object containing the trainers and their parties.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the sent message.
    /// </returns>
    /// <remarks>
    ///     Players pick their lead by reacting with its number. Each trainer's event completes once they have.
    /// </remarks>
    public async Task<Message> GenerateTeamPreview(Battle battle)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Pokemon Battle accepted!")
            .WithDescription("Team Preview")
            .WithColor(new Color(255, 182, 193))
            .WithFooter("React or type the number of the Pokémon to lead with")
            .WithImageUrl("attachment://team_preview.png");

        foreach (var trainer in new[] { battle.Trainer1, battle.Trainer2 })
            embed.AddField(trainer.Name, string.Join("\n", trainer.Party.Select((p, i) =>
                $"{DuelReactions.Numbers[i]} {p.Name} | {p.Hp}hp")), true);

        using var previewImage = await GenerateTeamPreviewImage(battle);
        var memoryStream = new MemoryStream();
        previewImage.Encode(SKEncodedImageFormat.Png, 100).SaveTo(memoryStream);
        memoryStream.Position = 0;

        var message = await battle.Channel.SendMessageAsync(embeds: [embed.Build()],
            attachments: [new AttachmentRequest(memoryStream, "team_preview.png")]);
        _ = battle.Reactions.PickLeadsAsync(battle, message);
        return message;
    }

    /// <summary>
    ///     Generates a team preview image showing both trainers' Pokémon parties.
    ///     Creates a stylized background with trainer names and Pokémon sprites.
    /// </summary>
    /// <param name="battle">The Battle object containing the trainers and their parties.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the generated SKImage.
    /// </returns>
    private async Task<SKImage> GenerateTeamPreviewImage(Battle battle)
    {
        const int width = 800;
        const int height = 400;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;

        // Fill background with Pokémon style pattern
        DrawPokemonStyleBackground(canvas, width, height);

        // Draw header
        using var titleFont = new SKFont();
        titleFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        titleFont.Size = 32;
        using var titlePaint = new SKPaint();
        titlePaint.Color = SKColors.Black;
        titlePaint.IsAntialias = true;

        const string titleText = "Team Preview";
        var titleWidth = UnicodeTextRenderer.MeasureText(titleText, titleFont);
        UnicodeTextRenderer.DrawText(canvas, width / 2 - titleWidth / 2, 40, titleText, titleFont, titlePaint);

        // Draw trainer names
        using var trainerFont = new SKFont();
        trainerFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        trainerFont.Size = 24;
        using var trainerPaint = new SKPaint();
        trainerPaint.IsAntialias = true;

        // Draw trainer 1 name in red
        trainerPaint.Color = new SKColor(255, 0, 0);
        UnicodeTextRenderer.DrawText(canvas, 150, 80, battle.Trainer1.Name, trainerFont, trainerPaint);

        // Draw trainer 2 name in blue
        trainerPaint.Color = new SKColor(0, 0, 255);
        var trainer2Width = UnicodeTextRenderer.MeasureText(battle.Trainer2.Name, trainerFont);
        UnicodeTextRenderer.DrawText(canvas, width - 150 - trainer2Width, 80, battle.Trainer2.Name, trainerFont, trainerPaint);

        // Draw dividing line
        using var linePaint = new SKPaint();
        linePaint.Color = SKColors.Black;
        linePaint.StrokeWidth = 2;
        linePaint.IsAntialias = true;
        linePaint.Style = SKPaintStyle.Stroke;
        canvas.DrawLine(width / 2, 60, width / 2, height - 20, linePaint);

        // Draw trainer 1's Pokémon
        await DrawTrainerTeam(canvas, battle.Trainer1, 50, 100, 300, 280);

        // Draw trainer 2's Pokémon
        await DrawTrainerTeam(canvas, battle.Trainer2, 450, 100, 300, 280);

        return surface.Snapshot();
    }

    /// <summary>
    ///     Draws a Pokémon-style patterned background on the canvas.
    ///     Creates a gradient background with subtle pattern overlay similar to Pokémon games.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawPokemonStyleBackground(SKCanvas canvas, int width, int height)
    {
        // Draw gradient background similar to Pokémon games
        using var bgPaint = new SKPaint();
        bgPaint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, height),
            [new SKColor(226, 246, 255), new SKColor(187, 227, 255)],
            null,
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, width, height, bgPaint);

        // Draw a subtle pattern overlay
        using var patternPaint = new SKPaint();
        patternPaint.Color = new SKColor(255, 255, 255, 20);
        patternPaint.IsAntialias = true;

        for (var i = 0; i < width; i += 20)
        for (var j = 0; j < height; j += 20)
            canvas.DrawCircle(i, j, 1, patternPaint);
    }

    /// <summary>
    ///     Draws a trainer's team of Pokémon on the canvas.
    ///     Displays each Pokémon's sprite and information in a stylized box.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="trainer">The trainer whose team to draw.</param>
    /// <param name="x">The starting X position for drawing the team.</param>
    /// <param name="y">The starting Y position for drawing the team.</param>
    /// <param name="width">The width allocated for the team display.</param>
    /// <param name="height">The height allocated for the team display.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task DrawTrainerTeam(SKCanvas canvas, Trainer? trainer, int x, int y, int width, int height)
    {
        const int pokemonPerRow = 2;
        var pokemonWidth = width / pokemonPerRow;
        var pokemonHeight = height / 3;

        using var nameFont = new SKFont();
        nameFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        nameFont.Size = 18;
        using var namePaint = new SKPaint();
        namePaint.Color = SKColors.Black;
        namePaint.IsAntialias = true;

        using var levelFont = new SKFont();
        levelFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        levelFont.Size = 14;
        using var levelPaint = new SKPaint();
        levelPaint.Color = SKColors.DarkGray;
        levelPaint.IsAntialias = true;

        for (var i = 0; i < trainer?.Party.Count; i++)
        {
            var pokemon = trainer.Party[i];
            var row = i / pokemonPerRow;
            var col = i % pokemonPerRow;

            var pokemonX = x + col * pokemonWidth;
            var pokemonY = y + row * pokemonHeight;

            // Get Pokémon sprite
            var fileName = await GetPokemonFileName(pokemon, mongoService);
            var sprite = await LoadPokemonBitmap($"pixel_sprites/{fileName}");

            if (sprite == null) continue;

            // Draw Pokémon sprite with shadow
            DrawPokemonWithShadow(canvas, sprite, pokemonX + 10, pokemonY + 5, 64, 64);

            // Sanitize the Pokémon name before drawing
            var sanitizedName = PokemonNameSanitizer.SanitizeDisplayName(pokemon.Name, 15);

            // Draw Pokémon name and level in Pokémon style box
            DrawPokemonInfoBox(canvas, sanitizedName, pokemon.Level, pokemonX + 80, pokemonY + 20, 180, 50);
        }
    }

    /// <summary>
    ///     Draws a Pokémon sprite with a shadow effect underneath.
    ///     Adds depth and style to the Pokémon display.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="sprite">The Pokémon sprite bitmap to draw.</param>
    /// <param name="x">The X position to draw the sprite.</param>
    /// <param name="y">The Y position to draw the sprite.</param>
    /// <param name="width">The width to scale the sprite to.</param>
    /// <param name="height">The height to scale the sprite to.</param>
    private static void DrawPokemonWithShadow(SKCanvas canvas, SKBitmap sprite, float x, float y, float width,
        float height)
    {
        // Draw shadow
        using var shadowPaint = new SKPaint();
        shadowPaint.Color = new SKColor(0, 0, 0, 70);
        shadowPaint.IsAntialias = true;
        shadowPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3);
        canvas.DrawOval(new SKRect(x + 5, y + height - 10, x + width - 5, y + height), shadowPaint);

        // Draw Pokémon sprite
        var rect = new SKRect(x, y, x + width, y + height);
        canvas.DrawBitmap(sprite, rect);
    }

    /// <summary>
    ///     Draws a Pokémon information box with name and level.
    ///     Creates a styled box similar to Pokémon games for displaying Pokémon details.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="name">The name of the Pokémon.</param>
    /// <param name="level">The level of the Pokémon.</param>
    /// <param name="x">The X position to draw the info box.</param>
    /// <param name="y">The Y position to draw the info box.</param>
    /// <param name="width">The width of the info box.</param>
    /// <param name="height">The height of the info box.</param>
    private static void DrawPokemonInfoBox(SKCanvas canvas, string name, int level, float x, float y, float width,
        float height)
    {
        // Draw box background
        using var boxBgPaint = new SKPaint();
        boxBgPaint.Color = new SKColor(248, 248, 248);
        boxBgPaint.IsAntialias = true;

        using var boxBorderPaint = new SKPaint();
        boxBorderPaint.Color = new SKColor(80, 80, 80);
        boxBorderPaint.IsAntialias = true;
        boxBorderPaint.Style = SKPaintStyle.Stroke;
        boxBorderPaint.StrokeWidth = 2;

        var boxRect = new SKRoundRect(new SKRect(x, y, x + width, y + height), 10, 10);
        canvas.DrawRoundRect(boxRect, boxBgPaint);
        canvas.DrawRoundRect(boxRect, boxBorderPaint);

        // Draw Pokémon name
        using var nameFont = new SKFont();
        nameFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        nameFont.Size = 18;
        using var namePaint = new SKPaint();
        namePaint.Color = SKColors.Black;
        namePaint.IsAntialias = true;
        UnicodeTextRenderer.DrawText(canvas, x + 10, y + 25, name, nameFont, namePaint);

        // Draw level
        using var levelFont = new SKFont();
        levelFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        levelFont.Size = 14;
        using var levelPaint = new SKPaint();
        levelPaint.Color = SKColors.DarkGray;
        levelPaint.IsAntialias = true;
        UnicodeTextRenderer.DrawText(canvas, x + 10, y + 45, $"Lv. {level}", levelFont, levelPaint);
    }


    /// <summary>
    ///     Generates the main battle image showing the current battle scene.
    ///     Displays the background, weather effects, Pokémon, and their status.
    /// </summary>
    /// <param name="battle">The Battle object containing the current battle state.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the generated SKImage.
    /// </returns>
    public async Task<SKImage> GenerateBattleImage(Battle battle)
    {
        const int width = 800;
        const int height = 450;

        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;

        // Draw background
        await DrawBackground(canvas, battle.BgNum, width, height);

        // Draw weather effects if any
        if (battle.Weather.WeatherType is not null)
            await DrawWeatherEffect(canvas, battle.Weather.Get(), width, height);

        // Draw trick room effect if active
        if (battle.TrickRoom.Active()) DrawTrickRoomEffect(canvas, width, height);

        // Draw Pokémon
        await DrawBattlePokemon(canvas, battle, width, height);

        // Draw HP bars and info
        DrawPokemonStatus(canvas, battle, width, height);

        return surface.Snapshot();
    }

    /// <summary>
    ///     Draws the battle background based on the background number.
    ///     Loads a background image or falls back to a gradient if the image is not found.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bgNum">The background number to load.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task DrawBackground(SKCanvas canvas, int bgNum, int width, int height)
    {
        var bgPath = Path.Combine(ResourcePath, "backgrounds", $"bg{bgNum}.png");

        if (File.Exists(bgPath))
        {
            var background = await LoadBitmapFromFile(bgPath);
            var rect = new SKRect(0, 0, width, height);
            canvas.DrawBitmap(background, rect);
        }
        else
        {
            // Fallback to a gradient background
            using var paint = new SKPaint();
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                [new SKColor(135, 206, 235), new SKColor(34, 139, 34)],
                null,
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, paint);
        }
    }

    /// <summary>
    ///     Draws weather effects on the battle scene based on the current weather type.
    ///     Renders visual elements for different weather conditions like rain, sun, sandstorm, etc.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="weatherType">The current weather type as a string.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task DrawWeatherEffect(SKCanvas canvas, string? weatherType, int width, int height)
    {
        switch (weatherType)
        {
            case "rain":
                DrawRainEffect(canvas, width, height);
                break;
            case "h-sun":
                DrawSunEffect(canvas, width, height);
                break;
            case "sandstorm": // Sandstorm
                DrawSandstormEffect(canvas, width, height);
                break;
            case "hail":
                DrawHailEffect(canvas, width, height);
                break;
            case "fog":
                DrawFogEffect(canvas, width, height);
                break;
        }
    }

    /// <summary>
    ///     Draws a rain effect on the battle scene.
    ///     Creates falling raindrops and adds a blue overlay.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawRainEffect(SKCanvas canvas, int width, int height)
    {
        var random = new Random();

        // Draw rain drops
        using var rainPaint = new SKPaint();
        rainPaint.Color = new SKColor(150, 200, 255, 180);
        rainPaint.IsAntialias = true;
        rainPaint.StrokeWidth = 2;
        rainPaint.Style = SKPaintStyle.Stroke;

        for (var i = 0; i < 100; i++)
        {
            float x1 = random.Next(width);
            float y1 = random.Next(height);
            var x2 = x1 - 10;
            var y2 = y1 + 20;

            canvas.DrawLine(x1, y1, x2, y2, rainPaint);
        }

        // Add a blue overlay
        using var overlayPaint = new SKPaint();
        overlayPaint.Color = new SKColor(0, 0, 150, 30);
        canvas.DrawRect(0, 0, width, height, overlayPaint);
    }

    /// <summary>
    ///     Draws a sun effect on the battle scene.
    ///     Creates a sun with rays and adds a yellow overlay.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawSunEffect(SKCanvas canvas, int width, int height)
    {
        // Draw sun
        using var sunPaint = new SKPaint();
        sunPaint.Color = new SKColor(255, 200, 0, 150);
        sunPaint.IsAntialias = true;

        canvas.DrawCircle(width / 2, height / 4, 60, sunPaint);

        // Draw sun rays
        using var rayPaint = new SKPaint();
        rayPaint.Color = new SKColor(255, 200, 0, 100);
        rayPaint.IsAntialias = true;
        rayPaint.StrokeWidth = 5;

        for (var i = 0; i < 12; i++)
        {
            var angle = i * 30 * Math.PI / 180;
            var x1 = width / 2 + (float)(80 * Math.Cos(angle));
            var y1 = height / 4 + (float)(80 * Math.Sin(angle));
            var x2 = width / 2 + (float)(120 * Math.Cos(angle));
            var y2 = height / 4 + (float)(120 * Math.Sin(angle));

            canvas.DrawLine(x1, y1, x2, y2, rayPaint);
        }

        // Add a yellow overlay
        using var overlayPaint = new SKPaint();
        overlayPaint.Color = new SKColor(255, 200, 0, 30);
        canvas.DrawRect(0, 0, width, height, overlayPaint);
    }

    /// <summary>
    ///     Draws a sandstorm effect on the battle scene.
    ///     Creates flying sand particles and adds a sandy overlay.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawSandstormEffect(SKCanvas canvas, int width, int height)
    {
        var random = new Random();

        // Draw sand particles
        using var sandPaint = new SKPaint();
        sandPaint.Color = new SKColor(210, 180, 140, 150);
        sandPaint.IsAntialias = true;

        for (var i = 0; i < 200; i++)
        {
            float x = random.Next(width);
            float y = random.Next(height);
            float size = random.Next(1, 4);

            canvas.DrawRect(x, y, x + size, y + size, sandPaint);
        }

        // Add a sand-colored overlay
        using var overlayPaint = new SKPaint();
        overlayPaint.Color = new SKColor(210, 180, 140, 50);
        canvas.DrawRect(0, 0, width, height, overlayPaint);
    }

    /// <summary>
    ///     Draws a hail effect on the battle scene.
    ///     Creates hail particles and adds a bluish overlay.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawHailEffect(SKCanvas canvas, int width, int height)
    {
        var random = new Random();

        // Draw hail particles
        using var hailPaint = new SKPaint();
        hailPaint.Color = new SKColor(220, 240, 255, 200);
        hailPaint.IsAntialias = true;

        for (var i = 0; i < 80; i++)
        {
            float x = random.Next(width);
            float y = random.Next(height);
            float size = random.Next(2, 6);

            canvas.DrawCircle(x, y, size, hailPaint);
        }

        // Add a blue-white overlay
        using var overlayPaint = new SKPaint();
        overlayPaint.Color = new SKColor(200, 220, 255, 30);
        canvas.DrawRect(0, 0, width, height, overlayPaint);
    }

    /// <summary>
    ///     Draws a fog effect on the battle scene.
    ///     Creates a misty overlay with fog patches.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawFogEffect(SKCanvas canvas, int width, int height)
    {
        // Draw fog overlay
        using var fogPaint = new SKPaint();
        fogPaint.Color = new SKColor(255, 255, 255, 100);
        canvas.DrawRect(0, 0, width, height, fogPaint);

        // Draw fog patches
        var random = new Random();
        using var patchPaint = new SKPaint();
        patchPaint.Color = new SKColor(255, 255, 255, 70);
        patchPaint.IsAntialias = true;
        patchPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 20);

        for (var i = 0; i < 15; i++)
        {
            float x = random.Next(width);
            float y = random.Next(height);
            float sizeX = random.Next(50, 150);
            float sizeY = random.Next(20, 50);

            canvas.DrawOval(new SKRect(x, y, x + sizeX, y + sizeY), patchPaint);
        }
    }

    /// <summary>
    ///     Draws a Trick Room effect on the battle scene.
    ///     Creates a distorted grid pattern with purple tint.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private static void DrawTrickRoomEffect(SKCanvas canvas, int width, int height)
    {
        // Draw grid pattern
        using var gridPaint = new SKPaint();
        gridPaint.Color = new SKColor(180, 100, 220, 100);
        gridPaint.IsAntialias = true;
        gridPaint.StrokeWidth = 1;
        gridPaint.Style = SKPaintStyle.Stroke;

        // Horizontal lines
        for (var y = 0; y < height; y += 30) canvas.DrawLine(0, y, width, y, gridPaint);

        // Vertical lines
        for (var x = 0; x < width; x += 30) canvas.DrawLine(x, 0, x, height, gridPaint);

        // Add a purple tint
        using var tintPaint = new SKPaint();
        tintPaint.Color = new SKColor(180, 100, 220, 20);
        canvas.DrawRect(0, 0, width, height, tintPaint);
    }

    /// <summary>
    ///     Draws the Pokémon on the battle scene.
    ///     Renders both trainers' Pokémon or substitutes if active.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="battle">The Battle object containing the current battle state.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task DrawBattlePokemon(SKCanvas canvas, Battle battle, int width, int height)
    {
        // Draw player's Pokémon (left side, back view like in Pokémon games)
        if (battle.Trainer1.CurrentPokemon != null)
        {
            var pokemon = battle.Trainer1.CurrentPokemon;
            var directory = "images";

            // Determine skin or default sprite
            if (pokemon.Skin != null && !pokemon.Skin.Contains("verification")) directory = "images";

            var fileName = await GetPokemonFileName(pokemon, mongoService);

            if (pokemon.Substitute > 0)
            {
                // Draw substitute
                var substitute = await LoadPokemonBitmap("images/substitute.png");
                if (substitute != null)
                {
                    var rect = new SKRect(70, height - 270, 300, height - 40);
                    canvas.DrawBitmap(substitute, rect);
                }
            }
            else
            {
                // Draw Pokémon
                var sprite = await LoadPokemonBitmap($"{directory}/{fileName}");
                if (sprite != null)
                {
                    var rect = new SKRect(70, height - 270, 300, height - 40);
                    canvas.DrawBitmap(sprite, rect);
                }
            }
        }

        // Draw opponent's Pokémon (right side)
        if (battle.Trainer2.CurrentPokemon != null)
        {
            var pokemon = battle.Trainer2.CurrentPokemon;
            var directory = "images";

            // Determine skin or default sprite
            if (pokemon.Skin != null && !pokemon.Skin.Contains("verification")) directory = "images";

            var fileName = await GetPokemonFileName(pokemon, mongoService);

            if (pokemon.Substitute > 0)
            {
                // Draw substitute
                var substitute = await LoadPokemonBitmap("images/substitute.png");
                if (substitute != null)
                {
                    var rect = new SKRect(width - 300, 40, width - 70, 270);
                    canvas.DrawBitmap(substitute, rect);
                }
            }
            else
            {
                // Draw Pokémon
                var sprite = await LoadPokemonBitmap($"{directory}/{fileName}");
                if (sprite != null)
                {
                    var rect = new SKRect(width - 300, 40, width - 70, 270);
                    canvas.DrawBitmap(sprite, rect);
                }
            }
        }
    }

    /// <summary>
    ///     Draws the Pokémon status displays and HP bars.
    ///     Shows information about both trainers' active Pokémon.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="battle">The Battle object containing the current battle state.</param>
    /// <param name="width">The width of the canvas.</param>
    /// <param name="height">The height of the canvas.</param>
    private void DrawPokemonStatus(SKCanvas canvas, Battle battle, int width, int height)
    {
        // Draw player's Pokémon info (bottom left) in Pokémon style - like in Image 1
        if (battle.Trainer1.CurrentPokemon != null)
        {
            var pokemon = battle.Trainer1.CurrentPokemon;
            DrawPokemonGameStyleHpBar(canvas, pokemon, 40, 40, 300, true);
        }

        // Draw opponent's Pokémon info (bottom right) in Pokémon style - moved from top to bottom
        if (battle.Trainer2.CurrentPokemon != null)
        {
            var pokemon = battle.Trainer2.CurrentPokemon;
            // Changed y-coordinate from 40 to (height - 110) to place it at the bottom
            DrawPokemonGameStyleHpBar(canvas, pokemon, width - 340, height - 110, 300, false);
        }
    }

    /// <summary>
    ///     Draws a Pokémon-style HP bar with name, level, and current HP.
    ///     Creates a stylized display similar to the Pokémon games.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="pokemon">The Pokémon whose HP to display.</param>
    /// <param name="x">The X position to draw the HP bar.</param>
    /// <param name="y">The Y position to draw the HP bar.</param>
    /// <param name="width">The width of the HP bar.</param>
    /// <param name="isPlayer">Whether this is the player's Pokémon (affects display style).</param>
    private static void DrawPokemonGameStyleHpBar(SKCanvas canvas, DuelPokemon pokemon, float x, float y, float width,
        bool isPlayer)
    {
        using var boxPath = new SKPath();
        boxPath.MoveTo(x, y);
        boxPath.LineTo(x + width, y);
        boxPath.LineTo(x + width, y + 70);
        boxPath.LineTo(x + width - 30, y + 70); // Create angled edge
        boxPath.LineTo(x, y + 70);
        boxPath.Close();

        using var boxBgPaint = new SKPaint();
        boxBgPaint.Color = new SKColor(220, 220, 220);
        boxBgPaint.IsAntialias = true;

        using var boxBorderPaint = new SKPaint();
        boxBorderPaint.Color = new SKColor(60, 60, 60);
        boxBorderPaint.IsAntialias = true;
        boxBorderPaint.Style = SKPaintStyle.Stroke;
        boxBorderPaint.StrokeWidth = 2;

        canvas.DrawPath(boxPath, boxBgPaint);
        canvas.DrawPath(boxPath, boxBorderPaint);

        // Sanitize the Pokémon name before drawing
        var sanitizedName = PokemonNameSanitizer.SanitizeDisplayName(pokemon.Name, 18);

        // Draw name and gender symbol
        using var nameFont = new SKFont();
        nameFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        nameFont.Size = 22;
        using var namePaint = new SKPaint();
        namePaint.Color = SKColors.Black;
        namePaint.IsAntialias = true;

        UnicodeTextRenderer.DrawText(canvas, x + 15, y + 25, sanitizedName, nameFont, namePaint);

        var genderSymbol = pokemon.Gender == "male" ? "♂" : pokemon.Gender == "female" ? "♀" : "";
        using var levelFont = new SKFont();
        levelFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        levelFont.Size = 18;
        using var levelPaint = new SKPaint();
        levelPaint.Color = SKColors.Black;
        levelPaint.IsAntialias = true;

        var levelText = $"{genderSymbol}Lv.{pokemon.Level}";
        UnicodeTextRenderer.DrawText(canvas, x + width - 70, y + 25, levelText, levelFont, levelPaint);

        // Draw HP label
        using var hpLabelFont = new SKFont();
        hpLabelFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        hpLabelFont.Size = 18;
        using var hpLabelPaint = new SKPaint();
        hpLabelPaint.Color = SKColors.Black;
        hpLabelPaint.IsAntialias = true;

        UnicodeTextRenderer.DrawText(canvas, x + 15, y + 50, "HP", hpLabelFont, hpLabelPaint);

        // Draw HP bar with proper spacing for numbers
        using var barBgPaint = new SKPaint();
        barBgPaint.Color = new SKColor(180, 180, 180);
        barBgPaint.IsAntialias = true;

        var barRect = new SKRect(x + 55, y + 42, x + width - 15, y + 54);
        canvas.DrawRect(barRect, barBgPaint);

        // Calculate HP percentage
        var hpPercent = (float)pokemon.Hp / pokemon.StartingHp;
        var hpBarWidth = (width - 70) * hpPercent;

        // Determine HP bar color
        var hpColor = hpPercent switch
        {
            > 0.5f => new SKColor(96, 192, 64), // Brighter green
            > 0.2f => new SKColor(248, 208, 48), // Yellow
            _ => new SKColor(240, 80, 48) // Red
        };

        using var hpPaint = new SKPaint();
        hpPaint.Color = hpColor;
        hpPaint.IsAntialias = true;

        // Draw the actual HP bar
        var hpRect = new SKRect(x + 55, y + 42, x + 55 + hpBarWidth, y + 54);
        canvas.DrawRect(hpRect, hpPaint);

        // Draw HP numbers with proper spacing
        if (!isPlayer) return;
        using var hpTextFont = new SKFont();
        hpTextFont.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        hpTextFont.Size = 16;
        using var hpTextPaint = new SKPaint();
        hpTextPaint.Color = SKColors.Black;
        hpTextPaint.IsAntialias = true;

        var hpText = $"{pokemon.Hp}/{pokemon.StartingHp}";
        var hpTextWidth = UnicodeTextRenderer.MeasureText(hpText, hpTextFont);
        UnicodeTextRenderer.DrawText(canvas, x + width - 15 - hpTextWidth, y + 67, hpText, hpTextFont, hpTextPaint);
    }

    /// <summary>
    ///     Gets the filename for a Pokémon's sprite based on its properties.
    ///     Determines the correct sprite file based on Pokémon ID, form, shiny status, etc.
    /// </summary>
    /// <param name="pokemon">The Pokémon to get the sprite for.</param>
    /// <param name="mongo">The MongoDB service for accessing Pokémon data.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the sprite filename.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     Thrown when an invalid Pokémon name is provided.
    /// </exception>
    private static async Task<string> GetPokemonFileName(DuelPokemon pokemon, IMongoService mongo)
    {
        var identifier = await mongo.Forms
            .Find(f => f.Identifier.Equals(pokemon._name.ToLower(), StringComparison.CurrentCultureIgnoreCase))
            .FirstOrDefaultAsync();

        if (identifier == null)
            throw new ArgumentException($"Invalid name ({pokemon._name}) passed to GetPokemonFormInfo");

        var suffix = identifier.FormIdentifier;
        int pokemonId;
        var formId = 0;

        if (!string.IsNullOrEmpty(suffix) && pokemon.FullName.EndsWith(suffix))
        {
            formId = (int)(identifier.FormOrder - 1)!;
            var formName = pokemon.FullName[..^(suffix.Length + 1)];

            var pokemonIdentifier = await mongo.Forms
                .Find(f => f.Identifier.Equals(formName, StringComparison.CurrentCultureIgnoreCase))
                .FirstOrDefaultAsync();

            if (pokemonIdentifier == null)
                throw new ArgumentException($"Invalid name ({pokemon._name}) passed to GetPokemonFormInfo");

            pokemonId = pokemonIdentifier.PokemonId;
        }
        else
        {
            pokemonId = identifier.PokemonId;
        }

        var fileType = "png";
        var skinPath = "";
        if (!string.IsNullOrEmpty(pokemon.Skin) && pokemon.Skin != "NULL")
        {
            if (pokemon.Skin.EndsWith("_gif"))
                fileType = "gif";
            skinPath = $"{pokemon.Skin}/";
        }

        var isPlaceholder = await mongo.RadiantPlaceholders
            .Find(p => p.Name.Equals(pokemon._name, StringComparison.CurrentCultureIgnoreCase))
            .FirstOrDefaultAsync();

        var radiantPath = pokemon.Radiant ? "radiant/" : "";
        if (pokemon.Radiant && isPlaceholder != null)
            return "placeholder.png";

        var shinyPath = pokemon.Shiny ? "shiny/" : "";
        var fileName = $"{radiantPath}{shinyPath}{skinPath}{pokemonId}-{formId}-.{fileType}";

        return fileName;
    }

    /// <summary>
    ///     Loads a Pokémon bitmap from the specified relative path.
    ///     Falls back to a placeholder if the image cannot be found.
    /// </summary>
    /// <param name="relativePath">The relative path to the Pokémon sprite.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the loaded SKBitmap.
    /// </returns>
    private async Task<SKBitmap> LoadPokemonBitmap(string relativePath)
    {
        var fullPath = Path.Combine(ResourcePath, relativePath);

        // Check if file exists
        if (File.Exists(fullPath)) return await LoadBitmapFromFile(fullPath);
        // Try fallback
        var fallbackPath = Path.Combine(ResourcePath, "images", "unknown.png");
        if (File.Exists(fallbackPath))
            return await LoadBitmapFromFile(fallbackPath);

        // Create a placeholder
        return CreatePlaceholderBitmap();
    }

    /// <summary>
    ///     Loads a bitmap from a file, using caching for better performance.
    ///     Returns a cached version if the image was loaded previously.
    /// </summary>
    /// <param name="path">The full path to the image file.</param>
    /// <returns>
    ///     A task representing the asynchronous operation that returns the loaded SKBitmap.
    /// </returns>
    private async Task<SKBitmap> LoadBitmapFromFile(string path)
    {
        // Check cache first
        if (_imageCache.TryGetValue(path, out var cached))
            return cached;

        return await Task.Run(() =>
        {
            try
            {
                var bitmap = SKBitmap.Decode(path);
                _imageCache[path] = bitmap;
                return bitmap;
            }
            catch
            {
                return CreatePlaceholderBitmap();
            }
        });
    }

    /// <summary>
    ///     Creates a placeholder bitmap with a question mark.
    ///     Used when a requested image cannot be found or loaded.
    /// </summary>
    /// <returns>A placeholder SKBitmap with a question mark.</returns>
    private static SKBitmap CreatePlaceholderBitmap()
    {
        var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);

        // Fill with question mark
        canvas.Clear(SKColors.LightGray);

        using var font = new SKFont();
        font.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        font.Size = 48;
        using var paint = new SKPaint();
        paint.Color = SKColors.Black;
        paint.IsAntialias = true;

        const string questionText = "?";
        var textWidth = UnicodeTextRenderer.MeasureText(questionText, font);
        UnicodeTextRenderer.DrawText(canvas, 32 - textWidth / 2, 48, questionText, font, paint);

        return bitmap;
    }
}