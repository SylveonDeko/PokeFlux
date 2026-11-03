using EeveeCore.Services.Impl;
using LinqToDB;
using MongoDB.Driver;
using Serilog;

namespace EeveeCore.Modules.Pokemon.Services;

/// <summary>
///     Service class for handling Pokemon form transformations.
///     Manages form changes, mega evolution, fusion mechanics, and related operations.
/// </summary>
public class FormsService(
    LinqToDbConnectionProvider dbProvider,
    IMongoService mongo,
    PokemonService pokemonService)
    : INService
{
    /// <summary>
    ///     List of Pokemon that can mega evolve.
    /// </summary>
    private static readonly HashSet<string> MegaEvolvablePokemon =
    [
        "venusaur", "blastoise", "alakazam", "gengar", "kangaskhan", "pinsir", "gyarados", "aerodactyl",
        "ampharos", "scizor", "heracross", "houndoom", "tyranitar", "blaziken", "gardevoir", "mawile",
        "aggron", "medicham", "manectric", "banette", "absol", "latias", "latios", "garchomp", "lucario",
        "abomasnow", "beedrill", "pidgeot", "slowbro", "steelix", "sceptile", "swampert", "sableye",
        "sharpedo", "camerupt", "altaria", "glalie", "salamence", "metagross", "rayquaza", "lopunny",
        "gallade", "audino", "diancie"
    ];

    /// <summary>
    ///     Pokemon that have X and Y mega forms.
    /// </summary>
    private static readonly HashSet<string> XYMegaForms = ["charizard", "mewtwo"];

    /// <summary>
    ///     Transforms a Pokemon to a specified form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="formName">The name of the form to transform to.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> TransformToFormAsync(ulong userId, string formName)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(formName))
                return (false, "Please specify a form name!");

            if (formName.Length > 50)
                return (false, "Form name is too long!");

            formName = formName.Trim().ToLower();
            
            // Prevent common user mistakes
            if (formName.Contains(" "))
            {
                formName = formName.Replace(" ", "-");
            }

            // Block inappropriate inputs
            if (formName.Contains("shit") || formName.Contains("fuck") || formName.Contains("damn"))
                return (false, "Please use appropriate form names!");

            // Block certain regional forms and unavailable forms
            if (IsBlockedForm(formName))
                return (false, GetBlockedFormMessage(formName));

            // Check for mega evolution redirect
            if (formName.Contains("mega"))
                return (false, "Use `/pokemon forms mega` for mega evolutions.");

            await using var db = await dbProvider.GetConnectionAsync();
            
            // Get selected Pokemon
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var pokemonName = selectedPokemon.PokemonName.ToLower();

            // Block fusion Pokemon
            if (pokemonName == "kyurem")
                return (false, "Please use `/pokemon forms fuse` for Kyurem and `/pokemon forms lunarize` / `/pokemon forms solarize` for Necrozma fusions.");

            // Check specific Pokemon requirements
            var validationResult = await ValidateFormTransformation(selectedPokemon, formName);
            if (!validationResult.Success)
                return validationResult;

            // Validate form exists in database
            var formExists = await ValidateFormExists(pokemonName, formName);
            if (!formExists.Success)
                return formExists;

            // Apply transformation
            var newFormName = BuildFormName(pokemonName, formName);
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, newFormName.Capitalize())
                .UpdateAsync();

            return (true, $"Your {pokemonName.Capitalize()} has transformed into {newFormName.Capitalize()}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error transforming Pokemon to form {FormName} for user {UserId}", formName, userId);
            return (false, "An error occurred while transforming your Pokemon.");
        }
    }

    /// <summary>
    ///     Resets a Pokemon to its base form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> DeformPokemonAsync(ulong userId)
    {
        try
        {
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var pokemonName = selectedPokemon.PokemonName;
            
            var name = pokemonName.ToLower().Split('-')[0];
            var originalFormInfo = await mongo.Forms.Find(f => f.Identifier == name).FirstOrDefaultAsync();
            if (originalFormInfo == null)
                return (false, "This Pokemon is not in a form that can be deformed!");

            // Handle special Tauros forms
            if (pokemonName.ToLower() == "tauros-blaze-paldea" || pokemonName.ToLower() == "tauros-aqua-paldea")
            {
                await using var db = await dbProvider.GetConnectionAsync();
                await db.UserPokemon
                    .Where(p => p.Id == selectedPokemon.Id)
                    .Set(p => p.PokemonName, "Tauros-paldea")
                    .UpdateAsync();

                return (true, $"You have deformed your {pokemonName}.");
            }

            // Check if Pokemon is actually formed
            if (!IsFormed(pokemonName) || pokemonName.ToLower().EndsWith("-alola"))
                return (false, "This Pokemon is not in a form that can be deformed!");

            // Build base name
            var baseFormName = GetBaseFormName(pokemonName);
            
            await using var dbConnection = await dbProvider.GetConnectionAsync();
            await dbConnection.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, baseFormName)
                .UpdateAsync();

            return (true, "Your Pokemon has successfully reset to its base form.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error deforming Pokemon for user {UserId}", userId);
            return (false, "An error occurred while deforming your Pokemon.");
        }
    }

    /// <summary>
    ///     Fuses two compatible Pokemon together.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="fusionType">The type of fusion (white, black, ice, shadow).</param>
    /// <param name="targetPokemonNumber">The number of the target Pokemon to fuse with.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> FusePokemonAsync(ulong userId, string fusionType, ulong targetPokemonNumber)
    {
        try
        {
            // Input validation
            if (string.IsNullOrWhiteSpace(fusionType))
                return (false, "Please specify a fusion type!");

            if (targetPokemonNumber == 0)
                return (false, "Pokemon number must be greater than 0!");

            if (targetPokemonNumber > 1000000)
                return (false, "That's way too many Pokemon! Please use a reasonable number.");
            
            fusionType = fusionType.Trim().ToLower();
            
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var targetPokemon = await pokemonService.GetPokemonByNumberAsync(userId, targetPokemonNumber);
            if (targetPokemon == null)
                return (false, "You do not have that many Pokemon!");

            var (success, message, formName) = fusionType switch
            {
                "white" => HandleKyuremWhiteFusion(selectedPokemon, targetPokemon),
                "black" => HandleKyuremBlackFusion(selectedPokemon, targetPokemon),
                "ice" => HandleCalyrexIceFusion(selectedPokemon, targetPokemon),
                "shadow" => HandleCalyrexShadowFusion(selectedPokemon, targetPokemon),
                _ => (false, "Invalid fusion type! Valid types: white, black, ice, shadow", "")
            };

            if (!success)
                return (false, message);

            // Apply the fusion
            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, formName)
                .UpdateAsync();

            return (true, $"You have fused your {selectedPokemon.PokemonName} with your {targetPokemon.PokemonName} Level {targetPokemon.Level}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error fusing Pokemon for user {UserId}", userId);
            return (false, "An error occurred while fusing your Pokemon.");
        }
    }

    /// <summary>
    ///     Fuses Necrozma with Lunala to create Necrozma-Dawn Wings form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="lunalaNumber">The number of the Lunala to fuse with.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> LunarizePokemonAsync(ulong userId, ulong lunalaNumber)
    {
        try
        {
            // Input validation
            if (lunalaNumber == 0)
                return (false, "Pokemon number must be greater than 0!");

            if (lunalaNumber > 1000000)
                return (false, "That's way too many Pokemon! Please use a reasonable number.");
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You need to select a Necrozma first!");

            if (selectedPokemon.PokemonName.ToLower() != "necrozma")
                return (false, $"You cannot lunarize a {selectedPokemon.PokemonName}.");

            if (selectedPokemon.HeldItem?.ToLower() != "n-lunarizer")
                return (false, "Your Necrozma is not holding a N-lunarizer.\nYou need to buy it from the Shop.");

            var lunala = await pokemonService.GetPokemonByNumberAsync(userId, lunalaNumber);
            if (lunala == null)
                return (false, "You do not have that many Pokemon!");

            if (lunala.PokemonName.ToLower() != "lunala")
                return (false, "That is not a Lunala. Please use `/pokemon forms lunarize <lunala_number>` to lunarize.");

            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, "Necrozma-dawn")
                .UpdateAsync();

            return (true, "You have fused your Necrozma with your Lunala!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error lunarizing Pokemon for user {UserId}", userId);
            return (false, "An error occurred while lunarizing your Pokemon.");
        }
    }

    /// <summary>
    ///     Fuses Necrozma with Solgaleo to create Necrozma-Dusk Mane form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <param name="solgaleoNumber">The number of the Solgaleo to fuse with.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> SolarizePokemonAsync(ulong userId, ulong solgaleoNumber)
    {
        try
        {
            // Input validation
            if (solgaleoNumber == 0)
                return (false, "Pokemon number must be greater than 0!");

            if (solgaleoNumber > 1000000)
                return (false, "That's way too many Pokemon! Please use a reasonable number.");
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            if (selectedPokemon.PokemonName.ToLower() != "necrozma")
                return (false, $"You cannot solarize a {selectedPokemon.PokemonName}.");

            if (selectedPokemon.HeldItem?.ToLower() != "n-solarizer")
                return (false, "Your Necrozma is not holding a N-solarizer.\nYou need to buy it from the Shop.");

            var solgaleo = await pokemonService.GetPokemonByNumberAsync(userId, solgaleoNumber);
            if (solgaleo == null)
                return (false, "You do not have that many Pokemon!");

            if (solgaleo.PokemonName.ToLower() != "solgaleo")
                return (false, "That is not a Solgaleo. Please use `/pokemon forms solarize <solgaleo_number>` to solarize.");

            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, "Necrozma-dusk")
                .UpdateAsync();

            return (true, $"You have fused your Necrozma with your Solgaleo Level {solgaleo.Level}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error solarizing Pokemon for user {UserId}", userId);
            return (false, "An error occurred while solarizing your Pokemon.");
        }
    }

    /// <summary>
    ///     Mega evolves a Pokemon.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> MegaEvolveAsync(ulong userId)
    {
        try
        {
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var pokemonName = selectedPokemon.PokemonName.ToLower();

            if (!MegaEvolvablePokemon.Contains(pokemonName))
                return (false, "That Pokemon cannot mega evolve!");

            // Special case for Rayquaza
            if (pokemonName == "rayquaza")
            {
                if (selectedPokemon.Moves?.Contains("dragon-ascent") != true)
                    return (false, "Your Rayquaza needs to know Dragon Ascent!");
            }
            else
            {
                var heldItem = selectedPokemon.HeldItem?.Replace("-", " ").ToLower();
                if (heldItem != "mega stone")
                    return (false, "This Pokemon is not holding a Mega Stone!");
            }

            // Get the mega form from MongoDB
            var megaForm = await GetMegaFormName(pokemonName);
            if (string.IsNullOrEmpty(megaForm))
                return (false, "This Pokemon cannot mega evolve!");

            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, megaForm.Capitalize())
                .UpdateAsync();

            return (true, $"Your {selectedPokemon.PokemonName} has mega evolved into {megaForm.Capitalize()}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error mega evolving Pokemon for user {UserId}", userId);
            return (false, "An error occurred while mega evolving your Pokemon.");
        }
    }

    /// <summary>
    ///     Mega evolves a Pokemon to its X form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> MegaEvolveXAsync(ulong userId)
    {
        try
        {
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var pokemonName = selectedPokemon.PokemonName.ToLower();

            if (!XYMegaForms.Contains(pokemonName))
                return (false, "That Pokemon cannot mega evolve into an X form!");

            var heldItem = selectedPokemon.HeldItem?.Replace("-", " ").ToLower();
            if (heldItem != "mega stone x")
                return (false, "This Pokemon is not holding a Mega Stone X!");

            var megaXForm = await GetMegaXFormName(pokemonName);
            if (string.IsNullOrEmpty(megaXForm))
                return (false, "This Pokemon cannot mega evolve to X form!");

            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, megaXForm.Capitalize())
                .UpdateAsync();

            return (true, $"Your {selectedPokemon.PokemonName} has mega evolved into {megaXForm.Capitalize()}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error mega evolving Pokemon to X form for user {UserId}", userId);
            return (false, "An error occurred while mega evolving your Pokemon.");
        }
    }

    /// <summary>
    ///     Mega evolves a Pokemon to its Y form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> MegaEvolveYAsync(ulong userId)
    {
        try
        {
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var pokemonName = selectedPokemon.PokemonName.ToLower();

            if (!XYMegaForms.Contains(pokemonName))
                return (false, "That Pokemon cannot mega evolve into a Y form!");

            var heldItem = selectedPokemon.HeldItem?.Replace("-", " ").ToLower();
            if (heldItem != "mega stone y")
                return (false, "This Pokemon is not holding a Mega Stone Y!");

            var megaYForm = await GetMegaYFormName(pokemonName);
            if (string.IsNullOrEmpty(megaYForm))
                return (false, "This Pokemon cannot mega evolve to Y form!");

            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, megaYForm.Capitalize())
                .UpdateAsync();

            return (true, $"Your {selectedPokemon.PokemonName} has mega evolved into {megaYForm.Capitalize()}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error mega evolving Pokemon to Y form for user {UserId}", userId);
            return (false, "An error occurred while mega evolving your Pokemon.");
        }
    }

    /// <summary>
    ///     Mega devolves a Pokemon back to its base form.
    /// </summary>
    /// <param name="userId">The ID of the user.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    public async Task<(bool Success, string Message)> MegaDevolveAsync(ulong userId)
    {
        try
        {
            var selectedPokemon = await pokemonService.GetSelectedPokemonAsync(userId);
            if (selectedPokemon == null)
                return (false, "You do not have a Pokemon selected!\nSelect one with `/pokemon select` first.");

            var pokemonName = selectedPokemon.PokemonName.ToLower();

            if (!pokemonName.Contains("mega"))
                return (false, "This Pokemon is not a mega Pokemon!");

            var baseForm = await GetBaseMegaFormName(pokemonName);
            if (string.IsNullOrEmpty(baseForm))
                return (false, "Cannot find base form for this Pokemon!");

            await using var db = await dbProvider.GetConnectionAsync();
            await db.UserPokemon
                .Where(p => p.Id == selectedPokemon.Id)
                .Set(p => p.PokemonName, baseForm.Capitalize())
                .UpdateAsync();

            return (true, $"Your {selectedPokemon.PokemonName} has devolved back to {baseForm.Capitalize()}!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error mega devolving Pokemon for user {UserId}", userId);
            return (false, "An error occurred while mega devolving your Pokemon.");
        }
    }

    #region Private Helper Methods

    /// <summary>
    ///     Checks if a form transformation is blocked.
    /// </summary>
    /// <param name="formName">The form name to check.</param>
    /// <returns>True if the form is blocked.</returns>
    private static bool IsBlockedForm(string formName)
    {
        var exceptions = new[] { "blaze-paldea", "aqua-paldea" };
        
        if (exceptions.Contains(formName))
            return false;

        return formName.EndsWith("alola") || formName.EndsWith("galar") || 
               formName.EndsWith("hisui") || formName.EndsWith("paldea") ||
               formName.EndsWith("misfit") || formName.EndsWith("skylarr") ||
               formName.EndsWith("lord");
    }

    /// <summary>
    ///     Gets the appropriate blocked form message.
    /// </summary>
    /// <param name="formName">The blocked form name.</param>
    /// <returns>The error message to display.</returns>
    private static string GetBlockedFormMessage(string formName)
    {
        if (formName.EndsWith("lord"))
            return "That form is not available yet!";
        
        return "You cannot form your Pokemon to a regional form!";
    }

    /// <summary>
    ///     Validates that a form transformation is allowed for the given Pokemon.
    /// </summary>
    /// <param name="pokemon">The Pokemon to transform.</param>
    /// <param name="formName">The target form name.</param>
    /// <returns>A tuple indicating success and a message.</returns>
    private async Task<(bool Success, string Message)> ValidateFormTransformation(
        Database.Linq.Models.Pokemon.Pokemon pokemon, string formName)
    {
        var pokemonName = pokemon.PokemonName.ToLower();

        // Special cases for specific Pokemon
        return pokemonName switch
        {
            "eevee" or "pikachu" => ValidateEeveePikachuForm(pokemon, formName),
            "necrozma" => ValidateNecrozmaForm(pokemon, formName),
            "lugia" => ValidateItemBasedForm(pokemon, "shadow-stone", formName),
            "shaymin" => ValidateItemBasedForm(pokemon, "gracidea-flower", formName),
            "kyogre" => ValidateItemBasedForm(pokemon, "blue-orb", formName),
            "groudon" => ValidateItemBasedForm(pokemon, "red-orb", formName),
            "hoopa" => ValidateItemBasedForm(pokemon, "prison-bottle", formName),
            "giratina" => ValidateItemBasedForm(pokemon, "griseous-orb", formName),
            "deoxys" => ValidateItemBasedForm(pokemon, "meteorite", formName),
            "thundurus" or "tornadus" or "landorus" => ValidateItemBasedForm(pokemon, "reveal-glass", formName),
            "zygarde" => ValidateItemBasedForm(pokemon, "zygarde-cell", formName),
            "palkia" => ValidateItemBasedForm(pokemon, "lustrous-orb", formName),
            "zacian" => ValidateItemBasedForm(pokemon, "rusty-sword", formName),
            "zamazenta" => ValidateItemBasedForm(pokemon, "rusty-shield", formName),
            "keldeo" => ValidateKeldeoForm(pokemon),
            "meloetta" => ValidateMeloettaForm(pokemon),
            "arceus" => ValidateArceusForm(pokemon, formName),
            "dialga" => ValidateDialgaForm(pokemon, formName),
            _ => (true, string.Empty)
        };
    }

    /// <summary>
    ///     Validates Eevee/Pikachu special forms.
    /// </summary>
    private (bool Success, string Message) ValidateEeveePikachuForm(
        Database.Linq.Models.Pokemon.Pokemon pokemon, string formName)
    {
        if (pokemon.Level != 100 || pokemon.Happiness < 252)
            return (false, $"Your {pokemon.PokemonName} needs to be level 100 and have maximum happiness!");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Validates Necrozma form transformation.
    /// </summary>
    private (bool Success, string Message) ValidateNecrozmaForm(
        Database.Linq.Models.Pokemon.Pokemon pokemon, string formName)
    {
        if (pokemon.HeldItem?.ToLower() != "ultranecronium-z")
            return (false, $"Your {pokemon.PokemonName} is not holding the Ultranecronium Z.");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Validates item-based form transformation.
    /// </summary>
    private (bool Success, string Message) ValidateItemBasedForm(
        Database.Linq.Models.Pokemon.Pokemon pokemon, string requiredItem, string formName)
    {
        if (pokemon.HeldItem?.ToLower() != requiredItem)
            return (false, $"Your {pokemon.PokemonName} is not holding the {requiredItem.Replace("-", " ")}.");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Validates Keldeo form transformation.
    /// </summary>
    private (bool Success, string Message) ValidateKeldeoForm(Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        if (pokemon.Moves?.Contains("secret-sword") != true)
            return (false, "Your Keldeo does not know the move Secret Sword.");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Validates Meloetta form transformation.
    /// </summary>
    private (bool Success, string Message) ValidateMeloettaForm(Database.Linq.Models.Pokemon.Pokemon pokemon)
    {
        if (pokemon.Moves?.Contains("relic-song") != true)
            return (false, "Your Meloetta does not know Relic Song move.");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Validates Arceus form transformation.
    /// </summary>
    private (bool Success, string Message) ValidateArceusForm(
        Database.Linq.Models.Pokemon.Pokemon pokemon, string formName)
    {
        var requiredPlate = GetArceusPlate(formName);
        if (string.IsNullOrEmpty(requiredPlate))
            return (false, "Invalid form for Arceus!");

        if (pokemon.HeldItem?.ToLower() != requiredPlate)
            return (false, $"Your Arceus is not holding the {requiredPlate.Replace("-", " ")}.");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Validates Dialga form transformation.
    /// </summary>
    private (bool Success, string Message) ValidateDialgaForm(
        Database.Linq.Models.Pokemon.Pokemon pokemon, string formName)
    {
        var requiredItem = formName.ToLower() switch
        {
            "origin" => "adamant-orb",
            "primal" => "primal-orb",
            _ => null
        };

        if (requiredItem == null)
            return (false, "Invalid form for Dialga!");

        if (pokemon.HeldItem?.ToLower() != requiredItem)
            return (false, $"Your Dialga is not holding the {requiredItem.Replace("-", " ")}.");
        
        return (true, string.Empty);
    }

    /// <summary>
    ///     Gets the required plate for Arceus forms.
    /// </summary>
    private string? GetArceusPlate(string formName)
    {
        return formName switch
        {
            "electric" => "zap-plate",
            "poison" => "toxic-plate",
            "rock" => "stone-plate",
            "ghost" => "spooky-plate",
            "water" => "splash-plate",
            "flying" => "sky-plate",
            "fairy" => "pixie-plate",
            "psychic" => "mind-plate",
            "grass" => "meadow-plate",
            "steel" => "iron-plate",
            "bug" => "insect-plate",
            "ice" => "icicle-plate",
            "fighting" => "fist-plate",
            "dragon" => "draco-plate",
            "fire" => "flame-plate",
            "dark" => "dread-plate",
            "ground" => "earth-plate",
            _ => null
        };
    }

    /// <summary>
    ///     Validates that a form exists in the database.
    /// </summary>
    private async Task<(bool Success, string Message)> ValidateFormExists(string pokemonName, string formName)
    {
        try
        {
            var formToEvolve = BuildFormName(pokemonName, formName);
            var formInfo = await mongo.Forms
                .Find(f => f.Identifier == formToEvolve)
                .FirstOrDefaultAsync();

            if (formInfo == null)
                return (false, "That form does not exist!");

            if (string.IsNullOrEmpty(formInfo.FormIdentifier) || formInfo.FormIdentifier.ToLower() != formName)
                return (false, "Invalid form for that Pokemon!");

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error validating form exists for {PokemonName} -> {FormName}", pokemonName, formName);
            return (false, "Error validating form.");
        }
    }

    /// <summary>
    ///     Builds the full form name for a Pokemon.
    /// </summary>
    private string BuildFormName(string pokemonName, string formName)
    {
        if (pokemonName.EndsWith("-galar"))
        {
            var baseName = pokemonName.Replace("-galar", "");
            return $"{baseName}-{formName}-galar";
        }

        if (pokemonName.EndsWith("-paldea"))
        {
            var baseName = pokemonName.Replace("-paldea", "");
            return $"{baseName}-{formName}";
        }

        return $"{pokemonName}-{formName}";
    }

    /// <summary>
    ///     Checks if a Pokemon name represents a formed Pokemon.
    /// </summary>
    private bool IsFormed(string pokemonName)
    {
        var name = pokemonName.ToLower();
        return name.Contains("-mega") || name.Contains("-x") || name.Contains("-y") ||
               name.Contains("-origin") || name.Contains("-10") || name.Contains("-complete") ||
               name.Contains("-ultra") || name.Contains("-crowned") || name.Contains("-eternamax") ||
               name.Contains("-blade") || name.Contains("-");
    }

    /// <summary>
    ///     Gets the base form name for a formed Pokemon.
    /// </summary>
    private string GetBaseFormName(string pokemonName)
    {
        var parts = pokemonName.Split('-');
        var baseName = parts[0].Capitalize();

        // Handle regional forms
        if (parts.Length > 1 && parts[^1].ToLower() == "galar")
            baseName += "-galar";

        return baseName ?? pokemonName;
    }

    /// <summary>
    ///     Handles Kyurem-White fusion.
    /// </summary>
    private (bool Success, string Message, string NewFormName) HandleKyuremWhiteFusion(
        Database.Linq.Models.Pokemon.Pokemon kyurem, Database.Linq.Models.Pokemon.Pokemon target)
    {
        if (kyurem.PokemonName.ToLower() != "kyurem")
            return (false, $"You cannot fuse a {kyurem.PokemonName} with Reshiram.", string.Empty);

        if (target.PokemonName.ToLower() != "reshiram")
            return (false, "That is not a Reshiram. Please use `/pokemon forms fuse white <reshiram_number>` to fuse Kyurem with Reshiram.", string.Empty);

        if (string.IsNullOrEmpty(kyurem.HeldItem) || kyurem.HeldItem.ToLower() != "light-stone")
            return (false, "Your Kyurem is not holding a Light Stone.\nYou need to buy it from the Shop.", string.Empty);

        return (true, string.Empty, "Kyurem-white");
    }

    /// <summary>
    ///     Handles Kyurem-Black fusion.
    /// </summary>
    private (bool Success, string Message, string NewFormName) HandleKyuremBlackFusion(
        Database.Linq.Models.Pokemon.Pokemon kyurem, Database.Linq.Models.Pokemon.Pokemon target)
    {
        if (kyurem.PokemonName.ToLower() != "kyurem")
            return (false, $"You cannot fuse a {kyurem.PokemonName} with Zekrom.", string.Empty);

        if (target.PokemonName.ToLower() != "zekrom")
            return (false, "That is not a Zekrom. Please use `/pokemon forms fuse black <zekrom_number>` to fuse Kyurem with Zekrom.", string.Empty);

        if (string.IsNullOrEmpty(kyurem.HeldItem) || kyurem.HeldItem.ToLower() != "dark-stone")
            return (false, "Your Kyurem is not holding a Dark Stone.\nYou need to buy it from the Shop.", string.Empty);

        return (true, string.Empty, "Kyurem-black");
    }

    /// <summary>
    ///     Handles Calyrex-Ice Rider fusion.
    /// </summary>
    private (bool Success, string Message, string NewFormName) HandleCalyrexIceFusion(
        Database.Linq.Models.Pokemon.Pokemon calyrex, Database.Linq.Models.Pokemon.Pokemon target)
    {
        if (calyrex.PokemonName.ToLower() != "calyrex")
            return (false, $"You cannot fuse a {calyrex.PokemonName} with Glastrier.", string.Empty);

        if (target.PokemonName.ToLower() != "glastrier")
            return (false, "That is not a Glastrier. Please use `/pokemon forms fuse ice <glastrier_number>` to fuse Calyrex with Glastrier.", string.Empty);

        if (string.IsNullOrEmpty(calyrex.HeldItem) || calyrex.HeldItem.ToLower() != "reins-of-unity")
            return (false, "Your Calyrex is not holding the Reins of Unity.\nYou need to buy it from the Shop.", string.Empty);

        return (true, string.Empty, "Calyrex-ice-rider");
    }

    /// <summary>
    ///     Handles Calyrex-Shadow Rider fusion.
    /// </summary>
    private (bool Success, string Message, string NewFormName) HandleCalyrexShadowFusion(
        Database.Linq.Models.Pokemon.Pokemon calyrex, Database.Linq.Models.Pokemon.Pokemon target)
    {
        if (calyrex.PokemonName.ToLower() != "calyrex")
            return (false, $"You cannot fuse a {calyrex.PokemonName} with Spectrier.", string.Empty);

        if (target.PokemonName.ToLower() != "spectrier")
            return (false, "That is not a Spectrier. Please use `/pokemon forms fuse shadow <spectrier_number>` to fuse Calyrex with Spectrier.", string.Empty);

        if (string.IsNullOrEmpty(calyrex.HeldItem) || calyrex.HeldItem.ToLower() != "reins-of-unity")
            return (false, "Your Calyrex is not holding the Reins of Unity.\nYou need to buy it from the Shop.", string.Empty);

        return (true, string.Empty, "Calyrex-shadow-rider");
    }

    /// <summary>
    ///     Gets the mega form name for a Pokemon.
    /// </summary>
    private async Task<string> GetMegaFormName(string pokemonName)
    {
        try
        {
            var pokemonInfo = await mongo.Forms
                .Find(f => f.Identifier == pokemonName)
                .FirstOrDefaultAsync();

            if (pokemonInfo == null)
                return string.Empty;

            var megaForm = await mongo.Forms
                .Find(f => f.Order == pokemonInfo.Order + 1)
                .FirstOrDefaultAsync();

            return megaForm?.Identifier ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting mega form for {PokemonName}", pokemonName);
            return string.Empty;
        }
    }

    /// <summary>
    ///     Gets the mega X form name for a Pokemon.
    /// </summary>
    private async Task<string> GetMegaXFormName(string pokemonName)
    {
        try
        {
            var pokemonInfo = await mongo.Forms
                .Find(f => f.Identifier == pokemonName)
                .FirstOrDefaultAsync();

            if (pokemonInfo == null)
                return string.Empty;

            var megaXForm = await mongo.Forms
                .Find(f => f.Order == pokemonInfo.Order + 1)
                .FirstOrDefaultAsync();

            return megaXForm?.Identifier ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting mega X form for {PokemonName}", pokemonName);
            return string.Empty;
        }
    }

    /// <summary>
    ///     Gets the mega Y form name for a Pokemon.
    /// </summary>
    private async Task<string> GetMegaYFormName(string pokemonName)
    {
        try
        {
            var pokemonInfo = await mongo.Forms
                .Find(f => f.Identifier == pokemonName)
                .FirstOrDefaultAsync();

            if (pokemonInfo == null)
                return string.Empty;

            var megaYForm = await mongo.Forms
                .Find(f => f.Order == pokemonInfo.Order + 2)
                .FirstOrDefaultAsync();

            return megaYForm?.Identifier ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting mega Y form for {PokemonName}", pokemonName);
            return string.Empty;
        }
    }

    /// <summary>
    ///     Gets the base form name for a mega Pokemon.
    /// </summary>
    private async Task<string> GetBaseMegaFormName(string megaPokemonName)
    {
        try
        {
            var megaInfo = await mongo.Forms
                .Find(f => f.Identifier == megaPokemonName)
                .FirstOrDefaultAsync();

            if (megaInfo == null)
                return string.Empty;

            var baseForm = await mongo.Forms
                .Find(f => f.Order == megaInfo.Order - 1)
                .FirstOrDefaultAsync();

            return baseForm?.Identifier ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error getting base form for mega {MegaPokemonName}", megaPokemonName);
            return string.Empty;
        }
    }

    #endregion
}