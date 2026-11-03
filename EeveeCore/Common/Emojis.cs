namespace EeveeCore.Common;

/// <summary>
///     Custom emojis the bot uses, hosted on the EeveeCore Official server.
/// </summary>
public static class Emojis
{
    /// <summary>The shiny sparkle.</summary>
    public const string Shiny = "<a:shiny:1566977266213191681>";

    /// <summary>The shiny sparkle shown in the Extras commands.</summary>
    public const string ShinyAlt = "<:shiny_2:1566977967727312897>";

    /// <summary>The radiant badge.</summary>
    public const string Radiant = "<:radiant_2:1566977935426990081>";

    /// <summary>The animated radiant badge.</summary>
    public const string RadiantAnimated = "<a:newradhmm:1566978125538000896>";

    /// <summary>The shadow badge.</summary>
    public const string Shadow = "<:shadowicon4:1566977568651882496>";

    /// <summary>The badge for a custom skin.</summary>
    public const string Skin = "<:skin23:1566977581759082496>";

    /// <summary>The animated badge for a custom Pokémon.</summary>
    public const string Custom = "<a:custom:1566978087248207873>";

    /// <summary>The staff badge.</summary>
    public const string Staff = "<:staff2:1566977997162950657>";

    /// <summary>The 2x premium badge.</summary>
    public const string PremiumX2 = "<:premiumX2:1566977884059336706>";

    /// <summary>The 3x premium badge.</summary>
    public const string PremiumX3 = "<:premiumX3:1566977897711796225>";

    /// <summary>The mission icon.</summary>
    public const string Mission = "<:mission:1566977482307932160>";

    /// <summary>The star icon.</summary>
    public const string Star = "<:starrr:1566977999872462850>";

    /// <summary>The crystallized slime item.</summary>
    public const string CrystallizedSlime = "<:CrystallizedSlime:1566977393170583552>";

    /// <summary>The error icon.</summary>
    public const string Error = "<:error:1566977420844601345>";

    /// <summary>The save icon.</summary>
    public const string Save = "<:save:1566977939801645059>";

    /// <summary>The delete icon.</summary>
    public const string Delete = "<:delete:1566977403505356800>";

    /// <summary>The delete icon used in the Pokémon commands.</summary>
    public const string DeleteAlt = "<:delete_2:1566977766383943680>";

    /// <summary>An empty spacer.</summary>
    public const string Blank = "<:blank:1566977702496309250>";

    /// <summary>The animated left arrow.</summary>
    public const string LeftArrow = "<a:leftarrow:1566977249373061120>";

    /// <summary>The animated right arrow.</summary>
    public const string RightArrow = "<a:rightarrow:1566977253848387585>";

    /// <summary>The numbers one to six, for party slots.</summary>
    public static readonly string[] Numbers =
    [
        "<:num1:1566977174957723648>", "<:num2:1566977183316979712>", "<:num3:1566977191986606080>",
        "<:num4:1566977198907195392>", "<:num5:1566977204762447872>", "<:num6:1566977210479288320>"
    ];

    /// <summary>The physical move category, in two parts.</summary>
    public const string Physical = "<:phys1:1566977493351534592><:phys2:1566977502335741952>";

    /// <summary>The special move category, in two parts.</summary>
    public const string Special = "<:special1:1566977973259603970><:special2:1566977595101163521>";

    /// <summary>The status move category, in two parts.</summary>
    public const string Status = "<:status1:1566977608598433794><:status2:1566978019136901122>";

    /// <summary>
    ///     Pieces of the progress bars: start, middle and end, filled and empty.
    /// </summary>
    public static class Bar
    {
        /// <summary>Filled start.</summary>
        public const string Start = "<:bar_e1:1566977326292414465>";

        /// <summary>Filled middle.</summary>
        public const string Middle = "<:bar_e2:1566977330432188417>";

        /// <summary>Filled end.</summary>
        public const string End = "<:bar_e3:1566977341735841793>";

        /// <summary>Empty start.</summary>
        public const string EmptyStart = "<:bar1:1566977276690567168>";

        /// <summary>Empty middle.</summary>
        public const string EmptyMiddle = "<:bar2:1566977296970022912>";

        /// <summary>Empty end.</summary>
        public const string EmptyEnd = "<:bar3:1566977303370534914>";

        /// <summary>Filled start of the mission bar.</summary>
        public const string MissionStart = "<:bar1_2:1566977283300798464>";

        /// <summary>Filled middle of the mission bar.</summary>
        public const string MissionMiddle = "<:bar2_2:1566977285557325825>";

        /// <summary>Filled end of the mission bar.</summary>
        public const string MissionEnd = "<:bar3_2:1566977318864289793>";

        /// <summary>Empty start of the mission bar.</summary>
        public const string MissionEmptyStart = "<:bar1e:1566977286530408450>";

        /// <summary>Empty middle of the mission bar.</summary>
        public const string MissionEmptyMiddle = "<:bar2e:1566977309490024448>";

        /// <summary>Empty end of the mission bar.</summary>
        public const string MissionEmptyEnd = "<:bar3_3:1566977333372387328>";

        /// <summary>Start of the beam bar.</summary>
        public const string BeamStart = "<:beamstart2:1566977365580455936>";

        /// <summary>Middle of the beam bar.</summary>
        public const string BeamMiddle = "<:beambar2:1566977689510744064>";
    }

    /// <summary>
    ///     Type icons, by lowercase type name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>
    {
        ["bug"] = "<:bug:1566977355530903553>",
        ["dark"] = "<:dark:1566977750214905856>",
        ["dragon"] = "<:dragon:1566977408861470721>",
        ["electric"] = "<:electric:1566977765377314817>",
        ["fairy"] = "<:fairy:1566977794875863040>",
        ["fighting"] = "<:fighting:1566977423776423937>",
        ["fire"] = "<:fire:1566977808809340928>",
        ["flying"] = "<:flying:1566977450376695809>",
        ["ghost"] = "<:ghost:1566977823430676480>",
        ["grass"] = "<:grass:1566977461462237185>",
        ["ground"] = "<:ground:1566977838572118016>",
        ["ice"] = "<:ice:1566977852648202240>",
        ["normal"] = "<:normal:1566977857513598977>",
        ["poison"] = "<:poison:1566977516546039808>",
        ["psychic"] = "<:psychic:1566977917559242754>",
        ["rock"] = "<:rock:1566977555976695808>",
        ["steel"] = "<:steel:1566978044420161537>",
        ["water"] = "<:water:1566977620136960001>"
    };

    /// <summary>
    ///     Gets the icon for a type, or the type name when there is no icon.
    /// </summary>
    /// <param name="type">The type name.</param>
    public static string Type(string type) =>
        Types.TryGetValue(type.ToLowerInvariant(), out var icon) ? icon : type;
}
