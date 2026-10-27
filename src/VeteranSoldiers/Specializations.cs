namespace VeteranSoldiers;

public enum Specialization { None = 0, Assault = 1, Guardian = 2, Mentor = 3 }

/// <summary>Personal bonuses; never modifies a shared unit prefab or grants healing.</summary>
public static class Specializations
{
    public const int UnlockLevel = 2;
    public static bool Valid(Specialization value) => value >= Specialization.None && value <= Specialization.Mentor;
    public static bool Active(Specialization value, int level, bool enabled) => enabled && level >= UnlockLevel && Valid(value);
    public static float Damage(Specialization value, int level, bool enabled) =>
        Active(value, level, enabled) && value == Specialization.Assault ? .05f : 0;
    public static float Health(Specialization value, int level, bool enabled) =>
        Active(value, level, enabled) && value == Specialization.Guardian ? .10f : 0;
    public static double Experience(Specialization value, int level, bool enabled) =>
        Active(value, level, enabled) && value == Specialization.Mentor ? 1.20 : 1;
}
