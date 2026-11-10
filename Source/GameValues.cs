using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ProjectZ
{
    // The game's own values that only a private member holds (a private const or a private static field): read from the
    // running game by reflection instead of copied, so a game update is followed. Read once, at the first use (a const
    // can't change while the game runs; the static fields read here are set by their type's own initialiser). The
    // fallback is the value of the game build ProjectZ was checked against, used only when the member is gone or has
    // another type (logged once per member); each caller keeps its fallback next to the rule it backs.
    public static class GameValues
    {
        public static T Private<T>(Type type, string name, T fallback)
        {
            try
            {
                FieldInfo f = AccessTools.Field(type, name);
                if (f != null && f.IsStatic)
                {
                    object v = f.IsLiteral ? f.GetRawConstantValue() : f.GetValue(null);
                    if (v is T t)
                    {
                        return t;
                    }
                }
            }
            catch (Exception)
            {
                // reported below
            }
            Log.Warning($"[ProjectZ] The game's {type.Name}.{name} could not be read; ProjectZ uses {fallback}.");
            return fallback;
        }
    }
}
