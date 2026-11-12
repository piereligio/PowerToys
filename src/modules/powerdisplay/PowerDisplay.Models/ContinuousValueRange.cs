// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace PowerDisplay.Models;

/// <summary>
/// User-defined limits for a continuous percentage setting (e.g. contrast), shared by the
/// Settings UI and the Power Display flyout so both apply the same validation.
/// </summary>
public static class ContinuousValueRange
{
    public const int AbsoluteMinimum = 0;
    public const int AbsoluteMaximum = 100;

    /// <summary>
    /// Returns a usable [minimum, maximum] pair: both clamped to 0-100 and, when the persisted
    /// pair is inverted or empty (minimum &gt;= maximum), the full 0-100 range so a bad value
    /// can never lock a slider to a single position.
    /// </summary>
    public static (int Minimum, int Maximum) Normalize(int minimum, int maximum)
    {
        minimum = Math.Clamp(minimum, AbsoluteMinimum, AbsoluteMaximum);
        maximum = Math.Clamp(maximum, AbsoluteMinimum, AbsoluteMaximum);
        return minimum < maximum ? (minimum, maximum) : (AbsoluteMinimum, AbsoluteMaximum);
    }
}
