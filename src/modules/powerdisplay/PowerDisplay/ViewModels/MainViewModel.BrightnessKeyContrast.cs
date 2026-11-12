// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ManagedCommon;
using Microsoft.UI.Dispatching;
using PowerDisplay.Common.Drivers.WMI;
using PowerDisplay.Helpers;

namespace PowerDisplay.ViewModels;

/// <summary>
/// MainViewModel: "lower contrast below minimum brightness". Extends "follow built-in display
/// brightness" past 0%: once the built-in brightness is at 0%, each further brightness-down key
/// press lowers the external monitors' contrast by <see cref="ContrastKeyStep"/>, down to each
/// monitor's contrast range minimum. Brightness-up presses then first raise contrast back to the
/// range maximum while the brightness is held at 0%; only after that does brightness rise again.
/// </summary>
/// <remarks>
/// At 0% Windows clamps the value and reports nothing, so the press itself is observed through
/// raw input (<see cref="BrightnessKeyListener"/>). A raw brightness-down press with no built-in
/// change within <see cref="ClampedPressWindow"/> is a press at the floor. Going up, Windows does
/// raise the built-in brightness (0% to 10%); that change is consumed here and the panel is set
/// back to 0% through WMI. Without a WMI panel (desktop PCs) brightness can't be held, so the
/// built-in value keeps rising while contrast recovers and externals catch up when it's done.
/// </remarks>
public partial class MainViewModel
{
    private const int ContrastKeyStep = 10;

    // Windows updates the brightness setting about 1 ms after the raw HID report; auto-repeat is
    // about 100 ms. A press is "at the floor" if no built-in change lands within this window of it.
    private static readonly TimeSpan ClampedPressWindow = TimeSpan.FromMilliseconds(80);

    private bool _lowerContrastBelowMinimumBrightness;

    // True after this feature lowered contrast and until every target is back at its range maximum.
    private bool _contrastZoneActive;
    private int _previousBuiltInBrightness = -1;
    private long _lastBuiltInChangeTicks;
    private long _pendingDownPressTicks;
    private DispatcherQueueTimer? _clampedPressTimer;

    /// <summary>
    /// Raised when <see cref="BrightnessKeyContrastActive"/> may have changed, so the window can
    /// register or unregister the raw input listener.
    /// </summary>
    internal event EventHandler? BrightnessKeyContrastActiveChanged;

    /// <summary>
    /// Gets a value indicating whether the feature is active. It needs "follow built-in display
    /// brightness": below 0% there is nothing to follow, and the externals are already at 0%.
    /// </summary>
    internal bool BrightnessKeyContrastActive => FollowBuiltInDisplayBrightness && _lowerContrastBelowMinimumBrightness;

