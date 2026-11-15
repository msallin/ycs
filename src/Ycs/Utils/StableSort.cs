namespace Ycs;

/// <summary>
/// A stable sort, as JavaScript's <c>Array.prototype.sort</c>: Yjs relies on equal elements keeping
/// their order (<see cref="List{T}.Sort(Comparison{T})"/> is unstable beyond 16 elements).
/// </summary>
internal static class StableSort
{
    private const int InsertionSortLimit = 32;

    public static void Sort<T>(List<T> list, Comparison<T> comparison)
    {
        var count = list.Count;
        if (count <= InsertionSortLimit)
        {
            InsertionSort(list, 0, count, comparison);
            return;
        }

        var items = list.ToArray();
        var buffer = new T[count];
        MergeSort(items, buffer, 0, count, comparison);
        for (var i = 0; i < count; i++)
        {
            list[i] = items[i];
        }
    }

    // Also fast for a list that is sorted but for one element, as the decoders of MergeUpdates are.
    private static void InsertionSort<T>(List<T> list, int start, int end, Comparison<T> comparison)
    {
        for (var i = start + 1; i < end; i++)
        {
            var item = list[i];
            var j = i - 1;
            while (j >= start && comparison(list[j], item) > 0)
            {
                list[j + 1] = list[j];
                j--;
            }

            list[j + 1] = item;
        }
    }

    private static void MergeSort<T>(T[] items, T[] buffer, int start, int end, Comparison<T> comparison)
    {
        if (end - start < 2)
        {
            return;
        }

        var middle = start + ((end - start) / 2);
        MergeSort(items, buffer, start, middle, comparison);
        MergeSort(items, buffer, middle, end, comparison);
        if (comparison(items[middle - 1], items[middle]) <= 0)
        {
            return;
        }

        Array.Copy(items, start, buffer, start, end - start);
        int left = start, right = middle, target = start;
        while (left < middle && right < end)
        {
            // Ties take the left element: that keeps the sort stable.
            items[target++] = comparison(buffer[right], buffer[left]) < 0 ? buffer[right++] : buffer[left++];
        }

        while (left < middle)
        {
            items[target++] = buffer[left++];
        }

        while (right < end)
        {
            items[target++] = buffer[right++];
        }
    }
}
