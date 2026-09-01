using FileComparer.Domain.Rules;
using Xunit;

namespace FileComparer.Tests.Application;

public sealed class PerceptualHashDistanceTests
{
    [Fact]
    public void CalculateMinimumReturnsClosestCandidateDistance()
    {
        var distance = PerceptualHashDistance.CalculateMinimum(
            [0b0000UL, 0b1111UL],
            [0b0011UL, 0b1111UL]);

        Assert.Equal(0, distance);
    }

    [Fact]
    public void CalculateMinimumReturnsMaximumDistanceForEmptyCandidates()
    {
        var distance = PerceptualHashDistance.CalculateMinimum([], [0UL]);

        Assert.Equal(64, distance);
    }
}