    private void SetLowerContrastBelowMinimumBrightness(bool value)
    {
        _lowerContrastBelowMinimumBrightness = value;
        if (!BrightnessKeyContrastActive)
        {
            ResetContrastZone();
        }

        BrightnessKeyContrastActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsContrastKeyTarget(MonitorViewModel monitor) =>
        IsFollowTarget(monitor) && monitor.SupportsContrast && monitor.ShowContrast;

    /// <summary>
    /// Called by the window for every raw brightness key report (window thread = dispatcher thread).
    /// Only brightness-down needs raw input: brightness-up at the floor still changes the built-in
    /// value, which <see cref="TryHandleContrastZone"/> handles.
    /// </summary>
    internal void OnBrightnessKeyPressed(BrightnessKey key)
    {
        if (key != BrightnessKey.Down || !BrightnessKeyContrastActive)
        {
            return;
        }

        _pendingDownPressTicks = _clock.UtcNow.Ticks;
        if (_clampedPressTimer is null)
        {
            _clampedPressTimer = _dispatcherQueue.CreateTimer();
            _clampedPressTimer.IsRepeating = false;
            _clampedPressTimer.Interval = ClampedPressWindow;
            _clampedPressTimer.Tick += (_, _) => EvaluateDownPress();
        }

        _clampedPressTimer.Stop();
        _clampedPressTimer.Start();
    }

    private void EvaluateDownPress()
    {
        if (!BrightnessKeyContrastActive || IsScanning || IsBulkApplyInProgress())
        {
            return;
        }

        // This press moved the built-in brightness (for example 10% to 0%): not a press at the floor.
        if (Math.Abs(Interlocked.Read(ref _lastBuiltInChangeTicks) - _pendingDownPressTicks) <= ClampedPressWindow.Ticks)
        {
            return;
        }

        if (_builtInBrightnessWatcher.LastBrightness != 0)
        {
            return;
        }

        StepContrast(-ContrastKeyStep);
    }

    /// <summary>
    /// Records every built-in brightness change before any early return of the follow handler,
    /// so the press classification and the "went up" check see the full history.
    /// </summary>
    /// <returns>The previous built-in value (-1 if unknown).</returns>
    private int NoteBuiltInBrightnessChange(int value)
    {
        int previous = _previousBuiltInBrightness;
        _previousBuiltInBrightness = value;
        Interlocked.Exchange(ref _lastBuiltInChangeTicks, _clock.UtcNow.Ticks);
        return previous;
    }

    /// <summary>
    /// While contrast is lowered, consumes built-in brightness changes instead of following them.
    /// </summary>
    /// <returns>True if the change was consumed and must not be mirrored to external monitors.</returns>
    private bool TryHandleContrastZone(int value, int previous)
    {
        if (!BrightnessKeyContrastActive || !_contrastZoneActive)
        {
            return false;
        }

        // Externals are already at 0%; this is also the echo of holding the panel at 0%.
        if (value == 0)
        {
            return true;
        }

        if (previous < 0 || value <= previous)
        {
            // Not a brightness-up step (e.g. lowered while being held); keep the externals at 0%.
            return true;
        }

        StepContrast(ContrastKeyStep);
        _ = HoldBuiltInBrightnessAtZeroAsync(value);
        return true;
    }

    /// <summary>
    /// Moves every target's contrast by <paramref name="delta"/> within its range and updates the
    /// zone state. The contrast setter debounces the DDC/CI write like a slider drag.
    /// </summary>
    private void StepContrast(int delta)
    {
        var targets = Monitors.Where(IsContrastKeyTarget).ToList();
        if (targets.Count == 0)
        {
            _contrastZoneActive = false;
            return;
        }

        bool allAtMaximum = true;
        foreach (var vm in targets)
        {
            int next = Math.Clamp(vm.Contrast + delta, vm.ContrastMinimum, vm.ContrastMaximum);
            if (next != vm.Contrast)
            {
                vm.Contrast = next;
            }

            allAtMaximum &= next >= vm.ContrastMaximum;
        }

        bool wasActive = _contrastZoneActive;
        _contrastZoneActive = !allAtMaximum;
        Logger.LogInfo($"[BrightnessKeyContrast] Contrast {(delta < 0 ? "down" : "up")} on {targets.Count} monitor(s): {string.Join(", ", targets.Select(t => t.Contrast))}");

        if (wasActive && !_contrastZoneActive)
        {
            Logger.LogInfo("[BrightnessKeyContrast] Contrast back at range maximum; brightness follows again");
        }
    }

    /// <summary>
    /// Puts the built-in brightness back to 0% after Windows raised it, so the next brightness-up
    /// press is again a step from 0%. If there is no WMI panel the value can't be held: when the
    /// zone has just ended, mirror the current value so externals don't lag behind.
    /// </summary>
    private async Task HoldBuiltInBrightnessAtZeroAsync(int raisedValue)
    {
        bool held = await WmiController.SetBrightnessOnAllPanelsAsync(0);
        if (held)
        {
            return;
        }

        Logger.LogInfo($"[BrightnessKeyContrast] No WMI panel to hold at 0%; built-in brightness is {raisedValue}%");
        if (!_contrastZoneActive)
        {
            _dispatcherQueue.TryEnqueue(() => OnBuiltInBrightnessChanged(this, _builtInBrightnessWatcher.LastBrightness));
        }
    }

    private bool IsBulkApplyInProgress() =>
        Volatile.Read(ref _bulkApplyDepth) > 0 || _clock.UtcNow.Ticks < Interlocked.Read(ref _suppressFollowUntilTicks);

    /// <summary>
    /// Forgets the lowered-contrast state (display change, profile/restore apply, feature off).
    /// Contrast values are left as they are.
    /// </summary>
    private void ResetContrastZone()
    {
        _clampedPressTimer?.Stop();
        _contrastZoneActive = false;
    }
}
