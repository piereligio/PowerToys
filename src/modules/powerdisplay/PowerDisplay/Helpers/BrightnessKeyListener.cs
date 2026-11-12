// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ManagedCommon;

namespace PowerDisplay.Helpers;

/// <summary>
/// Hardware brightness key reported by <see cref="BrightnessKeyListener"/>.
/// </summary>
internal enum BrightnessKey
{
    Up,
    Down,
}

/// <summary>
/// Observes the HID consumer-control "Display Brightness Increment / Decrement" usages
/// (0x0C/0x6F and 0x0C/0x70) through Raw Input. Laptop brightness keys produce no virtual-key
/// code, and when the built-in brightness is already at 0% or 100% Windows does not report any
/// brightness change either, so raw input is the only way to see a press at the limits.
/// The input is only observed: Windows still handles the keys (brightness, OSD) as usual.
/// </summary>
/// <remarks>
/// Raw input is delivered to <see cref="HandleMessage"/> as WM_INPUT on the window passed to the
/// constructor (registered with RIDEV_INPUTSINK, so it works while that window is hidden).
/// Registration is per process and per usage, so enabling it does not affect other input.
/// </remarks>
internal sealed partial class BrightnessKeyListener : IDisposable
{
    private const uint WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidiPreparsedData = 0x20000005;
    private const uint RimTypeHid = 2;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const ushort ConsumerUsagePage = 0x0C;
    private const ushort ConsumerControlUsage = 0x01;
    private const ushort BrightnessIncrementUsage = 0x6F;
    private const ushort BrightnessDecrementUsage = 0x70;
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private const int MaxUsages = 32;

    // Sanity bound for a single WM_INPUT payload; consumer-control reports are a few bytes.
    private const uint MaxRawInputSize = 4096;

    private readonly nint _hwnd;

    // Preparsed HID descriptor per raw input device handle (null when it can't be read).
    private readonly Dictionary<nint, byte[]?> _preparsedData = new();
    private bool _registered;
    private bool _disposed;

    public BrightnessKeyListener(nint hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>
    /// Raised on the window thread for every brightness key report (including auto-repeat).
    /// </summary>
    public event EventHandler<BrightnessKey>? KeyPressed;

    /// <summary>
    /// Registers or unregisters the raw input subscription. Only registered while a feature needs it.
    /// </summary>
    public unsafe void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == _registered || _hwnd == 0)
        {
            return;
        }

        var device = new RawInputDevice
        {
            UsagePage = ConsumerUsagePage,
            Usage = ConsumerControlUsage,
            Flags = enabled ? RidevInputSink : RidevRemove,
            Target = enabled ? _hwnd : 0, // RIDEV_REMOVE requires a null target.
        };

