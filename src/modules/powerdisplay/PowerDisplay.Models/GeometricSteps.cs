// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace PowerDisplay.Models;

/// <summary>
/// Key-press steps that shrink geometrically towards the bottom of a range: large near the top,
/// down to 1 near the minimum. Used by "lower contrast below minimum brightness", where low
/// contrast values need finer control. Up and down walk the same ladder, so a press up exactly
/// undoes a press down.
/// </summary>
public static class GeometricSteps
{
    /// <summary>
    /// Each level keeps this fraction of the distance from the range minimum. With 0.7 a 0-75
    /// range gives 75, 53, 37, 26, 18, 13, 9, 6, 4, 3, 2, 1, 0 (12 presses).
    /// </summary>
    public const double DefaultRatio = 0.7;

    /// <summary>
    /// Returns the distinct levels from <paramref name="maximum"/> down to <paramref name="minimum"/>.
    /// </summary>
    public static IReadOnlyList<int> Levels(int minimum, int maximum, double ratio = DefaultRatio)
    {
        if (ratio <= 0 || ratio >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Ratio must be between 0 and 1 (exclusive).");
        }

        var levels = new List<int> { maximum };
        if (maximum <= minimum)
        {
            return levels;
        }

        double span = maximum - minimum;
        double fraction = 1;
        while (true)
        {
            fraction *= ratio;
            int level = minimum + (int)Math.Round(span * fraction, MidpointRounding.AwayFromZero);
            if (level <= minimum)
            {
                break;
            }

            if (level < levels[^1])
            {
                levels.Add(level);
            }
        }

        levels.Add(minimum);
        return levels;
    }

    /// <summary>
    /// Returns the next level below <paramref name="current"/> (or the minimum).
    /// </summary>
    public static int StepDown(int current, int minimum, int maximum, double ratio = DefaultRatio)
    {
        foreach (int level in Levels(minimum, maximum, ratio))
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
    public static int StepUp(int current, int minimum, int maximum, double ratio = DefaultRatio)
    {
        var levels = Levels(minimum, maximum, ratio);
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
