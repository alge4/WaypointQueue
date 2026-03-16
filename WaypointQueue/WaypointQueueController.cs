using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using Game.Messages;
using Game.State;
using Model;
using Model.Ops;
using Model.Ops.Timetable;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.UI;
using WaypointQueue.UUM;
using static WaypointQueue.ModSaveManager;

namespace WaypointQueue
{
    public class WaypointQueueController : MonoBehaviour
    {
        public static event Action<string> LocoWaypointStateDidUpdate;
        public static event Action<ManagedWaypoint> WaypointDidUpdate;

        private Coroutine _coroutine;

        public readonly Dictionary<string, LocoWaypointState> WaypointStateMap = [];

        private WaypointResolver _waypointResolver;
        private RefuelService _refuelService;
        private ICarService _carService;
        private AutoEngineerService _autoEngineerService;
        private readonly HashSet<string> _noCrewWarningKeys = [];
        private readonly Dictionary<string, string> _knownCrewSymbolsByCrewId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _appliedRouteToQueueByLocoId = new(StringComparer.OrdinalIgnoreCase);

        private static WaypointQueueController _shared;

        public static WaypointQueueController Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = FindObjectOfType<WaypointQueueController>();
                }
                return _shared;
            }
        }

        public static float WaypointTickInterval = 0.5f;

        private void Awake()
        {
            Messenger.Default.Register<MapWillUnloadEvent>(this, OnMapWillUnload);
            _waypointResolver = Loader.ServiceProvider.GetService<WaypointResolver>();
            _refuelService = Loader.ServiceProvider.GetService<RefuelService>();
            _carService = Loader.ServiceProvider.GetService<ICarService>();
            _autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
        }

        private void OnMapWillUnload(MapWillUnloadEvent @event)
        {
            if (_coroutine != null)
            {
                Loader.LogDebug($"OnMapWillUnload stopping coroutine in WaypointQueueController OnMapWillUnload");
                StopCoroutine(_coroutine);
                _coroutine = null;
                WaypointStateMap.Clear();
            }
        }

        private IEnumerator Ticker()
        {
            WaitForSeconds t = new(WaypointTickInterval);
            while (true)
            {
                yield return t;
                Tick();
            }
        }

        private void Tick()
        {
            try
            {
                DoQueueTickUpdate();
            }
            catch (Exception e)
            {
                Loader.LogError(e.ToString());
                ErrorModalController.Shared.ShowTickErrorModal(e.Message);
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        private void DoQueueTickUpdate()
        {
            HandleLoopingRoutes();

            List<LocoWaypointState> listForRemoval = [];

            // Route/symbol events can mutate WaypointStateMap during a tick; iterate a snapshot to avoid
            // "Collection was modified" exceptions while preserving current tick behavior.
            foreach (LocoWaypointState entry in WaypointStateMap.Values.ToList())
            {
                List<ManagedWaypoint> waypointList = entry.Waypoints;
                AutoEngineerOrdersHelper ordersHelper = _autoEngineerService.GetOrdersHelper(entry.Locomotive);

                if (!_autoEngineerService.IsInWaypointMode(ordersHelper))
                {
                    entry.UnresolvedWaypoint = null;
                    continue;
                }

                if (!IsReadyToResolve(entry, ordersHelper))
                {
                    continue;
                }

                // Resolve waypoint order
                /**
                 * Unresolved waypoint should be the latest waypoint that this coroutine sent to the loco.
                 * We can't simply always resolve the first waypoint because we wouldn't know whether the loco has 
                 * actually performed the AE move order yet.
                 */
                if (entry.UnresolvedWaypoint != null)
                {
                    if (!_waypointResolver.HandleUnresolvedWaypoint(entry.UnresolvedWaypoint, ordersHelper, WaypointTickInterval))
                    {
                        continue;
                    }
                    else
                    {
                        //Loader.Log($"Finish resolving waypoint {entry.UnresolvedWaypoint.Id} {entry.UnresolvedWaypoint.Location} for {entry.UnresolvedWaypoint.Locomotive.Ident}");
                        RemoveWaypoint(entry.UnresolvedWaypoint);
                    }
                }

                // Send next waypoint
                if (waypointList.Count > 0)
                {
                    ManagedWaypoint nextWaypoint = waypointList.First();
                    entry.UnresolvedWaypoint = nextWaypoint;
                    SendToWaypointFromQueue(nextWaypoint, ordersHelper);
                }

                // Mark if empty
                if (waypointList.Count == 0)
                {
                    var (assignedRouteId, loop) = RouteAssignmentRegistry.Get(entry.Locomotive.id);
                    if (loop && !string.IsNullOrEmpty(assignedRouteId))
                    {
                        var assignedRoute = RouteRegistry.GetById(assignedRouteId);
                        if (assignedRoute != null)
                        {
                            Loader.Log($"Loco {entry.Locomotive.Ident}: queue empty & looping enabled → reassigning route '{assignedRoute.Name}' (apply mode).");
                            // Re-apply the saved route, but we already know the waypoint list is currently empty so just append
                            AddWaypointsFromRoute(entry.Locomotive, assignedRoute, append: true);

                            // After reassigning, continue to next loco without marking for removal
                            continue;
                        }
                    }

                    // Queue is depleted; allow symbol-selected routing to apply again.
                    _appliedRouteToQueueByLocoId.Remove(entry.Locomotive.id);
                    string depletedCrewId = entry.Locomotive?.trainCrewId;
                    if (!string.IsNullOrWhiteSpace(depletedCrewId) &&
                        _knownCrewSymbolsByCrewId.TryGetValue(depletedCrewId, out string currentSymbol) &&
                        !string.IsNullOrWhiteSpace(currentSymbol))
                    {
                        TryAutoAssignWatchedRouteForCrew(depletedCrewId, currentSymbol);
                    }
                    listForRemoval.Add(entry);
                }
            }

            // Update list of states
            foreach (var entry in listForRemoval)
            {
                WaypointStateMap.Remove(entry.LocomotiveId);
            }
        }

        private Dictionary<string, string> BuildCrewSymbolLookup()
        {
            Dictionary<string, string> lookup = new(_knownCrewSymbolsByCrewId, StringComparer.OrdinalIgnoreCase);
            var timetable = TimetableController.Shared?.Current;
            if (timetable?.Trains == null || timetable.Trains.Count == 0)
            {
                return lookup;
            }

            foreach (var train in timetable.Trains.Values)
            {
                if (train == null || string.IsNullOrWhiteSpace(train.Name)) continue;

                foreach (string crewId in ExtractCrewIdsFromTrain(train))
                {
                    if (!string.IsNullOrWhiteSpace(crewId) && !lookup.ContainsKey(crewId))
                    {
                        lookup[crewId] = train.Name;
                    }
                }
            }

            return lookup;
        }

        private static List<string> ExtractCrewIdsFromTrain(Timetable.Train train)
        {
            HashSet<string> crewIds = [];
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (PropertyInfo p in train.GetType().GetProperties(flags))
            {
                if (!p.Name.ToLowerInvariant().Contains("crew")) continue;
                object value = null;
                try { value = p.GetValue(train); } catch { } // Reflection; ignore missing/inaccessible member
                AddCrewIdsFromUnknownValue(value, crewIds);
            }

            foreach (FieldInfo f in train.GetType().GetFields(flags))
            {
                if (!f.Name.ToLowerInvariant().Contains("crew")) continue;
                object value = null;
                try { value = f.GetValue(train); } catch { } // Reflection; ignore missing/inaccessible member
                AddCrewIdsFromUnknownValue(value, crewIds);
            }

            return [.. crewIds];
        }

        private static void AddCrewIdsFromUnknownValue(object value, HashSet<string> crewIds)
        {
            if (value == null) return;

            if (value is string s)
            {
                if (!string.IsNullOrWhiteSpace(s)) crewIds.Add(s);
                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    AddCrewIdsFromUnknownValue(item, crewIds);
                }
                return;
            }

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in new[] { "id", "Id", "crewId", "CrewId", "trainCrewId", "TrainCrewId" })
            {
                PropertyInfo prop = value.GetType().GetProperty(name, flags);
                if (prop?.PropertyType == typeof(string))
                {
                    string v = prop.GetValue(value) as string;
                    if (!string.IsNullOrWhiteSpace(v)) crewIds.Add(v);
                }

                FieldInfo field = value.GetType().GetField(name, flags);
                if (field?.FieldType == typeof(string))
                {
                    string v = field.GetValue(value) as string;
                    if (!string.IsNullOrWhiteSpace(v)) crewIds.Add(v);
                }
            }
        }

        private static IEnumerable<Car> EnumerateKnownLocomotives()
        {
            Dictionary<string, Car> locosById = [];

            // Include locomotives that already have waypoint state
            foreach (LocoWaypointState state in Shared.WaypointStateMap.Values)
            {
                if (state?.Locomotive is BaseLocomotive)
                {
                    locosById[state.Locomotive.id] = state.Locomotive;
                }
            }

            // Include locomotives with route assignment
            foreach (RouteAssignment assignment in RouteAssignmentRegistry.All())
            {
                if (assignment == null || string.IsNullOrEmpty(assignment.LocoId)) continue;
                if (TrainController.Shared.TryGetCarForId(assignment.LocoId, out Car loco) && loco is BaseLocomotive)
                {
                    locosById[loco.id] = loco;
                }
            }

            // Try to enumerate all car ids from OpsController lookup (fallback-friendly via reflection)
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo lookupField = OpsController.Shared.GetType().GetField("_carPositionLookup", flags);
                if (lookupField?.GetValue(OpsController.Shared) is IDictionary dict)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Key is string carId && TrainController.Shared.TryGetCarForId(carId, out Car car))
                        {
                            if (car is BaseLocomotive)
                            {
                                locosById[car.id] = car;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best effort only; we still have assigned/active locomotives.
            }

            // Broad fallback: reflect over TrainController internals for any Car collections.
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                HashSet<Car> discoveredCars = [];
                object shared = TrainController.Shared;
                if (shared != null)
                {
                    foreach (FieldInfo field in shared.GetType().GetFields(flags))
                    {
                        object value = null;
                        try { value = field.GetValue(shared); } catch { } // Reflection; ignore on get
                        CollectCarsFromUnknownValue(value, discoveredCars, depth: 0);
                    }

                    foreach (PropertyInfo prop in shared.GetType().GetProperties(flags))
                    {
                        if (!prop.CanRead) continue;
                        object value = null;
                        try { value = prop.GetValue(shared); } catch { } // Reflection; ignore on get
                        CollectCarsFromUnknownValue(value, discoveredCars, depth: 0);
                    }
                }

                foreach (Car car in discoveredCars)
                {
                    if (car is BaseLocomotive)
                    {
                        locosById[car.id] = car;
                    }
                }
            }
            catch
            {
                // Best effort fallback only.
            }

            return locosById.Values;
        }

        private static void CollectCarsFromUnknownValue(object value, HashSet<Car> cars, int depth)
        {
            if (value == null || depth > 3)
            {
                return;
            }

            if (value is Car car)
            {
                cars.Add(car);
                return;
            }

            if (value is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    CollectCarsFromUnknownValue(entry.Key, cars, depth + 1);
                    CollectCarsFromUnknownValue(entry.Value, cars, depth + 1);
                }
                return;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (object item in enumerable)
                {
                    CollectCarsFromUnknownValue(item, cars, depth + 1);
                }
                return;
            }

            // Handle wrappers that expose a "Car" or "car" member.
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in new[] { "Car", "car", "Value", "value" })
            {
                PropertyInfo prop = value.GetType().GetProperty(name, flags);
                if (prop?.CanRead == true)
                {
                    object inner = null;
                    try { inner = prop.GetValue(value); } catch { } // Reflection; ignore on get
                    CollectCarsFromUnknownValue(inner, cars, depth + 1);
                }

                FieldInfo field = value.GetType().GetField(name, flags);
                if (field != null)
                {
                    object inner = null;
                    try { inner = field.GetValue(value); } catch { } // Reflection; ignore on get
                    CollectCarsFromUnknownValue(inner, cars, depth + 1);
                }
            }
        }

        private bool IsReadyToResolve(LocoWaypointState entry, AutoEngineerOrdersHelper ordersHelper)
        {
            try
            {
                bool readyToResolve = !_autoEngineerService.HasActiveWaypoint(ordersHelper) && _autoEngineerService.IsInWaypointMode(ordersHelper);
                return readyToResolve || NeedsForceResolve(entry);
            }
            catch (Exception e)
            {
                throw new QueueTickException($"Exception while checking if waypoint for {entry.Locomotive.Ident} is ready to resolve: {e.Message}", e);
            }
        }

        private bool NeedsForceResolve(LocoWaypointState entry)
        {
            if (entry.UnresolvedWaypoint == null)
            {
                return false;
            }

            bool atEndOfTrack = _autoEngineerService.AtEndOfTrack(entry.Locomotive as BaseLocomotive);
            bool isNearWaypoint = _autoEngineerService.IsNearWaypoint(entry.UnresolvedWaypoint);
            bool isTrainStopped = _waypointResolver.IsTrainStopped(entry.UnresolvedWaypoint);
            bool needsEndOfTrackResolve = atEndOfTrack && isNearWaypoint && isTrainStopped;

            bool needsAlreadyCoupledResolve = IsUnresolvedWaypointAlreadyCoupled(entry.UnresolvedWaypoint);

            return needsEndOfTrackResolve || needsAlreadyCoupledResolve;
        }

        private bool IsUnresolvedWaypointAlreadyCoupled(ManagedWaypoint wp)
        {
            if (wp.IsCoupling && wp.TryResolveCoupleToCar(out Car car))
            {
                List<Car> consist = [.. wp.Locomotive.EnumerateCoupled()];
                if (consist.Contains(car))
                {
                    return true;
                }
            }
            return false;
        }

        public void AddWaypoint(Car loco, Location location, string coupleToCarId, bool isReplacing, bool isInsertingNext)
        {
            Location clampedLocation = location.Clamped();
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            string actionName = "add";
            if (isReplacing) actionName = "replace";
            if (isInsertingNext) actionName = "insert next";
            Loader.Log($"Trying to {actionName} waypoint for loco {loco.Ident} to {clampedLocation} with {couplingLogSegment}");

            LocoWaypointState entry = GetOrAddLocoWaypointState(loco);

            ManagedWaypoint waypoint = new ManagedWaypoint(loco, clampedLocation, coupleToCarId);
            _refuelService.CheckNearbyFuelLoaders(waypoint);

            if (isReplacing && entry.Waypoints.Count > 0)
            {
                if (entry.Waypoints[0].Id == entry.UnresolvedWaypoint.Id)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                    entry.UnresolvedWaypoint = waypoint;
                }
                entry.Waypoints[0] = waypoint;
                RefreshCurrentWaypoint(loco, _autoEngineerService.GetOrdersHelper(loco));
            }
            else if (isInsertingNext && entry.Waypoints.Count > 0)
            {
                entry.Waypoints.Insert(1, waypoint);
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }
            Loader.Log($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");

            OnWaypointWasAdded(loco.id);
        }

        public LocoWaypointState GetOrAddLocoWaypointState(Car loco)
        {
            if (WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState entry))
            {
                Loader.LogDebug($"Found existing waypoint list for {loco.Ident}");
            }
            else
            {
                Loader.LogDebug($"No existing waypoint list found for {loco.Ident}");
                entry = new LocoWaypointState(loco);
                WaypointStateMap.Add(loco.id, entry);
            }
            return entry;
        }

        public void AddWaypointsFromRoute(Car loco, RouteDefinition route, bool append)
        {
            if (loco == null || route == null) return;

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            if (!append)
            {
                ClearWaypointState(loco.id);
            }

            Loader.LogDebug($"Adding waypoints from {route.Name} to {loco.Ident} queue");

            var entry = GetOrAddLocoWaypointState(loco);

            int validWaypointsAdded = 0;
            int failedWaypoints = 0;
            foreach (var rw in route.Waypoints)
            {
                if (rw.TryCopyForRoute(out ManagedWaypoint copy, loco: loco))
                {
                    entry.Waypoints.Add(copy);
                    validWaypointsAdded++;
                }
                else
                {
                    failedWaypoints++;
                    string failureMessage = $"Waypoint '{(string.IsNullOrWhiteSpace(rw?.Name) ? rw?.Id : rw.Name)}' has no valid location. Set a location in Routes -> Set by click.";
                    rw.Errors ??= [];
                    rw.Errors.RemoveAll(e => e != null && e.ErrorType == "Route copy");
                    rw.Errors.Add(new WaypointError("Route copy", failureMessage));
                    Loader.LogError($"[Routes] {failureMessage} Route='{route.Name}' Loco='{loco.Ident}'");
                }
            }
            Loader.Log($"Added {validWaypointsAdded} waypoints for {loco.Ident} from route {route.Name}");
            if (failedWaypoints > 0)
            {
                string modalMessage = $"{failedWaypoints} waypoint(s) in route '{route.Name}' were skipped because location is missing or invalid.";
                ErrorModalController.Shared?.ShowRouteCopyErrorModal(route.Name, loco.Ident.ToString(), modalMessage);
            }
            OnWaypointWasAdded(loco.id);
        }

        public void AssignRouteToLoco(Car loco, RouteDefinition route, bool replaceQueue = true, bool warnIfNoCrew = true)
        {
            if (loco == null || route == null) return;

            var (_, prevLoop) = RouteAssignmentRegistry.Get(loco.id);
            RouteAssignmentRegistry.Set(loco.id, route.Id, prevLoop);
            TryRegisterCrewSymbolForRoute(loco, route, warnIfNoCrew);
            AddWaypointsFromRoute(loco, route, append: !replaceQueue);
            MarkRouteAppliedToQueue(loco.id, route.Id);
        }

        public bool TryRegisterCrewSymbolForRoute(Car loco, RouteDefinition route, bool warnIfNoCrew = true)
        {
            if (loco == null || route == null) return false;

            string crewId = loco.trainCrewId;
            if (string.IsNullOrEmpty(crewId))
            {
                if (warnIfNoCrew && ShouldWarnNoCrew(loco.id, route.Id))
                {
                    Toast.Present($"Waypoint Queue: {loco.Ident} has no crew; route assigned without crew symbol.");
                }
                return false;
            }

            string symbol = string.IsNullOrWhiteSpace(route.TrainSymbol)
                ? BuildCrewSymbolForRoute(route)
                : route.TrainSymbol.Trim();
            StateManager.ApplyLocal(new RequestSetTrainCrewTimetableSymbol(crewId, symbol));
            NoteCrewSymbolChanged(crewId, symbol);
            Loader.Log($"[RouteAssign] Set crew symbol '{symbol}' for {loco.Ident}");
            return true;
        }

        public void NoteCrewSymbolChanged(string crewId, string symbol)
        {
            if (string.IsNullOrWhiteSpace(crewId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(symbol))
            {
                _knownCrewSymbolsByCrewId.Remove(crewId);
            }
            else
            {
                string normalizedSymbol = symbol.Trim();
                if (_knownCrewSymbolsByCrewId.TryGetValue(crewId, out string previousSymbol) &&
                    string.Equals(previousSymbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _knownCrewSymbolsByCrewId[crewId] = normalizedSymbol;
                TryAutoAssignWatchedRouteForCrew(crewId, normalizedSymbol);
            }
        }

        private void TryAutoAssignWatchedRouteForCrew(string crewId, string symbol)
        {
            if (string.IsNullOrWhiteSpace(crewId) || string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            RouteDefinition matchedRoute = RouteRegistry.Routes
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r?.TrainSymbol) &&
                    string.Equals(r.TrainSymbol.Trim(), symbol, StringComparison.OrdinalIgnoreCase));
            if (matchedRoute == null)
            {
                Loader.LogDebug($"[RouteAssign] No route configured for symbol '{symbol}' (crewId='{crewId}').");
                return;
            }

            List<Car> crewLocos = EnumerateKnownLocomotives()
                .Where(l => l != null && string.Equals(l.trainCrewId, crewId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Car selected = TrainController.Shared?.SelectedLocomotive;
            if (selected != null &&
                string.Equals(selected.trainCrewId, crewId, StringComparison.OrdinalIgnoreCase) &&
                crewLocos.All(l => l.id != selected.id))
            {
                crewLocos.Add(selected);
            }

            if (crewLocos.Count == 0)
            {
                Loader.LogDebug($"[RouteAssign] No locomotive found for crewId='{crewId}' symbol='{symbol}'.");
                return;
            }

            foreach (Car loco in crewLocos)
            {
                var (assignedRouteId, _) = RouteAssignmentRegistry.Get(loco.id);

                if (string.Equals(assignedRouteId, matchedRoute.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (HasAppliedRouteToQueue(loco.id, matchedRoute.Id))
                    {
                        Loader.LogDebug($"[RouteAssign] Route '{matchedRoute.Name}' already applied for {loco.Ident}; no copy needed.");
                        continue;
                    }

                    Loader.Log($"[RouteAssign] Loading symbol-selected route '{matchedRoute.Name}' to {loco.Ident} after symbol '{symbol}' update.");
                    AddWaypointsFromRoute(loco, matchedRoute, append: false);
                    MarkRouteAppliedToQueue(loco.id, matchedRoute.Id);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(assignedRouteId))
                {
                    Loader.Log($"[RouteAssign] Symbol '{symbol}' selected route '{matchedRoute.Name}' for {loco.Ident}.");
                }
                else
                {
                    RouteDefinition previousRoute = RouteRegistry.GetById(assignedRouteId);
                    string previousRouteName = previousRoute?.Name ?? assignedRouteId;
                    Loader.Log($"[RouteAssign] Symbol '{symbol}' switching {loco.Ident} from route '{previousRouteName}' to '{matchedRoute.Name}'.");
                }

                _appliedRouteToQueueByLocoId.Remove(loco.id);
                AssignRouteToLoco(loco, matchedRoute, replaceQueue: true, warnIfNoCrew: false);
            }
        }

        private bool HasAppliedRouteToQueue(string locoId, string routeId)
        {
            if (string.IsNullOrWhiteSpace(locoId) || string.IsNullOrWhiteSpace(routeId))
            {
                return false;
            }

            return _appliedRouteToQueueByLocoId.TryGetValue(locoId, out string appliedRouteId) &&
                string.Equals(appliedRouteId, routeId, StringComparison.OrdinalIgnoreCase);
        }

        private void MarkRouteAppliedToQueue(string locoId, string routeId)
        {
            if (string.IsNullOrWhiteSpace(locoId) || string.IsNullOrWhiteSpace(routeId))
            {
                return;
            }

            _appliedRouteToQueueByLocoId[locoId] = routeId;
        }

        public string BuildCrewSymbolForRoute(RouteDefinition route)
        {
            string prefix = (Loader.Settings.RouteCrewSymbolPrefix ?? "WQ").Trim();
            if (string.IsNullOrEmpty(prefix))
            {
                prefix = "WQ";
            }

            string routeId = (route?.Id ?? "").Replace("-", "");
            if (routeId.Length > 6)
            {
                routeId = routeId.Substring(0, 6);
            }
            if (string.IsNullOrEmpty(routeId))
            {
                routeId = "route";
            }

            string safeName = new string((route?.Name ?? "route")
                .Where(char.IsLetterOrDigit)
                .Take(10)
                .ToArray());
            if (string.IsNullOrEmpty(safeName))
            {
                safeName = "route";
            }

            return $"{prefix}-{routeId}-{safeName}";
        }

        private bool ShouldWarnNoCrew(string locoId, string routeId)
        {
            string key = $"{locoId}:{routeId}";
            if (_noCrewWarningKeys.Contains(key))
            {
                return false;
            }

            _noCrewWarningKeys.Add(key);
            return true;
        }

        private void HandleLoopingRoutes()
        {
            try
            {
                TryHandleLoopingRoutes();
            }
            catch (Exception e)
            {
                throw new QueueTickException("Failed to handle looping routes", e);
            }
        }

        private void TryHandleLoopingRoutes()
        {
            List<RouteAssignment> assignmentList = RouteAssignmentRegistry
                .All()
                .Where(ra => ra.Loop && !WaypointStateMap.ContainsKey(ra.LocoId))
                .ToList();

            foreach (var ra in assignmentList)
            {
                if (TrainController.Shared.TryGetCarForId(ra.LocoId, out Car loco))
                {
                    RouteDefinition route = RouteRegistry.GetById(ra.RouteId);
                    if (route == null)
                    {
                        Loader.LogError($"Failed to find route matching id {ra.RouteId}");
                        continue;
                    }
                    AddWaypointsFromRoute(loco, route, true);
                }
                else
                {
                    Loader.LogError($"Failed to find loco matching id {ra.LocoId}");
                    continue;
                }
            }
        }

        private void OnWaypointWasAdded(string locoId)
        {
            LocoWaypointStateDidUpdate.Invoke(locoId);

            if (_coroutine == null)
            {
                Loader.Log($"Starting waypoint coroutine after adding waypoint");
                _coroutine = StartCoroutine(Ticker());
            }
        }

        public void ClearWaypointState(string locoId)
        {
            if (WaypointStateMap.TryGetValue(locoId, out LocoWaypointState entry))
            {
                if (entry.UnresolvedWaypoint != null)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                }

                WaypointStateMap.Remove(locoId);
                _appliedRouteToQueueByLocoId.Remove(locoId);
                Loader.Log($"Removed waypoint state entry for {entry.Locomotive}");
                _autoEngineerService.CancelActiveOrders(entry.Locomotive);
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in ClearWaypointState");
                LocoWaypointStateDidUpdate.Invoke(locoId);
            }
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            Loader.Log($"Removing waypoint {waypoint.Id} {waypoint.Location} for {waypoint.Locomotive.Ident}");

            if (WaypointStateMap.TryGetValue(waypoint.Locomotive.id, out LocoWaypointState entry))
            {
                _waypointResolver.CleanupBeforeRemovingWaypoint(waypoint);

                string waypointId = waypoint.Id;
                int indexOfWaypoint = entry.Waypoints.FindIndex(w => w.Id == waypointId);

                if (indexOfWaypoint >= 0)
                {
                    entry.Waypoints.RemoveAt(indexOfWaypoint);
                    Loader.Log($"Removed waypoint {waypointId}");
                }
                else
                {
                    Loader.LogError($"Failed to remove waypoint {waypointId}");
                }

                if (entry.UnresolvedWaypoint.Id == waypointId)
                {
                    Loader.LogDebug($"Removed waypoint was unresolved. Resetting unresolved to null");
                    entry.UnresolvedWaypoint = null;
                    _autoEngineerService.CancelActiveOrders(entry.Locomotive);
                }

                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in RemoveWaypoint");
                LocoWaypointStateDidUpdate.Invoke(entry.LocomotiveId);
            }
        }

        public void RemoveCurrentWaypoint(Car locomotive)
        {
            if (WaypointStateMap.TryGetValue(locomotive.id, out LocoWaypointState state) && state.Waypoints.Count > 0)
            {
                RemoveWaypoint(state.Waypoints[0]);
            }
        }

        public void UpdateWaypoint(ManagedWaypoint updatedWaypoint)
        {
            Loader.LogDebug($"Updating waypoint");
            if (WaypointStateMap.TryGetValue(updatedWaypoint.Locomotive.id, out LocoWaypointState state) && state.Waypoints != null)
            {
                int index = state.Waypoints.FindIndex(w => w.Id == updatedWaypoint.Id);
                if (index >= 0)
                {
                    state.Waypoints[index] = updatedWaypoint;

                    if (updatedWaypoint.Id == state.UnresolvedWaypoint.Id)
                    {
                        Loader.LogDebug($"Updated unresolved waypoint");
                        state.UnresolvedWaypoint = updatedWaypoint;
                    }

                    Loader.LogDebug($"Invoking WaypointDidUpdate in UpdateWaypoint");
                    WaypointDidUpdate.Invoke(updatedWaypoint);
                }
            }
        }

        public void ReorderWaypoint(ManagedWaypoint waypoint, int newIndex)
        {
            if (WaypointStateMap.TryGetValue(waypoint.Locomotive.id, out LocoWaypointState state) && state.Waypoints != null)
            {
                int oldIndex = state.Waypoints.IndexOf(waypoint);
                if (oldIndex < 0) return;

                state.Waypoints.RemoveAt(oldIndex);

                if (newIndex > oldIndex)
                {
                    newIndex--; // the actual index could have shifted due to the removal
                }

                state.Waypoints.Insert(newIndex, waypoint);

                if (state.Waypoints[0].Id != state.UnresolvedWaypoint.Id)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(state.UnresolvedWaypoint);
                    Loader.LogDebug($"Resetting unresolved waypoint after reordering waypoint list");
                    state.UnresolvedWaypoint = waypoint;
                    SendToWaypointFromQueue(waypoint, _autoEngineerService.GetOrdersHelper(waypoint.Locomotive));
                }

                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in ReorderWaypoint");
                LocoWaypointStateDidUpdate.Invoke(waypoint.LocomotiveId);
            }
        }

        public void RerouteCurrentWaypoint(Car locomotive)
        {
            AutoEngineerOrdersHelper ordersHelper = _autoEngineerService.GetOrdersHelper(locomotive);
            if (_autoEngineerService.HasActiveWaypoint(ordersHelper))
            {
                StateManager.ApplyLocal(new AutoEngineerWaypointRerouteRequest(locomotive.id));
            }
            else
            {
                RefreshCurrentWaypoint(locomotive, ordersHelper);
            }
        }

        public void RefreshCurrentWaypoint(Car locomotive, AutoEngineerOrdersHelper ordersHelper)
        {
            if (WaypointStateMap.TryGetValue(locomotive.id, out LocoWaypointState state) && state.Waypoints.Count > 0)
            {
                Loader.Log($"Resetting current waypoint as active");
                ManagedWaypoint nextWaypoint = state.Waypoints.First();
                state.UnresolvedWaypoint = nextWaypoint;
                SendToWaypointFromQueue(nextWaypoint, ordersHelper);
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in RemoveCurrentWaypoint");
                LocoWaypointStateDidUpdate.Invoke(locomotive.id);
            }
        }

        public bool HasWaypointState(string locoId)
        {
            return WaypointStateMap.ContainsKey(locoId);
        }

        public List<ManagedWaypoint> GetWaypointList(Car loco)
        {
            WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState state);
            return state?.Waypoints ?? [];
        }

        public bool TryGetActiveWaypointFor(Car loco, out ManagedWaypoint waypoint)
        {
            waypoint = null;

            if (loco == null)
                return false;

            // Find the LocoWaypointState for this locomotive
            if (!WaypointStateMap.TryGetValue(loco.id, out LocoWaypointState state))
                return false;

            // The "active" waypoint is the unresolved one if present, otherwise the first in the list
            var active = state.UnresolvedWaypoint ?? state.Waypoints.FirstOrDefault();
            if (active == null)
                return false;

            waypoint = active;
            return true;
        }

        private void SendToWaypointFromQueue(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Sending next waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            _waypointResolver.ApplyTimetableSymbolIfRequested(waypoint);
            waypoint.StatusLabel = "Running to waypoint";
            UpdateWaypoint(waypoint);
            _autoEngineerService.SendToWaypoint(ordersHelper, waypoint.Location, waypoint.CoupleToCarId);
        }

        internal void LoadWaypointSaveState(WaypointSaveState saveState)
        {
            WaypointStateMap.Clear();

            List<string> unresolvedLocomotiveIds = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedLocationsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedCoupleToCarIdsByLocoId = [];
            Dictionary<string, List<ManagedWaypoint>> unresolvedDestinationIdsByLocoId = [];

            Loader.LogDebug($"Starting LoadWaypointSaveState");
            WaypointStateMap.Clear();
            foreach (var entry in saveState.WaypointStates)
            {
                Loader.LogDebug($"Loading waypoint state for {entry.LocomotiveId}");

                if (!entry.TryResolveLocomotive(out Car loco))
                {
                    unresolvedLocomotiveIds.Add(entry.LocomotiveId);
                    break;
                }

                List<ManagedWaypoint> validWaypoints = [];
                foreach (var waypoint in entry.Waypoints)
                {
                    Loader.LogDebug($"Loading waypoint {waypoint.Id}");
                    if (!waypoint.TryResolveLocomotive(out loco) && !unresolvedLocomotiveIds.Contains(waypoint.LocomotiveId))
                    {
                        unresolvedLocomotiveIds.Add(waypoint.LocomotiveId);
                        break;
                    }

                    if (!waypoint.TryResolveLocation(out Location loc))
                    {
                        if (unresolvedLocationsByLocoId.TryGetValue(loco.id, out List<ManagedWaypoint> waypoints))
                        {
                            waypoints.Add(waypoint);
                        }
                        else
                        {
                            unresolvedLocationsByLocoId.Add(waypoint.LocomotiveId, [waypoint]);
                        }
                        break;
                    }

                    if (!String.IsNullOrEmpty(waypoint.CoupleToCarId) && !waypoint.TryResolveCoupleToCar(out Car coupleToCar))
                    {
                        if (unresolvedCoupleToCarIdsByLocoId.TryGetValue(loco.id, out List<ManagedWaypoint> waypoints))
                        {
                            waypoints.Add(waypoint);
                        }
                        else
                        {
                            unresolvedCoupleToCarIdsByLocoId.Add(waypoint.LocomotiveId, [waypoint]);
                        }
                        break;
                    }
                    if (waypoint.WillUncoupleByDestination && !waypoint.CheckValidUncoupleDestinationId())
                    {
                        if (unresolvedDestinationIdsByLocoId.TryGetValue(loco.id, out List<ManagedWaypoint> waypoints))
                        {
                            waypoints.Add(waypoint);
                        }
                        else
                        {
                            unresolvedDestinationIdsByLocoId.Add(waypoint.LocomotiveId, [waypoint]);
                        }
                        break;
                    }
                    waypoint.TryResolveCouplingSearchText(out Car _);
                    waypoint.TryResolveUncouplingSearchText(out Car _);

                    validWaypoints.Add(waypoint);
                }
                entry.Waypoints = validWaypoints;

                if (entry.UnresolvedWaypoint != null)
                {
                    Loader.LogDebug($"Loading unresolved waypoint {entry.UnresolvedWaypoint.Id}");
                    if (!entry.UnresolvedWaypoint.IsValidWithLoco())
                    {
                        Loader.LogError($"Failed to hydrate unresolved waypoint {entry.UnresolvedWaypoint?.Id}");
                    }
                }

                WaypointStateMap.Add(entry.LocomotiveId, entry);
            }

            string unresolvedLocoIdsLogLine = "";
            if (unresolvedLocomotiveIds.Count > 0)
            {
                unresolvedLocoIdsLogLine = $"{unresolvedLocomotiveIds.Count} locomotive car ids could not be found.\n";
                Loader.LogError($"Failed to resolve {unresolvedLocomotiveIds.Count} locomotive car ids. {String.Join(",", unresolvedLocomotiveIds.Select(s => s))}");
            }

            string unresolvedLocationsByLocoLogLines = "";
            if (unresolvedLocationsByLocoId.Count > 0)
            {
                foreach (var item in unresolvedLocationsByLocoId.Values)
                {
                    string locoId = item[0].LocomotiveId;
                    string locoIdent = item[0].Locomotive.Ident.ToString();
                    unresolvedLocationsByLocoLogLines += $"{item.Count} waypoints for {locoIdent} failed to load track locations.\n";
                    Loader.LogError($"Failed to resolve track locations on {item.Count} waypoints for locomotive car id {locoId} with ident {locoIdent}. {String.Join(",", item.Select(w => $"[{w.Id}]"))}");
                }
            }

            string unresolvedCoupleToCarsByLocoLogLines = "";
            if (unresolvedCoupleToCarIdsByLocoId.Count > 0)
            {
                foreach (var item in unresolvedCoupleToCarIdsByLocoId.Values)
                {
                    string locoId = item[0].LocomotiveId;
                    string locoIdent = item[0].Locomotive.Ident.ToString();
                    unresolvedCoupleToCarsByLocoLogLines += $"{item.Count} waypoints for {locoIdent} failed to load couple to car ids.\n";
                    Loader.LogError($"Failed to resolve couple to car ids on {item.Count} waypoints for locomotive car id {locoId} with ident {locoIdent}. {String.Join(",", item.Select(w => $"[{w.Id}]"))}");
                }
            }

            string unresolvedDestinationIdsLogLines = "";
            if (unresolvedDestinationIdsByLocoId.Count > 0)
            {
                foreach (var item in unresolvedDestinationIdsByLocoId.Values)
                {
                    string locoId = item[0].LocomotiveId;
                    string locoIdent = item[0].Locomotive.Ident.ToString();
                    unresolvedDestinationIdsLogLines += $"{item.Count} waypoints for {locoIdent} failed to load uncoupling by destination ids.\n";
                    Loader.LogError($"Failed to resolve uncoupling by destination ids on {item.Count} waypoints for locomotive car id {locoId} with ident {locoIdent}. {String.Join(",", item.Select(w => $"[{w.Id}]"))}");
                }
            }

            if (unresolvedLocomotiveIds.Count > 0 || unresolvedLocationsByLocoId.Count > 0 || unresolvedCoupleToCarIdsByLocoId.Count > 0 || unresolvedDestinationIdsByLocoId.Count > 0)
            {
                ModalAlertController.PresentOkay("Failed to load waypoints", $"Waypoint Queue ran into an issue while trying to load waypoint data." +
                    $"\n\n{unresolvedLocoIdsLogLine}{unresolvedLocationsByLocoLogLines}{unresolvedCoupleToCarsByLocoLogLines}{unresolvedDestinationIdsLogLines}" +
                    $"\nSometimes this may happen if any rolling stock or track mods were modified or removed in this save, or if you are loading an earlier version of a save with a mismatched waypoints.json file." +
                    $"\n\nWaypoint Queue should still work normally with this save game, though some waypoints may be missing.");
            }

            if (TrainController.Shared.SelectedLocomotive)
            {
                Loader.LogDebug($"Invoking LocoWaypointStateDidUpdate in LoadWaypointSaveState");
                LocoWaypointStateDidUpdate.Invoke(TrainController.Shared.SelectedLocomotive.id);
            }

            if (_coroutine == null)
            {
                Loader.LogDebug($"Starting waypoint coroutine in LoadWaypointSaveState");
                _coroutine = StartCoroutine(Ticker());
            }
            else
            {
                Loader.LogDebug($"Restarting waypoint coroutine in LoadWaypointSaveState");
                StopCoroutine(_coroutine);
                _coroutine = StartCoroutine(Ticker());
            }
        }
    }

}
