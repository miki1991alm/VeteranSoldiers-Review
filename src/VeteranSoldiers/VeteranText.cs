using System;
using System.Globalization;
using System.Text;

namespace VeteranSoldiers;

/// <summary>Mod-owned presentation text only; never changes the game's locale or stored experience.</summary>
internal static class VeteranText
{
    private static readonly string[] EnglishRanks = { "Recruit", "Trained", "Veteran", "Elite", "Legendary" };
    private static readonly string[] SpanishRanks = { "Recluta", "Entrenado", "Veterano", "Élite", "Legendario" };
    private static bool spanish;
    private static CultureInfo culture = CultureInfo.GetCultureInfo("en-US");

    internal static void Configure(string language)
    {
        spanish = string.Equals(language, "Spanish", StringComparison.OrdinalIgnoreCase);
        culture = CultureInfo.GetCultureInfo(spanish ? "es-ES" : "en-US");
    }

    internal static string RankName(int level) => (spanish ? SpanishRanks : EnglishRanks)[level];
    internal static string ResolveRankName(int level, string? customName) =>
        string.IsNullOrWhiteSpace(customName) ? RankName(level) : customName!;
    internal static string Number(double value, string format) => value.ToString(format, culture);
    internal static string Veterancy => spanish ? "VETERANÍA" : "VETERANCY";
    internal static string Maximum => spanish ? "MÁXIMO" : "MAX";
    internal static string MaximumRank => spanish ? "Rango máximo alcanzado" : "Maximum rank reached";
    internal static string Damage => spanish ? "DAÑO" : "DAMAGE";
    internal static string Health => spanish ? "VIDA" : "HEALTH";
    internal static string MaximumHealth => spanish ? "VIDA MÁX." : "MAX HEALTH";
    internal static string NextRank(string rank, double remaining) => spanish
        ? "Próximo: " + rank + "\nFaltan " + Number(remaining, "N1") + " XP"
        : "Next: " + rank + "\n" + Number(remaining, "N1") + " XP remaining";
    internal static string Soldiers(int count) => Number(count, "N0") +
        (spanish ? (count == 1 ? " soldado" : " soldados") : (count == 1 ? " soldier" : " soldiers"));
    internal static string Selected(int count) => Number(count, "N0") + (spanish ? " seleccionados" : " selected");
    internal static string PromotedTo(string rank) => (spanish ? "ASCENSO: " : "PROMOTED: ") + rank;
    internal static string PromotionCount(int count) => Number(count, "N0") + (spanish ? " ASCENSOS" : " PROMOTIONS");
    internal static string EarnedInCombat => spanish ? "EXPERIENCIA GANADA EN COMBATE" : "EXPERIENCE EARNED IN COMBAT";
    internal static string InspectVeteranStats => spanish
        ? "Selecciona al soldado para ver su experiencia y atributos."
        : "Select the soldier to inspect experience and stats.";
    internal static string Choose(string english, string spanishText) => spanish ? spanishText : english;
    internal static string SpecialtyName(Specialization value) => value == Specialization.Assault ? Choose("Assault", "Asalto") :
        value == Specialization.Guardian ? Choose("Guardian", "Guardián") : value == Specialization.Mentor ? Choose("Mentor", "Mentor") : Choose("Unassigned", "Sin elegir");
    internal static string SpecialtyEffect(Specialization value) => value == Specialization.Assault ? Choose("+5% damage", "+5 % daño") :
        value == Specialization.Guardian ? Choose("+10% max health · no healing", "+10 % vida máx. · sin curación") : Choose("+20% earned XP", "+20 % XP obtenida");
    internal static string MedalName(ChronicleMedal value) => value switch
    {
        ChronicleMedal.Blooded => Choose("Baptism of fire", "Bautismo de fuego"),
        ChronicleMedal.BattleHardened => Choose("Battle hardened", "Curtido en combate"),
        ChronicleMedal.Slayer => Choose("Slayer", "Azote"),
        ChronicleMedal.Champion => Choose("Champion", "Campeón"),
        ChronicleMedal.SiegeBreaker => Choose("Siege breaker", "Rompedor de asedios"),
        ChronicleMedal.Veteran => Choose("Veteran's honor", "Honor veterano"),
        _ => Choose("No decorations yet", "Sin condecoraciones todavía")
    };
    internal static string Medals(ChronicleMedal medals)
    {
        if (medals == ChronicleMedal.None) return MedalName(medals);
        var result = new StringBuilder();
        foreach (ChronicleMedal medal in Enum.GetValues(typeof(ChronicleMedal)))
        {
            if (medal == ChronicleMedal.None || (medals & medal) == 0) continue;
            if (result.Length > 0) result.Append(" · ");
            result.Append(MedalName(medal));
        }
        return result.ToString();
    }
    internal static string ChronicleEvent(ChronicleEvent record) => record.Kind switch
    {
        ChronicleEventKind.FirstCombat => Choose("First verified combat contribution", "Primera contribución de combate verificada"),
        ChronicleEventKind.Promotion => Choose("Promoted to ", "Ascendió a ") + RankName(Math.Max(0, Math.Min(4, record.Level))),
        ChronicleEventKind.Medal => Choose("Earned: ", "Obtuvo: ") + MedalName((ChronicleMedal)(int)record.Value),
        ChronicleEventKind.Specialization => Choose("Chose the path of ", "Eligió la senda de ") + SpecialtyName((Specialization)(int)record.Value),
        ChronicleEventKind.Fallen => Choose("Fell in service", "Cayó en servicio"),
        _ => Choose("Service recorded", "Servicio registrado")
    };
}
