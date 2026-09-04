using System;
using Xunit;

#nullable enable

public sealed class LoadOrderCompatibilityTests
{
    [Fact]
    public void Recognizes_only_the_exact_mod_launch_manager_id()
    {
        Assert.True(global::ItsLoading.ItsLoading.IsLoadOrderManaged(
            new[] { "AnotherMod", "ModLaunchManager" }));
        Assert.False(global::ItsLoading.ItsLoading.IsLoadOrderManaged(
            new[] { "modlaunchmanager" }));
        Assert.False(global::ItsLoading.ItsLoading.IsLoadOrderManaged(
            Array.Empty<string>()));
    }
}
