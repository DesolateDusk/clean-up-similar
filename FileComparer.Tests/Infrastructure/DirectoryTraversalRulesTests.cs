using FileComparer.Infrastructure.FileSystem;
using Xunit;

namespace FileComparer.Tests.Infrastructure;

public sealed class DirectoryTraversalRulesTests
{
    [Theory]
    [InlineData(FileAttributes.Directory, true)]
    [InlineData(FileAttributes.Normal, true)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, false)]
    [InlineData(FileAttributes.ReparsePoint, false)]
    public void ReparsePointsAreNotFollowedOutsideSelectedTree(
        FileAttributes attributes,
        bool expected)
    {
        Assert.Equal(
            expected,
            DirectoryTraversalRules.CanProcess(attributes));
    }
}
