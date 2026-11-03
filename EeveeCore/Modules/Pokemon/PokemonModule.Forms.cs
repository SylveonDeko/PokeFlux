using EeveeCore.Common.ModuleBases;
using EeveeCore.Modules.Pokemon.Services;

namespace EeveeCore.Modules.Pokemon;

public partial class PokemonModule
{
    /// <summary>
    ///     Form changes, mega evolution and fusions for the selected Pokémon.
    /// </summary>
    [Group("pokemon forms")]
    [Name("Forms")]
    public class FormsModule : EeveeModule<FormsService>
    {
        private static readonly string[] FusionTypes = ["white", "black", "ice", "shadow"];

        /// <summary>
        ///     Transforms the selected Pokémon to a form.
        /// </summary>
        /// <param name="formName">The form to transform to.</param>
        [Command("form")]
        [Summary("Transform your selected Pokémon to a specific form. Usage: `pokemon forms form <form>`")]
        public async Task TransformForm([Remainder] string formName)
        {
            if (string.IsNullOrWhiteSpace(formName))
            {
                await ErrorAsync("Please specify a form name!");
                return;
            }

            if (formName.Length > 50)
            {
                await ErrorAsync("Form name is too long! Please use a shorter name.");
                return;
            }

            var result = await Service.TransformToFormAsync(ctx.User.Id, formName.Trim().ToLower());
            await CongratulateAsync(result, "Congratulations!!!", Color.Blue, true);
        }

        /// <summary>
        ///     Resets the selected Pokémon to its base form.
        /// </summary>
        [Command("deform")]
        [Summary("Reset your selected Pokémon to its base form. Usage: `pokemon forms deform`")]
        public async Task Deform()
        {
            var result = await Service.DeformPokemonAsync(ctx.User.Id);
            var message = result.Message.WithPrefix(await GetPrefixAsync());
            if (result.Success)
                await ConfirmAsync(message);
            else
                await ErrorAsync(message);
        }

        /// <summary>
        ///     Fuses the selected Pokémon with another of the player's Pokémon.
        /// </summary>
        /// <param name="fusionType">white, black, ice or shadow.</param>
        /// <param name="number">The number of the Pokémon to fuse with.</param>
        [Command("fuse")]
        [Summary("Fuse compatible Pokémon together. Usage: `pokemon forms fuse <type> <number>`\n" +
                 "Types: `white` (Kyurem + Reshiram), `black` (Kyurem + Zekrom), " +
                 "`ice` (Calyrex + Glastrier), `shadow` (Calyrex + Spectrier).")]
        public async Task Fuse(string fusionType, long number)
        {
            fusionType = fusionType.ToLowerInvariant();
            if (!FusionTypes.Contains(fusionType))
            {
                await ErrorAsync($"`{fusionType}` is not a fusion type. Use one of: {string.Join(", ", FusionTypes)}.");
                return;
            }

            if (!await CheckNumberAsync(number))
                return;

            var result = await Service.FusePokemonAsync(ctx.User.Id, fusionType, (ulong)number);
            await CongratulateAsync(result, "Fusion Complete!", Color.Purple, false);
        }

        /// <summary>
        ///     Fuses Necrozma with a Lunala into its Dawn Wings form.
        /// </summary>
        /// <param name="number">The number of the Lunala.</param>
        [Command("lunarize")]
        [Summary("Fuse Necrozma with Lunala to create Dawn Wings form. Usage: `pokemon forms lunarize <lunala number>`")]
        public async Task Lunarize(long number)
        {
            if (!await CheckNumberAsync(number))
                return;

            var result = await Service.LunarizePokemonAsync(ctx.User.Id, (ulong)number);
            await CongratulateAsync(result, "Fusion Complete!", Color.Gold, false);
        }

        /// <summary>
        ///     Fuses Necrozma with a Solgaleo into its Dusk Mane form.
        /// </summary>
        /// <param name="number">The number of the Solgaleo.</param>
        [Command("solarize")]
        [Summary("Fuse Necrozma with Solgaleo to create Dusk Mane form. Usage: `pokemon forms solarize <solgaleo number>`")]
        public async Task Solarize(long number)
        {
            if (!await CheckNumberAsync(number))
                return;

            var result = await Service.SolarizePokemonAsync(ctx.User.Id, (ulong)number);
            await CongratulateAsync(result, "Fusion Complete!", Color.Orange, false);
        }

        /// <summary>
        ///     Mega evolves the selected Pokémon.
        /// </summary>
        [Command("mega")]
        [Summary("Mega evolve your selected Pokémon. Usage: `pokemon forms mega`")]
        public async Task Mega() =>
            await CongratulateAsync(await Service.MegaEvolveAsync(ctx.User.Id), "Congratulations!!!", Color.Red, true);

        /// <summary>
        ///     Mega evolves the selected Pokémon to its X form.
        /// </summary>
        [Command("megax")]
        [Summary("Mega evolve your selected Pokémon to X form. Usage: `pokemon forms megax`")]
        public async Task MegaX() =>
            await CongratulateAsync(await Service.MegaEvolveXAsync(ctx.User.Id), "Congratulations!!!", Color.Red, true);

        /// <summary>
        ///     Mega evolves the selected Pokémon to its Y form.
        /// </summary>
        [Command("megay")]
        [Summary("Mega evolve your selected Pokémon to Y form. Usage: `pokemon forms megay`")]
        public async Task MegaY() =>
            await CongratulateAsync(await Service.MegaEvolveYAsync(ctx.User.Id), "Congratulations!!!", Color.Red, true);

        /// <summary>
        ///     Returns a mega Pokémon to its base form.
        /// </summary>
        [Command("unmega")]
        [Summary("Devolve your mega Pokémon back to base form. Usage: `pokemon forms unmega`")]
        public async Task Unmega() =>
            await CongratulateAsync(await Service.MegaDevolveAsync(ctx.User.Id), "Devolution Complete!", Color.Green,
                true);

        private async Task<bool> CheckNumberAsync(long number)
        {
            if (number < 1)
            {
                await ErrorAsync("Please provide a valid Pokemon number!");
                return false;
            }

            if (number > 1000000)
            {
                await ErrorAsync("That's way too many Pokemon! Please use a reasonable number.");
                return false;
            }

            return true;
        }

        private async Task CongratulateAsync((bool Success, string Message) result, string title, Color color,
            bool addressUser)
        {
            var message = result.Message.WithPrefix(await GetPrefixAsync());
            if (!result.Success)
            {
                await ErrorAsync(message);
                return;
            }

            await SendAsync(embed: new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(addressUser ? $"{ctx.User.Username}, {message}" : message)
                .WithColor(color));
        }
    }
}
