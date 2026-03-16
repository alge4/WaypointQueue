using Game.Messages;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WaypointQueue.UUM;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(RequestSetTrainCrewTimetableSymbol))]
    internal static class PatchRequestSetTrainCrewTimetableSymbol
    {
        private static bool _loggedMapping;

        static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredConstructors(typeof(RequestSetTrainCrewTimetableSymbol));
        }

        [HarmonyPostfix]
        static void CaptureCrewSymbol(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (__instance == null) return;

            var (crewId, symbol, mappingSource) = ResolveCrewSymbol(__instance, __originalMethod, __args);
            if (!_loggedMapping)
            {
                _loggedMapping = true;
                Loader.Log($"[RouteAssign] RequestSetTrainCrewTimetableSymbol mapping source={mappingSource} crewId='{crewId ?? "<null>"}' symbol='{symbol ?? "<null>"}'");
            }

            WaypointQueueController.Shared?.NoteCrewSymbolChanged(crewId, symbol);
        }

        private static (string crewId, string symbol, string source) ResolveCrewSymbol(object instance, MethodBase ctor, object[] args)
        {
            // 1) Prefer constructor parameter names + args (most stable contract).
            ParameterInfo[] parameters = ctor?.GetParameters() ?? [];
            if (args != null && args.Length == parameters.Length && args.Length > 0)
            {
                string crewFromName = null;
                string symbolFromName = null;

                for (int i = 0; i < parameters.Length; i++)
                {
                    string name = (parameters[i].Name ?? "").ToLowerInvariant();
                    string value = CoerceString(args[i], preferSymbol: name.Contains("symbol") || name.Contains("timetable"));
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (crewFromName == null && name.Contains("crew"))
                    {
                        crewFromName = value;
                    }
                    if (symbolFromName == null && (name.Contains("symbol") || name.Contains("timetable")))
                    {
                        symbolFromName = value;
                    }
                }

                if (!string.IsNullOrWhiteSpace(crewFromName) || !string.IsNullOrWhiteSpace(symbolFromName))
                {
                    return (crewFromName, symbolFromName, "ctor-name");
                }

                // Fallback positional guess for common 2-string ctor.
                string[] stringArgs = args.OfType<string>().ToArray();
                if (stringArgs.Length >= 2)
                {
                    return (stringArgs[0], stringArgs[1], "ctor-positional");
                }
            }

            // 2) Last-resort reflection on instance fields.
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string crewId =
                instance.GetType().GetField("trainCrewId", flags)?.GetValue(instance) as string ??
                instance.GetType().GetField("crewId", flags)?.GetValue(instance) as string ??
                instance.GetType().GetField("_trainCrewId", flags)?.GetValue(instance) as string ??
                instance.GetType().GetField("_crewId", flags)?.GetValue(instance) as string;

            string symbol =
                instance.GetType().GetField("timetableSymbol", flags)?.GetValue(instance) as string ??
                instance.GetType().GetField("symbol", flags)?.GetValue(instance) as string ??
                instance.GetType().GetField("_timetableSymbol", flags)?.GetValue(instance) as string ??
                instance.GetType().GetField("_symbol", flags)?.GetValue(instance) as string;

            return (crewId, symbol, "instance-field");
        }

        private static string CoerceString(object value, bool preferSymbol)
        {
            if (value == null) return null;
            if (value is string s) return string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            Type type = value.GetType();
            if (type.IsEnum)
            {
                string enumName = value.ToString();
                return string.IsNullOrWhiteSpace(enumName) ? null : enumName.Trim();
            }

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string[] preferredNames = preferSymbol
                ? new[] { "Symbol", "symbol", "TimetableSymbol", "timetableSymbol", "Name", "name", "Value", "value", "Id", "id" }
                : new[] { "trainCrewId", "crewId", "Id", "id", "Name", "name", "Value", "value" };

            foreach (string memberName in preferredNames)
            {
                PropertyInfo p = type.GetProperty(memberName, flags);
                if (p != null && p.CanRead)
                {
                    object candidate = null;
                    try { candidate = p.GetValue(value); } catch { }
                    string parsed = CoerceLeafString(candidate);
                    if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
                }

                FieldInfo f = type.GetField(memberName, flags);
                if (f != null)
                {
                    object candidate = null;
                    try { candidate = f.GetValue(value); } catch { }
                    string parsed = CoerceLeafString(candidate);
                    if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
                }
            }

            string fallback = value.ToString();
            return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
        }

        private static string CoerceLeafString(object value)
        {
            if (value == null) return null;
            if (value is string s) return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is decimal)
            {
                string converted = value.ToString();
                return string.IsNullOrWhiteSpace(converted) ? null : converted.Trim();
            }
            return null;
        }
    }
}
