// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PowerDisplay.Models;

namespace PowerDisplay.UnitTests;

/// <summary>
/// Covers the per-monitor contrast range: persisted defaults, legacy settings files, setter
/// clamping, and the shared normalization used by the flyout.
/// </summary>
[TestClass]
public class ContrastRangeSettingsTests
{
    [TestMethod]
    public void Defaults_FullRange()
    {
        var monitor = new MonitorInfo();

        Assert.AreEqual(0, monitor.ContrastMin);
        Assert.AreEqual(100, monitor.ContrastMax);
    }

    [TestMethod]
    public void Deserialize_LegacyJsonWithoutRange_KeepsFullRange()
    {
        const string legacyJson = """
        {
            "id": "DDC_DELA246_1",
            "enableContrast": true
        }
        """;

        var monitor = JsonSerializer.Deserialize<MonitorInfo>(legacyJson);

        Assert.IsNotNull(monitor);
        Assert.AreEqual(0, monitor.ContrastMin);
        Assert.AreEqual(100, monitor.ContrastMax);
    }

    [TestMethod]
    public void RoundTrip_PreservesRangeUnderCamelCaseKeys()
    {
        var original = new MonitorInfo { ContrastMin = 10, ContrastMax = 70 };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<MonitorInfo>(json);

        StringAssert.Contains(json, "\"contrastMin\":10");
        StringAssert.Contains(json, "\"contrastMax\":70");
        Assert.IsNotNull(restored);
        Assert.AreEqual(10, restored.ContrastMin);
        Assert.AreEqual(70, restored.ContrastMax);
    }

    [TestMethod]
    public void Setters_ClampToPercentage()
    {
        var monitor = new MonitorInfo { ContrastMin = -5, ContrastMax = 150 };

        Assert.AreEqual(0, monitor.ContrastMin);
        Assert.AreEqual(100, monitor.ContrastMax);
    }

    [TestMethod]
    [DataRow(10, 70, 10, 70)]
    [DataRow(-10, 120, 0, 100)]
    [DataRow(70, 10, 0, 100)]
    [DataRow(50, 50, 0, 100)]
    public void Normalize_ReturnsUsableRange(int min, int max, int expectedMin, int expectedMax)
    {
        var (actualMin, actualMax) = ContinuousValueRange.Normalize(min, max);

        Assert.AreEqual(expectedMin, actualMin);
        Assert.AreEqual(expectedMax, actualMax);
    }
}
