namespace Ycs;

/// <summary>
/// JavaScript's <c>Array.prototype.sort</c> as V8 does it (TimSort, <c>third_party/v8/builtins/array-sort.tq</c>):
/// stable, and it asks the comparison the same questions in the same order, so that a comparison that is not
/// consistent gives the order V8 gives. Yjs' <c>mergeUpdatesV2</c> sorts its struct readers with one: a GC struct and
/// an item at the same clock compare as less in both orders, and the one Yjs keeps (under Node) is the one V8 puts
/// first. (<see cref="List{T}.Sort(Comparison{T})"/> is not stable beyond 16 elements.)
/// </summary>
internal static class StableSort
{
    // V8's kMinGallopWins: the gallop threshold a sort starts with, and the wins that keep a merge galloping.
    private const int MinGallopWins = 7;

    public static void Sort<T>(List<T> list, Comparison<T> comparison)
    {
        var length = list.Count;
        if (length < 2)
        {
            return;
        }

        var minRunLength = ComputeMinRunLength(length);
        if (minRunLength == length)
        {
            // One run (below 64 elements): nothing to merge, and nothing to allocate.
            var run = CountAndMakeRun(list, comparison, 0, length);
            if (run < length)
            {
                BinaryInsertionSort(list, comparison, 0, run, length);
            }

            return;
        }

        var state = new State<T>(list, comparison);
        var remaining = length;
        var low = 0;
        while (remaining != 0)
        {
            var currentRunLength = CountAndMakeRun(list, comparison, low, low + remaining);
            if (currentRunLength < minRunLength)
            {
                var forcedRunLength = Math.Min(minRunLength, remaining);
                BinaryInsertionSort(list, comparison, low, low + currentRunLength, low + forcedRunLength);
                currentRunLength = forcedRunLength;
            }

            state.PushRun(low, currentRunLength);
            state.MergeCollapse();
            low += currentRunLength;
            remaining -= currentRunLength;
        }

        state.MergeForceCollapse();
    }

    private static int ComputeMinRunLength(int length)
    {
        var n = length;
        var r = 0;
        while (n >= 64)
        {
            r |= n & 1;
            n >>= 1;
        }

        return n + r;
    }

    /// <summary>The length of the run that starts at <paramref name="lowArg"/>; a descending run is reversed.</summary>
    private static int CountAndMakeRun<T>(List<T> work, Comparison<T> compare, int lowArg, int high)
    {
        var low = lowArg + 1;
        if (low == high)
        {
            return 1;
        }

        var runLength = 2;
        var elementLow = work[low];
        var order = compare(elementLow, work[low - 1]);

        // Strictly descending, so that reversing keeps the sort stable.
        var isDescending = order < 0;
        var previousElement = elementLow;
        for (var idx = low + 1; idx < high; ++idx)
        {
            var currentElement = work[idx];
            order = compare(currentElement, previousElement);
            if (isDescending ? order >= 0 : order < 0)
            {
                break;
            }

            previousElement = currentElement;
            ++runLength;
        }

        if (isDescending)
        {
            work.Reverse(lowArg, runLength);
        }

        return runLength;
    }

    /// <summary>Sorts [<paramref name="low"/>, <paramref name="high"/>), of which [low, startArg) is sorted.</summary>
    private static void BinaryInsertionSort<T>(List<T> work, Comparison<T> compare, int low, int startArg, int high)
    {
        var start = low == startArg ? startArg + 1 : startArg;
        for (; start < high; ++start)
        {
            var left = low;
            var right = start;
            var pivot = work[start];

            // Invariants: pivot >= all in [low, left), pivot < all in [right, start).
            while (left < right)
            {
                var mid = left + ((right - left) >> 1);
                if (compare(pivot, work[mid]) < 0)
                {
                    right = mid;
                }
                else
                {
                    left = mid + 1;
                }
            }

            for (var p = start; p > left; --p)
            {
                work[p] = work[p - 1];
            }

            work[left] = pivot;
        }
    }

    /// <summary>The merges of one sort: its pending runs (base and length) and the gallop threshold.</summary>
    private sealed class State<T>
    {
        private readonly List<T> _work;
        private readonly Comparison<T> _compare;
        private readonly List<(int Base, int Length)> _runs = new();
        private T[] _temp = Array.Empty<T>();
        private int _minGallop = MinGallopWins;

        public State(List<T> work, Comparison<T> compare)
        {
            _work = work;
            _compare = compare;
        }

        public void PushRun(int runBase, int length) => _runs.Add((runBase, length));

