namespace WeChatJevHud.Observer;

internal static class SequenceAlignment
{
    public static IReadOnlyList<(int Left, int Right)> Align(
        int leftCount,
        int rightCount,
        Func<int, int, bool> isMatch,
        Func<int, int, double>? matchCost = null)
    {
        var lengths = new int[leftCount + 1, rightCount + 1];
        var costs = new double[leftCount + 1, rightCount + 1];
        var choices = new byte[leftCount, rightCount];
        for (var left = leftCount - 1; left >= 0; left--)
        {
            for (var right = rightCount - 1; right >= 0; right--)
            {
                var count = lengths[left + 1, right];
                var cost = costs[left + 1, right];
                byte choice = 1;
                if (lengths[left, right + 1] > count ||
                    (lengths[left, right + 1] == count && costs[left, right + 1] < cost))
                {
                    count = lengths[left, right + 1];
                    cost = costs[left, right + 1];
                    choice = 2;
                }
                if (isMatch(left, right))
                {
                    var pairedCount = 1 + lengths[left + 1, right + 1];
                    var pairedCost = (matchCost?.Invoke(left, right) ?? 0) + costs[left + 1, right + 1];
                    if (pairedCount > count || (pairedCount == count && pairedCost <= cost))
                    {
                        count = pairedCount;
                        cost = pairedCost;
                        choice = 3;
                    }
                }
                lengths[left, right] = count;
                costs[left, right] = cost;
                choices[left, right] = choice;
            }
        }

        var matches = new List<(int Left, int Right)>();
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < leftCount && rightIndex < rightCount)
        {
            if (choices[leftIndex, rightIndex] == 3)
            {
                matches.Add((leftIndex, rightIndex));
                leftIndex++;
                rightIndex++;
            }
            else if (choices[leftIndex, rightIndex] == 1)
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
