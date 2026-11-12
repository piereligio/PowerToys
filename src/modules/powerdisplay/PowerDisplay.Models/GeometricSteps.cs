// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace PowerDisplay.Models;

/// <summary>
/// Key-press steps that shrink geometrically towards the bottom of a range (capped at
/// <see cref="DefaultMaxStep"/> near the top, down to 1 near the minimum). Used by "lower
/// contrast below minimum brightness", where low contrast values need finer control. Up and down walk the same ladder, so a press up exactly
/// undoes a press down.
/// </summary>
public static class GeometricSteps
{
    /// <summary>
    /// Each level keeps this fraction of the distance from the range minimum.
    /// </summary>
    public const double DefaultRatio = 0.8;

    /// <summary>
    /// Largest allowed step, so the top of the range is not crossed in a few big jumps.
    /// With the defaults a 0-75 range gives 75, 65, 55, 45, 36, 29, 23, 18, 14, 11, 9, 7, 6, 5,
    /// 4, 3, 2, 1, 0 (18 presses).
    /// </summary>
    public const int DefaultMaxStep = 10;

    /// <summary>
    /// Returns the distinct levels from <paramref name="maximum"/> down to <paramref name="minimum"/>.
    /// Every step is at least 1 and at most <paramref name="maxStep"/>.
    /// </summary>
    public static IReadOnlyList<int> Levels(int minimum, int maximum, double ratio = DefaultRatio, int maxStep = DefaultMaxStep)
    {
        if (ratio <= 0 || ratio >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Ratio must be between 0 and 1 (exclusive).");
        }

        maxStep = Math.Max(1, maxStep);
        var levels = new List<int> { maximum };
        int level = maximum;
        while (level > minimum)
        {
            int next = minimum + (int)Math.Round((level - minimum) * ratio, MidpointRounding.AwayFromZero);
            next = Math.Max(next, level - maxStep);
            if (next >= level)
            {
                next = level - 1;
            }

            levels.Add(next);
            level = next;
        }

        return levels;
    }

    /// <summary>
    /// Returns the next level below <paramref name="current"/> (or the minimum).
    /// </summary>
    public static int StepDown(int current, int minimum, int maximum)
    {
        foreach (int level in Levels(minimum, maximum))
        {
            if (level < current)
            {
                return level;
            }
        }

        return Math.Min(current, Math.Max(minimum, maximum));
    }

    /// <summary>
    /// Returns the next level above <paramref name="current"/> (or the maximum).
    /// </summary>
    public static int StepUp(int current, int minimum, int maximum)
    {
        var levels = Levels(minimum, maximum);
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            if (levels[i] > current)
            {
                return levels[i];
            }
        }

        return Math.Max(current, maximum);
    }
}