        /// <summary>Merges runs until the invariants on their lengths hold again (V8's <c>MergeCollapse</c>).</summary>
        public void MergeCollapse()
        {
            while (_runs.Count > 1)
            {
                var n = _runs.Count - 2;
                if (!RunInvariantEstablished(n + 1) || !RunInvariantEstablished(n))
                {
                    if (_runs[n - 1].Length < _runs[n + 1].Length)
                    {
                        --n;
                    }

                    MergeAt(n);
                }
                else if (_runs[n].Length <= _runs[n + 1].Length)
                {
                    MergeAt(n);
                }
                else
                {
                    break;
                }
            }
        }

        public void MergeForceCollapse()
        {
            while (_runs.Count > 1)
            {
                var n = _runs.Count - 2;
                if (n > 0 && _runs[n - 1].Length < _runs[n + 1].Length)
                {
                    --n;
                }

                MergeAt(n);
            }
        }

        private bool RunInvariantEstablished(int n) =>
            n < 2 || _runs[n - 2].Length > _runs[n - 1].Length + _runs[n].Length;

        /// <summary>Merges the runs at <paramref name="i"/> and <paramref name="i"/> + 1 (V8's <c>MergeAt</c>).</summary>
        private void MergeAt(int i)
        {
            var (baseA, lengthA) = _runs[i];
            var (baseB, lengthB) = _runs[i + 1];
            _runs[i] = (baseA, lengthA + lengthB);
            _runs.RemoveAt(i + 1);

            // Where does b start in a? Elements in a before that can be ignored.
            var k = GallopRight(_work, _work[baseB], baseA, lengthA, 0);
            baseA += k;
            lengthA -= k;
            if (lengthA == 0)
            {
                return;
            }

            // Where does a end in b? Elements in b after that can be ignored.
            lengthB = GallopLeft(_work, _work[baseA + lengthA - 1], baseB, lengthB, lengthB - 1);
            if (lengthB == 0)
            {
                return;
            }

            if (lengthA <= lengthB)
            {
                MergeLow(baseA, lengthA, baseB, lengthB);
            }
            else
            {
                MergeHigh(baseA, lengthA, baseB, lengthB);
            }
        }

        /// <summary>
        /// Where <paramref name="key"/> belongs in the sorted [base, base + length) of <paramref name="array"/>, left of
        /// any equal element, searched from <paramref name="hint"/> outwards (V8's <c>GallopLeft</c>).
        /// </summary>
        private int GallopLeft(IList<T> array, T key, int runBase, int length, int hint)
        {
            var lastOfs = 0;
            var offset = 1;
            if (_compare(array[runBase + hint], key) < 0)
            {
                // a[hint] < key: gallop right until a[hint + lastOfs] < key <= a[hint + offset].
                var maxOfs = length - hint;
                while (offset < maxOfs)
                {
                    if (_compare(array[runBase + hint + offset], key) >= 0)
                    {
                        break;
                    }

                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0)
                    {
                        offset = maxOfs;
                    }
                }

                if (offset > maxOfs)
                {
                    offset = maxOfs;
                }

                lastOfs += hint;
                offset += hint;
            }
            else
            {
                // key <= a[hint]: gallop left until a[hint - offset] < key <= a[hint - lastOfs].
                var maxOfs = hint + 1;
                while (offset < maxOfs)
                {
                    if (_compare(array[runBase + hint - offset], key) < 0)
                    {
                        break;
                    }

                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0)
                    {
                        offset = maxOfs;
                    }
                }

                if (offset > maxOfs)
                {
                    offset = maxOfs;
                }

                var tmp = lastOfs;
                lastOfs = hint - offset;
                offset = hint - tmp;
            }

            // a[lastOfs] < key <= a[offset]: a binary search in between.
            lastOfs++;
            while (lastOfs < offset)
            {
                var m = lastOfs + ((offset - lastOfs) >> 1);
                if (_compare(array[runBase + m], key) < 0)
                {
                    lastOfs = m + 1;
                }
                else
                {
                    offset = m;
                }
            }

