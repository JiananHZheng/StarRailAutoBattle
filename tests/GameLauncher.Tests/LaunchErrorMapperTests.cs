using GameLauncher.Core;

namespace GameLauncher.Tests;

public class LaunchErrorMapperTests
{
    [Theory]
    [InlineData(1223, LaunchFailureCategory.UserCancelledElevation)]
    [InlineData(2, LaunchFailureCategory.NotFound)]
    [InlineData(3, LaunchFailureCategory.NotFound)]
    [InlineData(5, LaunchFailureCategory.AccessDenied)]
    [InlineData(740, LaunchFailureCategory.ElevationRequired)]
    [InlineData(193, LaunchFailureCategory.BadExecutable)]
    [InlineData(216, LaunchFailureCategory.BadExecutable)]
    [InlineData(225, LaunchFailureCategory.BlockedBySecurity)]
    [InlineData(1260, LaunchFailureCategory.BlockedBySecurity)]
    [InlineData(4551, LaunchFailureCategory.BlockedBySecurity)]
    [InlineData(1155, LaunchFailureCategory.NoAssociation)]
    [InlineData(31, LaunchFailureCategory.Other)]
    public void Maps_win32_errors_to_categories(int code, LaunchFailureCategory expected)
    {
        var failure = LaunchErrorMapper.Map(code);
        Assert.Equal(expected, failure.Category);
        Assert.Equal(code, failure.Win32Error);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.False(string.IsNullOrWhiteSpace(failure.SystemMessage));
    }

    [Fact]
    public void Access_denied_does_not_claim_a_specific_cause()
    {
        var message = LaunchErrorMapper.Map(5).Message;
        Assert.Contains("无法判断原因", message);
        Assert.DoesNotContain("以管理员身份运行本应用", message);
    }

    [Fact]
    public void Access_denied_mentions_manifest_when_target_requires_admin()
    {
        Assert.Contains("UAC", LaunchErrorMapper.Map(5, ExecutionLevel.RequireAdministrator).Message);
        Assert.DoesNotContain("清单", LaunchErrorMapper.Map(5, ExecutionLevel.AsInvoker).Message);
    }

    [Fact]
    public void Unknown_code_includes_the_number()
    {
        Assert.Contains("31", LaunchErrorMapper.Map(31).Message);
    }
}
