using System;
using System.Collections;
using System.Reflection;
using Helpers;
using HarmonyLib;
using Model;
using Track;
using UI;
using UI.Common;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    internal class RouteWaypointLocationPicker : MonoBehaviour
    {
        private ManagedWaypoint _waypoint;
        private Action<ManagedWaypoint> _onWaypointChange;
        private Coroutine _coroutine;
        private bool _locationWasPicked;
        private MethodInfo _hitLocationMethod;

        private static RouteWaypointLocationPicker _shared;
        public static RouteWaypointLocationPicker Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = UnityEngine.Object.FindObjectOfType<RouteWaypointLocationPicker>();
                }

                return _shared;
            }
        }

        public void StartPickingLocation(ManagedWaypoint waypoint, Action<ManagedWaypoint> onWaypointChange)
        {
            _waypoint = waypoint;
            _onWaypointChange = onWaypointChange;

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
            }

            _coroutine = StartCoroutine(Loop());
            Toast.Present("Click a track location to set this route waypoint", ToastPosition.Bottom);
            GameInput.RegisterEscapeHandler(GameInput.EscapeHandler.Transient, DidEscape);
        }

        private bool DidEscape()
        {
            Cancel("Cancelled route waypoint location selection");
            return true;
        }

        private void Cancel(string message)
        {
            if (_coroutine != null)
            {
                Toast.Present(message, ToastPosition.Bottom);
                StopLoop();
            }
        }

        private void StopLoop()
        {
            _waypoint = null;
            _onWaypointChange = null;
            _locationWasPicked = false;

            if (_coroutine != null)
            {
                StopCoroutine(_coroutine);
                _coroutine = null;
            }

            GameInput.UnregisterEscapeHandler(GameInput.EscapeHandler.Transient);
        }

        private IEnumerator Loop()
        {
            while (!_locationWasPicked)
            {
                bool hasSnappedLocation = TryGetSnappedLocation(out Location snappedLocation);
                if (Input.GetMouseButtonDown(0))
                {
                    if (hasSnappedLocation)
                    {
                        _waypoint.OverwriteLocation(snappedLocation);
                        try
                        {
                            Loader.ServiceProvider.GetService<RefuelService>()?.CheckNearbyFuelLoaders(_waypoint);
                        }
                        catch (Exception e)
                        {
                            Loader.LogError($"[Routes] Failed to evaluate nearby fuel/water loaders for route waypoint: {e.Message}");
                        }
                        _onWaypointChange?.Invoke(_waypoint);
                        _locationWasPicked = true;
                        Toast.Present("Route waypoint location set", ToastPosition.Bottom);
                    }
                    else
                    {
                        Toast.Present("No valid track location under cursor", ToastPosition.Bottom);
                    }
                }

                yield return null;
            }

            StopLoop();
        }

        private bool TryGetSnappedLocation(out Location location)
        {
            location = default;

            AutoEngineerDestinationPicker picker = UnityEngine.Object.FindObjectOfType<AutoEngineerDestinationPicker>();
            if (picker != null)
            {
                _hitLocationMethod ??= AccessTools.Method(typeof(AutoEngineerDestinationPicker), "HitLocation");
                if (_hitLocationMethod != null)
                {
                    object hit = null;
                    try { hit = _hitLocationMethod.Invoke(picker, null); } catch { } // Reflection; ignore invoke failure
                    if (TryExtractLocationFromHit(hit, out location))
                    {
                        return true;
                    }
                }
            }

            Camera camera = null;
            if (!MainCameraHelper.TryGetIfNeeded(ref camera))
            {
                return false;
            }

            Location? raw = Graph.Shared.LocationFromMouse(camera);
            if (raw.HasValue)
            {
                location = raw.Value;
                return true;
            }

            return false;
        }

        private static bool TryExtractLocationFromHit(object hit, out Location location)
        {
            location = default;
            if (hit == null) return false;

            Type hitType = hit.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (PropertyInfo prop in hitType.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                if (!IsLocationLike(prop.PropertyType)) continue;
                object value = null;
                try { value = prop.GetValue(hit); } catch { } // Reflection; ignore missing/inaccessible member
                if (TryUnboxLocation(value, out location)) return true;
            }

            foreach (FieldInfo field in hitType.GetFields(flags))
            {
                if (!IsLocationLike(field.FieldType)) continue;
                object value = null;
                try { value = field.GetValue(hit); } catch { } // Reflection; ignore missing/inaccessible member
                if (TryUnboxLocation(value, out location)) return true;
            }

            return false;
        }

        private static bool IsLocationLike(Type type)
        {
            if (type == typeof(Location)) return true;
            Type inner = Nullable.GetUnderlyingType(type);
            return inner == typeof(Location);
        }

        private static bool TryUnboxLocation(object value, out Location location)
        {
            if (value is Location l)
            {
                location = l;
                return true;
            }

            location = default;
            return false;
        }
    }
}
