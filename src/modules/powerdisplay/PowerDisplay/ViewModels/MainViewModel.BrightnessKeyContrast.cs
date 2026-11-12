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
using PowerDisplay.Models;

namespace PowerDisplay.ViewModels;

/// <summary>
/// MainViewModel: "lower contrast below minimum brightness". Extends "follow built-in display
/// brightness" past 0%: once the built-in brightness is at 0%, each further brightness-down key
/// press lowers the external monitors' contrast one step of a geometric ladder
/// (<see cref="GeometricSteps"/>: large steps near the top, 1 near the bottom), down to each
/// monitor's contrast range minimum. Brightness-up presses then first raise contrast back to the
/// range maximum while the brightness is held at 0%; only after that does brightness rise again.
/// </summary>
/// <remarks>
/// At 0% Windows clamps the value and reports nothing, so the press itself is observed through
/// raw input (<see cref="BrightnessKeyListener"/>). A raw brightness-down press is a press at the
/// floor when the built-in brightness did not change around it. Going up, Windows does raise the
/// built-in brightness (0% to 10%); that change is consumed here and the brightness is written back
/// to 0% in the power scheme (falling back to WMI).
/// Holding a key auto-repeats about every 100 ms, so contrast steps are rate-limited to
/// <see cref="ContrastStepInterval"/>, and crossing the 0% boundary pauses for
/// <see cref="BoundaryDetent"/> in both directions so one held press doesn't run straight through.
/// </remarks>
public partial class MainViewModel
{
    // Windows updates the brightness setting about 1 ms after the raw HID report. A down press is
    // evaluated this long after it arrives, so a change it caused has been seen by then.
    private static readonly TimeSpan ClampedPressWindow = TimeSpan.FromMilliseconds(80);

    // Minimum time between two contrast steps while a key is held (auto-repeat is ~100 ms).
    private static readonly TimeSpan ContrastStepInterval = TimeSpan.FromMilliseconds(250);

    // Pause at the 0% boundary: no contrast step for this long after brightness reached 0%, and no
    // brightness step for this long after contrast got back to its range maximum.
    private static readonly TimeSpan BoundaryDetent = TimeSpan.FromMilliseconds(400);

    private bool _lowerContrastBelowMinimumBrightness;

    // True after this feature lowered contrast and until every target is back at its range maximum.
    private bool _contrastZoneActive;
    private int _previousBuiltInBrightness = -1;
    private long _lastBuiltInChangeTicks;
    private long _pendingDownPressTicks;
    private long _lastContrastStepTicks;
    private long _zoneExitTicks;
    private bool _holdFailureLogged;
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

        // The built-in brightness changed after, with, or shortly before this press (for example the
        // press that took it from 10% to 0%, or a held key's next repeat): not a press at the floor yet.
        if (Interlocked.Read(ref _lastBuiltInChangeTicks) >= _pendingDownPressTicks - BoundaryDetent.Ticks)
        {
            return;
        }

        if (_builtInBrightnessWatcher.LastBrightness != 0 || IsWithinContrastStepInterval())
        {
            return;
        }

        StepContrast(up: false);
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
        if (!BrightnessKeyContrastActive)
        {
            return false;
        }

        if (!_contrastZoneActive)
        {
            // Just got back to the contrast maximum: a held up key pauses here before brightness rises.
            if (value > 0 && value > previous && _clock.UtcNow.Ticks - _zoneExitTicks < BoundaryDetent.Ticks)
            {
                _ = HoldBuiltInBrightnessAtZeroAsync(value);
                return true;
            }

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

        if (!IsWithinContrastStepInterval())
        {
            StepContrast(up: true);
        }

        _ = HoldBuiltInBrightnessAtZeroAsync(value);
        return true;
    }

    /// <summary>
    /// Moves every target's contrast one ladder step within its range and updates the zone state.
    /// The contrast setter debounces the DDC/CI write like a slider drag.
    /// </summary>
    private void StepContrast(bool up)
    {
        var targets = Monitors.Where(IsContrastKeyTarget).ToList();
        if (targets.Count == 0)
        {
            _contrastZoneActive = false;
            return;
        }

        bool allAtMaximum = true;
        bool anyChanged = false;
        foreach (var vm in targets)
        {
            int next = up
                ? GeometricSteps.StepUp(vm.Contrast, vm.ContrastMinimum, vm.ContrastMaximum)
                : GeometricSteps.StepDown(vm.Contrast, vm.ContrastMinimum, vm.ContrastMaximum);
            next = Math.Clamp(next, vm.ContrastMinimum, vm.ContrastMaximum);
            if (next != vm.Contrast)
            {
                vm.Contrast = next;
                anyChanged = true;
            }

            allAtMaximum &= next >= vm.ContrastMaximum;
        }

        bool wasActive = _contrastZoneActive;
        _contrastZoneActive = !allAtMaximum;
        if (!anyChanged)
        {
            // Already at the range minimum (or maximum): nothing to write or log.
            return;
        }

        _lastContrastStepTicks = _clock.UtcNow.Ticks;
        Logger.LogInfo($"[BrightnessKeyContrast] Contrast {(up ? "up" : "down")} on {targets.Count} monitor(s): {string.Join(", ", targets.Select(t => t.Contrast))}");

        if (wasActive && !_contrastZoneActive)
        {
            _zoneExitTicks = _lastContrastStepTicks;
            Logger.LogInfo("[BrightnessKeyContrast] Contrast back at range maximum; brightness follows again");
        }
    }

    private bool IsWithinContrastStepInterval() =>
        _clock.UtcNow.Ticks - _lastContrastStepTicks < ContrastStepInterval.Ticks;

    /// <summary>
    /// Puts the built-in brightness back to 0% after Windows raised it, so the next brightness-up
    /// press is again a step from 0%. The power-scheme write is what the keys step from (WMI alone
    /// changes the panel but not that value). If neither works the value can't be held: when the
    /// zone has ended, mirror the current value so externals don't lag behind.
    /// </summary>
    private async Task HoldBuiltInBrightnessAtZeroAsync(int raisedValue)
    {
        bool held = await Task.Run(() => PowerSettingsNative.TrySetSchemeBrightness(0));
        if (!held)
        {
            held = await WmiController.SetBrightnessOnAllPanelsAsync(0);
        }

        if (held)
        {
            return;
        }

        if (!_holdFailureLogged)
        {
            _holdFailureLogged = true;
            Logger.LogWarning($"[BrightnessKeyContrast] Could not hold built-in brightness at 0% (now {raisedValue}%)");
        }

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
