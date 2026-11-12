// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PowerDisplay.UnitTests;

/// <summary>
/// Covers the persisted shape of the "lower contrast below minimum brightness" setting:
/// off by default, absent from older settings files, stored under a snake_case key.
/// </summary>
[TestClass]
public class LowerContrastBelowMinimumSettingsTests
{
    [TestMethod]
    public void Defaults_Disabled()
    {
        Assert.IsFalse(new PowerDisplayProperties().LowerContrastBelowMinimumBrightness);
    }

    [TestMethod]
    public void Deserialize_LegacyJsonMissingField_DefaultsToDisabled()
    {
        const string legacyJson = """
        {
            "follow_builtin_display_brightness": true
        }
        """;

        var properties = JsonSerializer.Deserialize<PowerDisplayProperties>(legacyJson);

        Assert.IsNotNull(properties);
        Assert.IsTrue(properties.FollowBuiltInDisplayBrightness);
        Assert.IsFalse(properties.LowerContrastBelowMinimumBrightness);
    }

    [TestMethod]
    public void RoundTrip_UsesSnakeCaseKeyAndPreservesValue()
    {
        var original = new PowerDisplayProperties { LowerContrastBelowMinimumBrightness = true };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<PowerDisplayProperties>(json);

        StringAssert.Contains(json, "\"lower_contrast_below_minimum_brightness\":true");
        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.LowerContrastBelowMinimumBrightness);
    }
}