            return offset;
        }

        /// <summary>As <see cref="GallopLeft"/>, but right of any equal element (V8's <c>GallopRight</c>).</summary>
        private int GallopRight(IList<T> array, T key, int runBase, int length, int hint)
        {
            var lastOfs = 0;
            var offset = 1;
            if (_compare(key, array[runBase + hint]) < 0)
            {
                // key < a[hint]: gallop left until a[hint - offset] <= key < a[hint - lastOfs].
                var maxOfs = hint + 1;
                while (offset < maxOfs)
                {
                    if (_compare(key, array[runBase + hint - offset]) >= 0)
                    {
                        break;
                    }

                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0)
                    {
                        offset = maxOfs;
                    }
                }

                if (offset > maxOfs)
                {
                    offset = maxOfs;
                }

                var tmp = lastOfs;
                lastOfs = hint - offset;
                offset = hint - tmp;
            }
            else
            {
                // a[hint] <= key: gallop right until a[hint + lastOfs] <= key < a[hint + offset].
                var maxOfs = length - hint;
                while (offset < maxOfs)
                {
                    if (_compare(key, array[runBase + hint + offset]) < 0)
                    {
                        break;
                    }

                    lastOfs = offset;
                    offset = (offset << 1) + 1;
                    if (offset <= 0)
                    {
                        offset = maxOfs;
                    }
                }

                if (offset > maxOfs)
                {
                    offset = maxOfs;
                }

                lastOfs += hint;
                offset += hint;
            }

            // a[lastOfs] <= key < a[offset]: a binary search in between.
            lastOfs++;
            while (lastOfs < offset)
            {
                var m = lastOfs + ((offset - lastOfs) >> 1);
                if (_compare(key, array[runBase + m]) < 0)
                {
                    offset = m;
                }
                else
                {
                    lastOfs = m + 1;
                }
            }

            return offset;
        }

        private T[] Temp(int length)
        {
            if (_temp.Length < length)
            {
                _temp = new T[length];
            }

            return _temp;
        }

        private void CopyToTemp(int from, T[] temp, int count)
        {
            for (var i = 0; i < count; i++)
            {
                temp[i] = _work[from + i];
            }
        }

        private void CopyFromTemp(T[] temp, int from, int to, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _work[to + i] = temp[from + i];
            }
        }

        // Within the list; the ranges may overlap, so it copies in the direction that is safe.
        private void CopyWithin(int from, int to, int count)
        {
            if (from < to)
            {
                for (var i = count - 1; i >= 0; i--)
                {
                    _work[to + i] = _work[from + i];
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    _work[to + i] = _work[from + i];
                }
            }
        }

        /// <summary>
        /// Merges the adjacent runs a and b, a not longer than b, from the left (V8's <c>MergeLow</c>). The first element
        /// of b is less than the first of a, and the last of a is greater than every element of b.
        /// </summary>
        private void MergeLow(int baseA, int lengthA, int baseB, int lengthB)
        {
            var temp = Temp(lengthA);
            CopyToTemp(baseA, temp, lengthA);
            var dest = baseA;
            var cursorTemp = 0;
            var cursorB = baseB;
            _work[dest++] = _work[cursorB++];

            if (--lengthB == 0)
            {
                Succeed();
                return;
            }

            if (lengthA == 1)
            {
                CopyB();
                return;
            }

            var minGallop = _minGallop;
            while (true)
            {
                var nofWinsA = 0;
                var nofWinsB = 0;

                // One element at a time until one run appears to win consistently.
                while (true)
                {
                    if (_compare(_work[cursorB], temp[cursorTemp]) < 0)
                    {
                        _work[dest++] = _work[cursorB++];
                        ++nofWinsB;
                        --lengthB;
                        nofWinsA = 0;
                        if (lengthB == 0)
                        {
                            Succeed();
                            return;
                        }

                        if (nofWinsB >= minGallop)
                        {
                            break;
                        }
                    }
                    else
                    {
                        _work[dest++] = temp[cursorTemp++];
                        ++nofWinsA;
                        --lengthA;
                        nofWinsB = 0;
                        if (lengthA == 1)
                        {
                            CopyB();
                            return;
                        }

                        if (nofWinsA >= minGallop)
                        {
                            break;
                        }
                    }
                }

                // Galloping, until neither run wins by MinGallopWins or more.
                ++minGallop;
                var firstIteration = true;
                while (nofWinsA >= MinGallopWins || nofWinsB >= MinGallopWins || firstIteration)
                {
                    firstIteration = false;
                    minGallop = Math.Max(1, minGallop - 1);
                    _minGallop = minGallop;

                    nofWinsA = GallopRight(temp, _work[cursorB], cursorTemp, lengthA, 0);
                    if (nofWinsA > 0)
                    {
                        CopyFromTemp(temp, cursorTemp, dest, nofWinsA);
                        dest += nofWinsA;
                        cursorTemp += nofWinsA;
                        lengthA -= nofWinsA;
                        if (lengthA == 1)
                        {
                            CopyB();
                            return;
                        }

                        // With a comparison that is not consistent, a can run out here.
                        if (lengthA == 0)
                        {
                            Succeed();
                            return;
                        }
                    }

                    _work[dest++] = _work[cursorB++];
                    if (--lengthB == 0)
                    {
                        Succeed();
                        return;
                    }

                    nofWinsB = GallopLeft(_work, temp[cursorTemp], cursorB, lengthB, 0);
                    if (nofWinsB > 0)
                    {
                        CopyWithin(cursorB, dest, nofWinsB);
                        dest += nofWinsB;
                        cursorB += nofWinsB;
                        lengthB -= nofWinsB;
                        if (lengthB == 0)
                        {
                            Succeed();
                            return;
                        }
                    }

                    _work[dest++] = temp[cursorTemp++];
                    if (--lengthA == 1)
                    {
                        CopyB();
                        return;
                    }
                }

                // Penalize leaving the galloping mode.
                ++minGallop;
                _minGallop = minGallop;
            }

            void Succeed()
            {
                if (lengthA > 0)
                {
                    CopyFromTemp(temp, cursorTemp, dest, lengthA);
                }
            }

            // The last element of a belongs at the end of the merge.
            void CopyB()
            {
                CopyWithin(cursorB, dest, lengthB);
                _work[dest + lengthB] = temp[cursorTemp];
            }
        }

        /// <summary>
        /// Merges the adjacent runs a and b, b not longer than a, from the right (V8's <c>MergeHigh</c>), under the
        /// conditions of <see cref="MergeLow"/>.
        /// </summary>
        private void MergeHigh(int baseA, int lengthA, int baseB, int lengthB)
        {
            var temp = Temp(lengthB);
            CopyToTemp(baseB, temp, lengthB);
            var dest = baseB + lengthB - 1;
            var cursorTemp = lengthB - 1;
            var cursorA = baseA + lengthA - 1;
            _work[dest--] = _work[cursorA--];

            if (--lengthA == 0)
            {
                Succeed();
                return;
            }

            if (lengthB == 1)
            {
                CopyA();
                return;
            }

            var minGallop = _minGallop;
            while (true)
            {
                var nofWinsA = 0;
                var nofWinsB = 0;

                // One element at a time until one run appears to win consistently.
                while (true)
                {
                    if (_compare(temp[cursorTemp], _work[cursorA]) < 0)
                    {
                        _work[dest--] = _work[cursorA--];
                        ++nofWinsA;
                        --lengthA;
                        nofWinsB = 0;
                        if (lengthA == 0)
                        {
                            Succeed();
                            return;
                        }

                        if (nofWinsA >= minGallop)
                        {
                            break;
                        }
                    }
                    else
                    {
                        _work[dest--] = temp[cursorTemp--];
                        ++nofWinsB;
                        --lengthB;
                        nofWinsA = 0;
                        if (lengthB == 1)
                        {
                            CopyA();
                            return;
                        }

                        if (nofWinsB >= minGallop)
                        {
                            break;
                        }
                    }
                }

                // Galloping, until neither run wins by MinGallopWins or more.
                ++minGallop;
                var firstIteration = true;
                while (nofWinsA >= MinGallopWins || nofWinsB >= MinGallopWins || firstIteration)
                {
                    firstIteration = false;
                    minGallop = Math.Max(1, minGallop - 1);
                    _minGallop = minGallop;

                    var k = GallopRight(_work, temp[cursorTemp], baseA, lengthA, lengthA - 1);
                    nofWinsA = lengthA - k;
                    if (nofWinsA > 0)
                    {
                        dest -= nofWinsA;
                        cursorA -= nofWinsA;
                        CopyWithin(cursorA + 1, dest + 1, nofWinsA);
                        lengthA -= nofWinsA;
                        if (lengthA == 0)
                        {
                            Succeed();
                            return;
                        }
                    }

                    _work[dest--] = temp[cursorTemp--];
                    if (--lengthB == 1)
                    {
                        CopyA();
                        return;
                    }

                    k = GallopLeft(temp, _work[cursorA], 0, lengthB, lengthB - 1);
                    nofWinsB = lengthB - k;
                    if (nofWinsB > 0)
                    {
                        dest -= nofWinsB;
                        cursorTemp -= nofWinsB;
                        CopyFromTemp(temp, cursorTemp + 1, dest + 1, nofWinsB);
                        lengthB -= nofWinsB;
                        if (lengthB == 1)
                        {
                            CopyA();
                            return;
                        }

                        // With a comparison that is not consistent, b can run out here.
                        if (lengthB == 0)
                        {
                            Succeed();
                            return;
                        }
                    }

                    _work[dest--] = _work[cursorA--];
                    if (--lengthA == 0)
                    {
                        Succeed();
                        return;
                    }
                }

                // Penalize leaving the galloping mode.
                ++minGallop;
                _minGallop = minGallop;
            }

            void Succeed()
            {
                if (lengthB > 0)
                {
                    CopyFromTemp(temp, 0, dest - (lengthB - 1), lengthB);
                }
            }

            // The first element of b belongs at the front of the merge.
            void CopyA()
            {
                dest -= lengthA;
                cursorA -= lengthA;
                CopyWithin(cursorA + 1, dest + 1, lengthA);
                _work[dest] = temp[cursorTemp];
            }
        }
    }
}
