using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using Utility.EnumsStorage;

namespace VeteranSoldiers;

public sealed class ModSettings
{
    public readonly ConfigFile File;
    public readonly ConfigEntry<bool> Enabled, ExperienceFromDamage, ExperienceFromKills, ShowExperienceBar,
        ShowVeteranInsignias, EnableSiegeUnits, EnableEnemyVeterancy, Persistence, DebugLogging, StructureExperience, ShowPanel, ShowPromotionNotifications, EnableSpecializations;
    public readonly ConfigEntry<KeyboardShortcut> ToggleInterfaceShortcut, ArmyShortcut, DiagnosticsShortcut;
    public readonly ConfigEntry<double> ExperienceMultiplier, DamagePerExperience, KillExperience,
        MeleeMultiplier, RangedMultiplier, SiegeMultiplier, PowerfulKillMultiplier, StructureMultiplier;
    public readonly ConfigEntry<float> MaxDamage, MaxHealth, PanelScale;
    public readonly ConfigEntry<int> MaxLevel;
    public readonly ConfigEntry<string> IncludedUnitTypes, Language;
    public readonly Rank[] Ranks;
    public readonly HashSet<UnitType> Included = new HashSet<UnitType>();

    public ModSettings(ConfigFile file)
    {
        File = file;
        Enabled = file.Bind("General", "Enabled", true, "Enable veterancy. Restart the game after changing configuration.");
        Persistence = file.Bind("General", "Persistence", true, "Store a hash-verified sidecar in BepInEx/config/VeteranSoldiers/saves.");
        DebugLogging = file.Bind("General", "DebugLogging", false, "Log XP and rank changes; this can produce many messages.");
        EnableSpecializations = file.Bind("Specializations", "Enabled", true, "Optional personal choice at Veteran rank: Assault +5% damage, Guardian +10% maximum health (no healing), Mentor +20% earned XP. One choice per soldier. Disabling suspends effects but preserves saved choices. Restart after changing.");
        ExperienceFromDamage = file.Bind("Experience", "ExperienceFromDamage", true);
        ExperienceFromKills = file.Bind("Experience", "ExperienceFromKills", true);
        ExperienceMultiplier = Number(file, "Experience", "ExperienceMultiplier", 1, 0, 100);
        DamagePerExperience = Number(file, "Experience", "DamagePerExperience", 5, .001, 1000000);
        KillExperience = Number(file, "Experience", "KillExperience", 5, 0, 1000000);
        PowerfulKillMultiplier = Number(file, "Experience", "PowerfulKillMultiplier", 1.5, 1, 10);
        StructureExperience = file.Bind("Experience", "StructureExperience", true);
        StructureMultiplier = Number(file, "Experience", "StructureMultiplier", .25, 0, 10);
        MeleeMultiplier = Number(file, "Balance", "MeleeMultiplier", 1, 0, 10);
        RangedMultiplier = Number(file, "Balance", "RangedMultiplier", 1, 0, 10);
        SiegeMultiplier = Number(file, "Balance", "SiegeMultiplier", .5, 0, 10);
        EnableSiegeUnits = file.Bind("Units", "EnableSiegeUnits", true);
        EnableEnemyVeterancy = file.Bind("Units", "EnableEnemyVeterancy", false, "Optional: enemies also progress when damaging the opposing side.");
        IncludedUnitTypes = file.Bind("Units", "IncludedUnitTypes", "*", "* or comma-separated UnitType names (Archer,Swordsman,...). Only eligible military units with Health participate.");
        if (IncludedUnitTypes.Value != "*")
            foreach (string token in IncludedUnitTypes.Value.Split(','))
            {
                if (!Enum.TryParse(token.Trim(), true, out UnitType unitType) || !Enum.IsDefined(typeof(UnitType), unitType))
                    throw new ArgumentException("Unknown UnitType in IncludedUnitTypes: " + token);
                Included.Add(unitType);
            }
        MaxLevel = file.Bind("Ranks", "MaxLevel", 5, new ConfigDescription("1 to 5", new AcceptableValueRange<int>(1, 5)));
        MaxDamage = file.Bind("Limits", "MaxDamagePerHit", 100000f, new ConfigDescription("Caps damage with veterancy; never reduces existing base-game damage.", new AcceptableValueRange<float>(1, 10000000)));
        MaxHealth = file.Bind("Limits", "MaxHealth", 100000f, new ConfigDescription("Never reduces pre-existing base-game maximum health.", new AcceptableValueRange<float>(1, 10000000)));
        Language = file.Bind("Interface", "Language", "English", new ConfigDescription("Mod interface language. In-game journal settings apply immediately; restart after editing this file. Does not change the game's language.", new AcceptableValueList<string>("English", "Spanish")));
        VeteranText.Configure(Language.Value);
        ShowExperienceBar = file.Bind("Interface", "ShowExperienceBar", true);
        ShowVeteranInsignias = file.Bind("Interface", "ShowVeteranInsignias", true, "Rank shields in the panel and above selected veterans. Respects visibility and limits icon density.");
        ShowPanel = file.Bind("Interface", "ShowPanel", true, "Veterancy in the native panel. ToggleInterfaceShortcut temporarily hides mod visuals.");
        ShowPromotionNotifications = file.Bind("Interface", "ShowPromotionNotifications", true, "Show one grouped card for friendly combat promotions. Click to select and locate a surviving promoted soldier. No notices on save restore.");
        ToggleInterfaceShortcut = file.Bind("Interface", "ToggleInterfaceShortcut", new KeyboardShortcut(KeyCode.F8), "Visual toggle only. Change to avoid another mod's shortcut (e.g. F8 + LeftControl), or None to disable. Does not consume the game's key input.");
        ArmyShortcut = file.Bind("Interface", "ArmyShortcut", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl), "Open the veteran journal: roster, soldier records, Hall, memorial and settings. Configurable; None disables the shortcut. A HUD button also opens the journal.");
        DiagnosticsShortcut = file.Bind("Interface", "DiagnosticsShortcut", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl), "Write a privacy-conscious conflict report on demand. No compatibility certification; None disables the shortcut.");
        PanelScale = file.Bind("Interface", "PanelScale", 1f, new ConfigDescription("Scale of the veterancy section inside the native panel.", new AcceptableValueRange<float>(.75f, 1.5f)));
        if (!Progression.Finite(MaxDamage.Value) || !Progression.Finite(MaxHealth.Value) || !Progression.Finite(PanelScale.Value))
            throw new ArgumentException("Health, damage and panel limits must be finite.");
        Ranks = Progression.Defaults();
        for (int i = 0; i < Ranks.Length; i++)
        {
            string section = "Rank" + (i + 1);
            string customName = file.Bind(section, "Name", "", "Optional custom rank name. Empty uses the selected interface language. Existing custom names are preserved.").Value;
            Ranks[i].Name = VeteranText.ResolveRankName(i, customName);
            Ranks[i].Threshold = file.Bind(section, "Experience", Ranks[i].Threshold).Value;
            string bonusDescription = i == 0 ? "Recruit keeps base-game stats: leave at 0." : "Fractional bonus: 0.10 = 10%.";
            Ranks[i].DamageBonus = file.Bind(section, "DamageBonus", Ranks[i].DamageBonus, bonusDescription).Value;
            Ranks[i].HealthBonus = file.Bind(section, "HealthBonus", Ranks[i].HealthBonus, bonusDescription).Value;
        }
        Progression.Validate(Ranks);
        file.Save();
    }

    internal void ApplyInterfaceLanguage(string language)
    {
        if (language != "English" && language != "Spanish") throw new ArgumentException("Unsupported mod interface language.", nameof(language));
        Language.Value = language;
        VeteranText.Configure(language);
        for (int i = 0; i < Ranks.Length; i++)
        {
            string custom = File.TryGetEntry<string>("Rank" + (i + 1), "Name", out var entry) ? entry.Value : "";
            Ranks[i].Name = VeteranText.ResolveRankName(i, custom);
        }
        File.Save();
    }

    private static ConfigEntry<double> Number(ConfigFile file, string section, string name, double value, double min, double max)
    {
        var entry = file.Bind(section, name, value, new ConfigDescription("Finite value.", new AcceptableValueRange<double>(min, max)));
        if (!Progression.Finite(entry.Value)) throw new ArgumentException(name + " must be finite.");
        return entry;
    }
}