        if (RegisterRawInputDevices(ref device, 1, (uint)sizeof(RawInputDevice)))
        {
            _registered = enabled;
            if (!enabled)
            {
                _preparsedData.Clear();
            }

            Logger.LogInfo($"[BrightnessKeyListener] Raw input for brightness keys {(enabled ? "registered" : "unregistered")}");
        }
        else
        {
            Logger.LogWarning($"[BrightnessKeyListener] RegisterRawInputDevices failed, error={Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// Inspects a window message. Never swallows it: WM_INPUT must still reach DefWindowProc.
    /// </summary>
    /// <returns>Always false.</returns>
    public bool HandleMessage(uint uMsg, nint lParam)
    {
        if (uMsg != WmInput || !_registered)
        {
            return false;
        }

        try
        {
            ProcessRawInput(lParam);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[BrightnessKeyListener] Failed to process raw input: {ex.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SetEnabled(false);
        _disposed = true;
    }

    private unsafe void ProcessRawInput(nint hRawInput)
    {
        uint headerSize = (uint)sizeof(RawInputHeader);
        uint size = 0;
        _ = GetRawInputData(hRawInput, RidInput, null, ref size, headerSize);
        if (size < headerSize + 8 || size > MaxRawInputSize)
        {
            return;
        }

        byte* buffer = stackalloc byte[(int)size];
        if (GetRawInputData(hRawInput, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
        {
            return;
        }

        var header = (RawInputHeader*)buffer;
        if (header->Type != RimTypeHid)
        {
            return;
        }

        // RAWHID: DWORD dwSizeHid; DWORD dwCount; BYTE bRawData[dwSizeHid * dwCount].
        byte* hid = buffer + headerSize;
        uint reportSize = *(uint*)hid;
        uint reportCount = *(uint*)(hid + 4);
        byte* reports = hid + 8;
        if (reportSize == 0 || (ulong)reportSize * reportCount > size - headerSize - 8)
        {
            return;
        }

        byte[]? preparsed = GetPreparsedData(header->Device);

        for (uint i = 0; i < reportCount; i++)
        {
            byte* report = reports + (i * reportSize);
            var key = preparsed is not null
                ? ParseWithDescriptor(preparsed, report, reportSize)
                : ParseHeuristically(report, reportSize);

            if (key is BrightnessKey pressed)
            {
                KeyPressed?.Invoke(this, pressed);
            }
        }
    }

    /// <summary>
    /// Reads the pressed consumer usages with the device's HID descriptor (works for any report layout).
    /// </summary>
    private static unsafe BrightnessKey? ParseWithDescriptor(byte[] preparsed, byte* report, uint reportSize)
    {
        ushort* usages = stackalloc ushort[MaxUsages];
        uint usageCount = MaxUsages;

        fixed (byte* preparsedPtr = preparsed)
        {
            int status = HidP_GetUsages(HidpInput, ConsumerUsagePage, 0, usages, ref usageCount, (nint)preparsedPtr, report, reportSize);
            if (status != HidpStatusSuccess)
            {
                // Consumer usages declared as a value array instead of buttons; fall back.
                return ParseHeuristically(report, reportSize);
            }
        }

        for (uint i = 0; i < usageCount; i++)
        {
            switch (usages[i])
            {
                case BrightnessIncrementUsage:
                    return BrightnessKey.Up;
                case BrightnessDecrementUsage:
                    return BrightnessKey.Down;
            }
        }

        return null;
    }

    /// <summary>
    /// Fallback for devices whose descriptor can't be parsed: the common consumer-control layout is
    /// a report ID followed by 16-bit little-endian usage codes (e.g. Surface: 03-6F-00 / 03-70-00).
    /// </summary>
    private static unsafe BrightnessKey? ParseHeuristically(byte* report, uint reportSize)
    {
        for (uint offset = 1; offset + 1 < reportSize; offset += 2)
        {
            ushort usage = (ushort)(report[offset] | (report[offset + 1] << 8));
            switch (usage)
            {
                case BrightnessIncrementUsage:
                    return BrightnessKey.Up;
                case BrightnessDecrementUsage:
                    return BrightnessKey.Down;
            }
        }

        return null;
    }

    private unsafe byte[]? GetPreparsedData(nint device)
    {
        if (_preparsedData.TryGetValue(device, out var cached))
        {
            return cached;
        }

        byte[]? data = null;
        uint size = 0;
        if (GetRawInputDeviceInfo(device, RidiPreparsedData, null, ref size) == 0 && size > 0 && size <= 65536)
        {
            data = new byte[size];
            fixed (byte* ptr = data)
            {
                if (GetRawInputDeviceInfo(device, RidiPreparsedData, ptr, ref size) == uint.MaxValue)
                {
                    data = null;
                }
            }
        }

        _preparsedData[device] = data;
        return data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint WParam;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(ref RawInputDevice pRawInputDevices, uint uiNumDevices, uint cbSize);

    [LibraryImport("user32.dll")]
    private static unsafe partial uint GetRawInputData(nint hRawInput, uint uiCommand, void* pData, ref uint pcbSize, uint cbSizeHeader);

    [LibraryImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static unsafe partial uint GetRawInputDeviceInfo(nint hDevice, uint uiCommand, void* pData, ref uint pcbSize);

    [LibraryImport("hid.dll")]
    private static unsafe partial int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, ushort* usageList, ref uint usageLength, nint preparsedData, byte* report, uint reportLength);
}
