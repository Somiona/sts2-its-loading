using System;
using ItsLoading;
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

    [Fact]
    public void Delegated_mod_stage_is_hidden_but_later_stages_are_presented()
    {
        Assert.False(global::ItsLoading.ItsLoading.ShouldPresentStage(
            modStageDelegated: true, BootStage.Mods));
        Assert.True(global::ItsLoading.ItsLoading.ShouldPresentStage(
            modStageDelegated: true, BootStage.Essential));
        Assert.True(global::ItsLoading.ItsLoading.ShouldPresentStage(
            modStageDelegated: false, BootStage.Mods));
    }
}
