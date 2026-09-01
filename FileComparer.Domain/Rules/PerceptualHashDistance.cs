using System.Numerics;

namespace FileComparer.Domain.Rules;

/// <summary>
/// 計算 64 位元感知雜湊之間的距離。
/// </summary>
public static class PerceptualHashDistance
{
    /// <summary>
    /// 計算兩個 64 位元感知雜湊的漢明距離。
    /// </summary>
    /// <example>
    /// <code>
    /// var distance = PerceptualHashDistance.Calculate(leftHash, rightHash);
    /// </code>
    /// 結果為 <c>0</c> 代表兩個雜湊完全相同。
    /// </example>
    public static int Calculate(ulong left, ulong right) =>
        BitOperations.PopCount(left ^ right);

    /// <summary>
    /// 找出兩組候選感知雜湊之間的最小漢明距離。
    /// </summary>
    /// <remarks>
    /// 圖片指紋可能包含旋轉與裁切版本的雜湊。任一候選集合為空時，
    /// 回傳 <c>64</c>，也就是 64 位元雜湊可達到的最大距離。
    /// </remarks>
    public static int CalculateMinimum(
        IReadOnlyList<ulong> leftHashes,
        IReadOnlyList<ulong> rightHashes)
    {
        ArgumentNullException.ThrowIfNull(leftHashes);
        ArgumentNullException.ThrowIfNull(rightHashes);

        var minimumDistance = 64;
        foreach (var leftHash in leftHashes)
        {
            foreach (var rightHash in rightHashes)
            {
                var distance = Calculate(leftHash, rightHash);
                if (distance == 0)
                {
                    return 0;
                }

                minimumDistance = Math.Min(minimumDistance, distance);
            }
        }

        return minimumDistance;
    }
}
