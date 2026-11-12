// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Models;

namespace PowerDisplay.UnitTests;

/// <summary>
/// Covers the contrast ladder used by "lower contrast below minimum brightness".
/// </summary>
[TestClass]
public class GeometricStepsTests
{
    private static readonly int[] ExpectedZeroTo75 = { 75, 53, 37, 26, 18, 13, 9, 6, 4, 3, 2, 1, 0 };

    [TestMethod]
    public void Levels_ZeroTo75_ShrinkTowardsMinimum()
    {
        CollectionAssert.AreEqual(ExpectedZeroTo75, GeometricSteps.Levels(0, 75).ToArray());
    }

    [TestMethod]
    public void Levels_OffsetMinimum_EndsAtMinimumAndStrictlyDecreases()
    {
        var levels = GeometricSteps.Levels(20, 75);

        Assert.AreEqual(75, levels[0]);
        Assert.AreEqual(20, levels[^1]);
        for (int i = 1; i < levels.Count; i++)
        {
            Assert.IsTrue(levels[i] < levels[i - 1]);
        }
    }

    [TestMethod]
    public void StepDown_FromOffLadderValue_GoesToNextLowerLevel()
    {
        Assert.AreEqual(53, GeometricSteps.StepDown(65, 0, 75));
        Assert.AreEqual(1, GeometricSteps.StepDown(2, 0, 75));
        Assert.AreEqual(0, GeometricSteps.StepDown(0, 0, 75));
    }

    [TestMethod]
    public void StepUp_RetracesLadderAndStopsAtMaximum()
    {
        Assert.AreEqual(1, GeometricSteps.StepUp(0, 0, 75));
        Assert.AreEqual(75, GeometricSteps.StepUp(53, 0, 75));
        Assert.AreEqual(75, GeometricSteps.StepUp(75, 0, 75));
    }

    [TestMethod]
    public void StepDownThenUp_ReturnsToSameLevel()
    {
        foreach (int level in GeometricSteps.Levels(0, 75).Skip(1))
        {
            Assert.AreEqual(level, GeometricSteps.StepDown(GeometricSteps.StepUp(level, 0, 75), 0, 75));
        }
    }
}
