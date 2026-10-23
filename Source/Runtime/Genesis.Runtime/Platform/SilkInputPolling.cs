using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Silk.NET.Input;
using Silk.NET.Windowing;

namespace Genesis.Runtime.Platform
{
    /// <summary>
    /// Silk.NET's input context polls every one of its sixteen gamepad and sixteen joystick slots on
    /// every window update, connected or not; on a machine with controllers attached that was about half
    /// the game thread's own time in the complex benchmark scene. This takes that update over: mice and
    /// connected controllers are updated every frame as before (no added input latency), and the empty
    /// slots are looked at twice a second for a controller plugged in. When Silk's internals are not as
    /// expected nothing is taken over; GENESIS_SILK_INPUT=every keeps Silk's own polling.
    /// </summary>
    internal sealed class SilkInputPolling
    {
        /// <summary>How often an empty controller slot is looked at, in seconds.</summary>
        public const double EmptySlotSeconds = 0.5;

        private readonly List<(Func<bool> Connected, Action Update)> _controllers = new();
        private readonly List<Action> _always = new();
        private long _nextEmptyCheck;

        private SilkInputPolling() { }

        public static SilkInputPolling TryTakeOver(IView window, IInputContext input)
        {
            if (window == null || input == null || Environment.GetEnvironmentVariable("GENESIS_SILK_INPUT") == "every") return null;
            try
            {
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type type = input.GetType();
                if (type.GetField("_gamepads", Instance)?.GetValue(input) is not Array gamepads
                    || type.GetField("_joysticks", Instance)?.GetValue(input) is not Array joysticks
                    || type.GetField("_mice", Instance)?.GetValue(input) is not Array mice)
                    return null;
                var polling = new SilkInputPolling();
                foreach (object device in gamepads) polling.AddController(device);
                foreach (object device in joysticks) polling.AddController(device);
                foreach (object mouse in mice)
                    if (mouse != null && UpdateOf(mouse) is { } updateMouse) polling._always.Add(updateMouse);
                // The context's own subscription goes only once every device can be updated here, and
                // only where the window holds it as expected (else both would poll): its ProcessEvents
                // in the window's ProcessEvents (Silk's own windows), or its Update(double) in Update.
                int miceCount = 0;
                foreach (object mouse in mice) if (mouse != null) miceCount++;
                if (polling._controllers.Count != gamepads.Length + joysticks.Length || polling._always.Count != miceCount) return null;
                if (type.GetMethod("ProcessEvents", Instance, null, Type.EmptyTypes, null) is { } process
                    && Delegate.CreateDelegate(typeof(Action), input, process) is Action processEvents
                    && Subscribed(window, "ProcessEvents", processEvents))
                {
                    Unsubscribe(window, "ProcessEvents", processEvents);
                    return Subscribed(window, "ProcessEvents", processEvents) ? null : polling;
                }
                if (type.BaseType?.GetMethod("Update", Instance, null, [typeof(double)], null) is { } update)
                {
                    var own = (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), input, update);
                    if (!Subscribed(window, "Update", own)) return null;
                    window.Update -= own;
                    return Subscribed(window, "Update", own) ? null : polling;
                }
                return null;
            }
            catch (Exception error) when (error is ArgumentException or MemberAccessException or TargetException or InvalidCastException)
            {
                return null;
            }
        }

        // One of the window's events, by its backing field somewhere up its class chain.
        private static FieldInfo EventField(IView window, string eventName)
        {
            for (Type type = window.GetType(); type != null; type = type.BaseType)
                if (type.GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } field
                    && typeof(Delegate).IsAssignableFrom(field.FieldType))
                    return field;
            return null;
        }

        private static bool Subscribed(IView window, string eventName, Delegate handler) =>
            EventField(window, eventName)?.GetValue(window) is Delegate list && Array.IndexOf(list.GetInvocationList(), handler) >= 0;

        private static void Unsubscribe(IView window, string eventName, Delegate handler)
        {
            if (EventField(window, eventName) is { } field && field.GetValue(window) is Delegate list)
                field.SetValue(window, Delegate.Remove(list, handler));
        }

        private static Action UpdateOf(object device) =>
            device.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) is { } method
                ? (Action)Delegate.CreateDelegate(typeof(Action), device, method)
                : null;

        private void AddController(object device)
        {
            if (device is not IInputDevice input || UpdateOf(device) is not { } update) return;
            _controllers.Add((() => input.IsConnected, update));
        }

        /// <summary>Updates the devices, as Silk's own update did; the window's update calls it first.</summary>
        public void Update()
        {
            foreach (Action mouse in _always) mouse();
            long now = Stopwatch.GetTimestamp();
            bool checkEmpty = now >= _nextEmptyCheck;
            if (checkEmpty) _nextEmptyCheck = now + (long)(EmptySlotSeconds * Stopwatch.Frequency);
            foreach ((Func<bool> connected, Action update) in _controllers)
                if (checkEmpty || connected()) update();
        }
    }
}
