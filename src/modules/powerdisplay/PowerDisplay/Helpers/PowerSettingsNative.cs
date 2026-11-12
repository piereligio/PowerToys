// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

namespace PowerDisplay.Helpers;

/// <summary>
/// P/Invoke for the power-setting notifications used by
/// <see cref="DisplayChangeWatcher"/> (console display state) and
/// <see cref="BuiltInBrightnessWatcher"/> (built-in display brightness).
/// </summary>
internal static partial class PowerSettingsNative
{
    /// <summary>
    /// GUID_CONSOLE_DISPLAY_STATE — Windows fires this whenever the console
    /// display's power state changes. Data byte is 0 (off), 1 (on), 2 (dimmed).
    /// Reliable on both S3 and S0ix, and also fires for idle-blank / lid /
    /// screensaver transitions that never enter system sleep.
    /// </summary>
    internal static readonly Guid GuidConsoleDisplayState =
        new("6fe69556-704a-47a0-8f24-c28d936fda47");

    /// <summary>
    /// GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS: the active power scheme's display
    /// brightness (0-100). Windows updates it whenever the user changes the
    /// built-in panel brightness (hardware brightness keys, Quick Settings slider,
    /// Settings app), including while that panel is inactive (e.g. "Second screen
    /// only" projection). Data is a DWORD.
    /// </summary>
    internal static readonly Guid GuidVideoCurrentMonitorBrightness =
        new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    /// <summary>
    /// GUID_VIDEO_SUBGROUP (SUB_VIDEO): the power-scheme subgroup that holds the brightness setting.
    /// </summary>
    internal static readonly Guid GuidVideoSubgroup =
        new("7516b95f-f776-4464-8c53-06167f40cc99");

    internal const uint DisplayStateOff = 0;
    internal const uint DisplayStateOn = 1;
    internal const uint DisplayStateDimmed = 2;

    /// <summary>
    /// DEVICE_NOTIFY_CALLBACK — Recipient is a pointer to
    /// DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS containing a callback delegate.
    /// </summary>
    internal const uint DeviceNotifyCallback = 0x00000002;

    /// <summary>
    /// Power-broadcast type value passed as the <c>type</c> parameter of the
    /// callback when a subscribed power setting changes.
    /// </summary>
    internal const uint PowerSettingChangeNotification = 0x8013;

    /// <summary>
    /// Callback signature for power-setting notifications.
    /// Windows invokes this on an internal worker thread — implementations
    /// must marshal to their own thread before touching UI / VM state.
    /// </summary>
    /// <remarks>
    /// <see cref="Marshal.GetFunctionPointerForDelegate"/> does NOT root the
    /// delegate against GC. Callers must keep the managed delegate alive (e.g.
    /// via <c>GCHandle.Alloc</c>) for the entire lifetime of the registration,
    /// or Windows will eventually invoke a collected delegate and produce an
    /// access violation.
    /// </remarks>
    /// <param name="context">Opaque context supplied at registration.</param>
    /// <param name="type">Notification type (e.g. <see cref="PowerSettingChangeNotification"/>).</param>
    /// <param name="setting">Pointer to a POWERBROADCAST_SETTING.</param>
    /// <returns>Reserved — must return 0.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    /// <summary>
    /// POWERBROADCAST_SETTING — the payload Windows passes to the callback.
    /// PowerSetting (16 bytes) + DataLength (4 bytes) precede a variable-length
    /// Data array whose first byte holds the new display state.
    /// </summary>
    internal const int PowerBroadcastSettingDataOffset = 20;

    /// <summary>
    /// Registers a callback to receive notifications when a specific power
    /// setting changes. Free the registration with
    /// <see cref="PowerSettingUnregisterNotification"/> when no longer needed.
    /// </summary>
    /// <returns>
    /// <c>ERROR_SUCCESS</c> (0) on success; otherwise a Win32 error code
    /// (the function returns the error directly; <c>GetLastError</c> is not used).
    /// </returns>
    [LibraryImport("Powrprof.dll")]
    internal static partial uint PowerSettingRegisterNotification(
        ref Guid settingGuid,
        uint flags,
        ref DeviceNotifySubscribeParameters recipient,
        out IntPtr registrationHandle);

    /// <summary>
    /// Cancels a registration previously returned by
    /// <see cref="PowerSettingRegisterNotification"/>.
    /// </summary>
    /// <returns>
    /// <c>ERROR_SUCCESS</c> (0) on success; otherwise a Win32 error code.
    /// </returns>
    [LibraryImport("Powrprof.dll")]
    internal static partial uint PowerSettingUnregisterNotification(IntPtr registrationHandle);

    /// <summary>
    /// Sets the built-in display brightness the way Windows itself stores it: the active power
    /// scheme's brightness for the current power source, then re-applies the scheme. Unlike
    /// WmiSetBrightness this also updates the value the brightness keys step from, works while
    /// the built-in panel is inactive, and on PCs without a WMI panel. Raises the
    /// <see cref="GuidVideoCurrentMonitorBrightness"/> notification like a key press.
    /// </summary>
    /// <returns>True on success.</returns>
    internal static unsafe bool TrySetSchemeBrightness(int percent)
    {
        uint value = (uint)Math.Clamp(percent, 0, 100);
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr schemePtr) != 0 || schemePtr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            Guid scheme = *(Guid*)schemePtr;

            // ACLineStatus: 0 offline (battery), 1 online, 255 unknown (treated as AC).
            bool onBattery = GetSystemPowerStatus(out SystemPowerStatus status) && status.ACLineStatus == 0;
            uint result = onBattery
                ? PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in GuidVideoSubgroup, in GuidVideoCurrentMonitorBrightness, value)
                : PowerWriteACValueIndex(IntPtr.Zero, in scheme, in GuidVideoSubgroup, in GuidVideoCurrentMonitorBrightness, value);

            return result == 0 && PowerSetActiveScheme(IntPtr.Zero, in scheme) == 0;
        }
        finally
        {
            LocalFree(schemePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("Powrprof.dll")]
    private static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("Powrprof.dll")]
    private static partial uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint acValueIndex);

    [LibraryImport("Powrprof.dll")]
    private static partial uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint dcValueIndex);

    [LibraryImport("Powrprof.dll")]
    private static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr hMem);
}
