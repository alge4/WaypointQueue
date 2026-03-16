using Game.State;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using WaypointQueue.UUM;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(StateManager))]
    internal static class PatchStateManagerApplyLocal
    {
        private static bool _loggedFirstSymbolMessage;
        private static bool _loggedFirstBulkExtract;
        private static bool _loggedUpdateTrainCrewsShape;

        static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (MethodInfo mi in typeof(StateManager).GetMethods(flags))
            {
                if (mi.Name != "ApplyLocal") continue;
                yield return mi;
            }
        }

        [HarmonyPostfix]
        static void ObserveApplyLocal(object __0)
        {
            if (__0 == null) return;

            string typeName = __0.GetType().Name;
            if (string.Equals(typeName, "RequestSetTrainCrewTimetableSymbol", StringComparison.OrdinalIgnoreCase))
            {
                // Handled by PatchRequestSetTrainCrewTimetableSymbol with richer ctor mapping.
                return;
            }

            if (!ContainsIgnoreCase(typeName, "TrainCrew") &&
                !ContainsIgnoreCase(typeName, "Timetable") &&
                !ContainsIgnoreCase(typeName, "Symbol"))
            {
                return;
            }

            if (ContainsIgnoreCase(typeName, "UpdateTrainCrews"))
            {
                List<(string crewId, string symbol)> updatePairs = ExtractFromUpdateTrainCrews(__0);
                if (updatePairs.Count > 0)
                {
                    foreach ((string pairCrewId, string pairSymbol) in updatePairs)
                    {
                        WaypointQueueController.Shared?.NoteCrewSymbolChanged(pairCrewId, pairSymbol);
                    }

                    if (!_loggedFirstBulkExtract)
                    {
                        _loggedFirstBulkExtract = true;
                        var sample = updatePairs[0];
                        Loader.Log($"[RouteAssign] ApplyLocal extracted {updatePairs.Count} crew-symbol pair(s) from type={typeName}; sample crewId='{sample.crewId}' symbol='{sample.symbol}'");
                    }
                    return;
                }
            }

            List<(string crewId, string symbol)> extractedPairs = ExtractCrewSymbolPairs(__0);
            if (extractedPairs.Count > 0)
            {
                if (!_loggedFirstBulkExtract)
                {
                    _loggedFirstBulkExtract = true;
                    var sample = extractedPairs[0];
                    Loader.Log($"[RouteAssign] ApplyLocal extracted {extractedPairs.Count} crew-symbol pair(s) from type={typeName}; sample crewId='{sample.crewId}' symbol='{sample.symbol}'");
                }

                foreach ((string pairCrewId, string pairSymbol) in extractedPairs)
                {
                    WaypointQueueController.Shared?.NoteCrewSymbolChanged(pairCrewId, pairSymbol);
                }
                return;
            }

            var (crewId, symbol) = TryExtractCrewSymbolPair(__0);
            if (!_loggedFirstSymbolMessage)
            {
                _loggedFirstSymbolMessage = true;
                Loader.Log($"[RouteAssign] ApplyLocal observed type={typeName} crewId='{crewId ?? "<null>"}' symbol='{symbol ?? "<null>"}'");
            }
            if (!_loggedUpdateTrainCrewsShape && ContainsIgnoreCase(typeName, "UpdateTrainCrews") &&
                string.IsNullOrWhiteSpace(crewId) && string.IsNullOrWhiteSpace(symbol))
            {
                _loggedUpdateTrainCrewsShape = true;
                Loader.Log($"[RouteAssign] ApplyLocal shape type={typeName}: {DescribeMemberShape(__0)}");
            }

            if (!string.IsNullOrWhiteSpace(crewId))
            {
                WaypointQueueController.Shared?.NoteCrewSymbolChanged(crewId, symbol);
            }
        }

        private static (string crewId, string symbol) TryExtractCrewSymbolPair(object message)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            string crewId = null;
            string symbol = null;

            foreach (PropertyInfo p in message.GetType().GetProperties(flags))
            {
                if (!p.CanRead) continue;
                string name = p.Name.ToLowerInvariant();
                object raw = null;
                try { raw = p.GetValue(message); } catch { }
                string value = CoerceToString(raw);
                if (string.IsNullOrEmpty(value)) continue;

                if (crewId == null && IsCrewName(name))
                {
                    crewId = value;
                }
                else if (symbol == null && IsSymbolName(name))
                {
                    symbol = value;
                }
            }

            foreach (FieldInfo f in message.GetType().GetFields(flags))
            {
                string name = f.Name.ToLowerInvariant();
                object raw = null;
                try { raw = f.GetValue(message); } catch { }
                string value = CoerceToString(raw);
                if (string.IsNullOrEmpty(value)) continue;

                if (crewId == null && IsCrewName(name))
                {
                    crewId = value;
                }
                else if (symbol == null && IsSymbolName(name))
                {
                    symbol = value;
                }
            }

            return (crewId, symbol);
        }

        private static List<(string crewId, string symbol)> ExtractCrewSymbolPairs(object root)
        {
            List<(string crewId, string symbol)> pairs = new();
            if (root == null) return pairs;

            HashSet<int> visited = new();
            Queue<(object value, int depth)> queue = new();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (current == null || depth > 4) continue;

                Type type = current.GetType();
                if (type == typeof(string) || type.IsPrimitive || type.IsEnum) continue;

                int identity = RuntimeHelpers.GetHashCode(current);
                if (!visited.Add(identity)) continue;

                if (current is IEnumerable enumerable && current is not string)
                {
                    foreach (object item in enumerable)
                    {
                        if (item == null) continue;
                        queue.Enqueue((item, depth + 1));
                    }
                }

                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                string crewCandidate = null;
                string symbolCandidate = null;

                foreach (PropertyInfo p in type.GetProperties(flags))
                {
                    object value = SafeGet(() => p.GetValue(current));
                    string n = p.Name.ToLowerInvariant();
                    if (crewCandidate == null && IsCrewName(n))
                    {
                        string maybeCrew = CoerceToString(value);
                        if (!string.IsNullOrWhiteSpace(maybeCrew)) crewCandidate = maybeCrew;
                    }
                    if (symbolCandidate == null && IsSymbolName(n))
                    {
                        string maybeSymbol = CoerceToString(value);
                        if (!string.IsNullOrWhiteSpace(maybeSymbol)) symbolCandidate = maybeSymbol;
                    }

                    if (value != null && p.PropertyType != typeof(string) && !p.PropertyType.IsPrimitive && !p.PropertyType.IsEnum)
                    {
                        queue.Enqueue((value, depth + 1));
                    }
                }

                foreach (FieldInfo f in type.GetFields(flags))
                {
                    object value = SafeGet(() => f.GetValue(current));
                    string n = f.Name.ToLowerInvariant();
                    if (crewCandidate == null && IsCrewName(n))
                    {
                        string maybeCrew = CoerceToString(value);
                        if (!string.IsNullOrWhiteSpace(maybeCrew)) crewCandidate = maybeCrew;
                    }
                    if (symbolCandidate == null && IsSymbolName(n))
                    {
                        string maybeSymbol = CoerceToString(value);
                        if (!string.IsNullOrWhiteSpace(maybeSymbol)) symbolCandidate = maybeSymbol;
                    }

                    if (value != null && f.FieldType != typeof(string) && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum)
                    {
                        queue.Enqueue((value, depth + 1));
                    }
                }

                if (!string.IsNullOrWhiteSpace(crewCandidate) && !string.IsNullOrWhiteSpace(symbolCandidate))
                {
                    pairs.Add((crewCandidate, symbolCandidate));
                }
            }

            return pairs;
        }

        private static object SafeGet(Func<object> getter)
        {
            try { return getter(); }
            catch { return null; }
        }

        private static string CoerceToString(object value)
        {
            if (value == null) return null;
            if (value is string s)
            {
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }

            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is decimal)
            {
                string text = value.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string[] preferredNames = { "Id", "id", "Name", "name", "Value", "value", "Symbol", "symbol", "TimetableSymbol", "timetableSymbol" };
            foreach (string memberName in preferredNames)
            {
                PropertyInfo p = t.GetProperty(memberName, flags);
                if (p != null && p.CanRead)
                {
                    object candidate = SafeGet(() => p.GetValue(value));
                    string text = CoerceToString(candidate);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }

                FieldInfo f = t.GetField(memberName, flags);
                if (f != null)
                {
                    object candidate = SafeGet(() => f.GetValue(value));
                    string text = CoerceToString(candidate);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }

            return null;
        }

        private static string DescribeMemberShape(object value)
        {
            if (value == null) return "<null>";
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            List<string> chunks = new();
            Type t = value.GetType();

            foreach (PropertyInfo p in t.GetProperties(flags))
            {
                if (!p.CanRead) continue;
                object pv = SafeGet(() => p.GetValue(value));
                chunks.Add($"{p.Name}:{DescribeValue(pv)}");
            }
            foreach (FieldInfo f in t.GetFields(flags))
            {
                object fv = SafeGet(() => f.GetValue(value));
                chunks.Add($"{f.Name}:{DescribeValue(fv)}");
            }

            if (chunks.Count == 0) return $"{t.Name}{{<no readable members>}}";
            return $"{t.Name}{{{string.Join(", ", chunks)}}}";
        }

        private static string DescribeValue(object value)
        {
            if (value == null) return "<null>";
            if (value is string s) return $"\"{s}\"";
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is decimal) return value.ToString();
            if (value is IEnumerable e && value is not string)
            {
                int count = 0;
                foreach (object _ in e)
                {
                    count++;
                    if (count >= 5) break;
                }
                return $"{t.Name}[~{count}+]";
            }
            return t.Name;
        }

        private static List<(string crewId, string symbol)> ExtractFromUpdateTrainCrews(object message)
        {
            List<(string crewId, string symbol)> pairs = new();
            object trainCrews = GetMemberValue(message, "TrainCrews");
            if (trainCrews == null) return pairs;

            if (trainCrews is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    string crewId = CoerceToString(entry.Key);
                    string symbol = ExtractSymbolFromCrewState(entry.Value);
                    if (!string.IsNullOrWhiteSpace(crewId) && !string.IsNullOrWhiteSpace(symbol))
                    {
                        pairs.Add((crewId, symbol));
                    }
                }
                return pairs;
            }

            if (trainCrews is IEnumerable enumerable && trainCrews is not string)
            {
                foreach (object item in enumerable)
                {
                    if (item == null) continue;
                    object key = GetMemberValue(item, "Key");
                    object value = GetMemberValue(item, "Value");
                    string crewId = CoerceToString(key);
                    string symbol = ExtractSymbolFromCrewState(value);
                    if (!string.IsNullOrWhiteSpace(crewId) && !string.IsNullOrWhiteSpace(symbol))
                    {
                        pairs.Add((crewId, symbol));
                    }
                }
            }

            return pairs;
        }

        private static string ExtractSymbolFromCrewState(object crewState)
        {
            if (crewState == null) return null;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            string[] preferredMembers =
            {
                "TimetableSymbol", "timetableSymbol",
                "TrainSymbol", "trainSymbol",
                "Symbol", "symbol",
                "TimetableTrain", "timetableTrain"
            };

            foreach (string name in preferredMembers)
            {
                object member = GetMemberValue(crewState, name);
                string parsed = CoerceToString(member);
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            }

            foreach (PropertyInfo p in crewState.GetType().GetProperties(flags))
            {
                if (!p.CanRead) continue;
                string n = p.Name.ToLowerInvariant();
                if (!IsSymbolName(n)) continue;
                object member = SafeGet(() => p.GetValue(crewState));
                string parsed = CoerceToString(member);
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            }

            foreach (FieldInfo f in crewState.GetType().GetFields(flags))
            {
                string n = f.Name.ToLowerInvariant();
                if (!IsSymbolName(n)) continue;
                object member = SafeGet(() => f.GetValue(crewState));
                string parsed = CoerceToString(member);
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            }

            return null;
        }

        private static object GetMemberValue(object obj, string memberName)
        {
            if (obj == null || string.IsNullOrEmpty(memberName)) return null;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo p = obj.GetType().GetProperty(memberName, flags);
            if (p != null && p.CanRead)
            {
                return SafeGet(() => p.GetValue(obj));
            }

            FieldInfo f = obj.GetType().GetField(memberName, flags);
            if (f != null)
            {
                return SafeGet(() => f.GetValue(obj));
            }

            return null;
        }

        private static bool ContainsIgnoreCase(string value, string segment)
        {
            return value?.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCrewName(string lowerName)
        {
            return !string.IsNullOrEmpty(lowerName) && lowerName.Contains("crew");
        }

        private static bool IsSymbolName(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName)) return false;
            if (lowerName.Contains("crew")) return false;
            if (lowerName.Contains("symbol")) return true;
            if (lowerName.Contains("timetable")) return true;
            return false;
        }
    }
}
