namespace WeChatJevHud.Observer;

internal static class SequenceAlignment
{
    public static IReadOnlyList<(int Left, int Right)> Align(
        int leftCount,
        int rightCount,
        Func<int, int, bool> isMatch)
    {
        var lengths = new int[leftCount + 1, rightCount + 1];
        for (var left = leftCount - 1; left >= 0; left--)
        {
            for (var right = rightCount - 1; right >= 0; right--)
            {
                lengths[left, right] = isMatch(left, right)
                    ? 1 + lengths[left + 1, right + 1]
                    : Math.Max(lengths[left + 1, right], lengths[left, right + 1]);
            }
        }

        var matches = new List<(int Left, int Right)>();
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < leftCount && rightIndex < rightCount)
        {
            if (isMatch(leftIndex, rightIndex) &&
                lengths[leftIndex, rightIndex] == 1 + lengths[leftIndex + 1, rightIndex + 1])
            {
                matches.Add((leftIndex, rightIndex));
                leftIndex++;
                rightIndex++;
            }
            else if (lengths[leftIndex + 1, rightIndex] >= lengths[leftIndex, rightIndex + 1])
            {
                leftIndex++;
            }
            else
            {
                rightIndex++;
            }
        }

        return matches;
    }
}
