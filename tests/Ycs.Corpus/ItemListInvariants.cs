namespace Ycs.Corpus;

/// <summary>
/// The list invariant of the Lean 4 formalization of Yjs (github.com/iasakura/lean-yjs at
/// 6a1bfe1, "Lean" below), checked on the item list of a Ycs sequence.
///
/// Lean's state is an array of single-element items, each with an id, an origin and a right
/// origin (pointers to items of the same array, or the sentinels first / last), and the proofs
/// keep <c>YjsArrInvariant</c> (LeanYjs/Algorithm/Invariant/YjsArray.lean):
/// <c>closed</c>, <c>item_set_inv</c> (I1 <c>origin_not_leq</c>, I3
/// <c>origins_adjacent_in_reachable</c>, I2 <c>id_unique</c>), <c>sorted</c> and <c>unique</c>.
///
/// Ycs holds runs (one Item of length n is n elements) and keeps tombstones in the list, as
/// Lean does (deletion only adds to <c>deletedIds</c>, LeanYjs/Algorithm/Delete/Basic.lean
/// <c>deleteById</c>). A run is expanded into the elements Lean would hold: element k > 0 of a
/// run has origin "element k - 1" and the run's right origin. That is exactly the item Yjs
/// itself materializes when it splits a run (Item.SplitItem, and Item.Integrate with an
/// offset), and the only kind of run Item.MergeWith builds, so the expansion is faithful, not an
/// approximation. With the expansion every Lean definition applies unchanged. Content is
/// irrelevant to Lean, so format and embed items of a Y.Text count as elements like characters.
/// </summary>
/// <remarks>
/// Not checked, because it does not carry over:
/// - Map entries (the parentSub lists of Y.Map and of attributes): outside the Lean model
///   (TECHNICAL.md: "the item-run splitting and parentSub map handling of Yjs are outside the
///   model"). Only sequences are checked.
/// - GC structs (the remains of a collected parent): they have no origins. One inside a sequence
///   is reported as a violation, not checked past.
/// - Lean's `maximalId` / `isClockSafe` (Insert/Spec.lean, Insert/Basic.lean) and `IsItemValid`
///   (Insert/Spec.lean) are preconditions of one insert, relative to the state it is applied to,
///   not properties of a list; on a stored list what remains of them is I1 and I3 per element.
/// - The premises of `insert_commutative` (distinct client ids, neither item reachable from the
///   other) hold for concurrent updates by construction; the brute-force tests exercise the
///   conclusion instead (CommutativityTests).
/// </remarks>
internal static class ItemListInvariants
{
    /// <summary>
    /// Above this many elements the quadratic checks (pairwise <c>sorted</c>, I3, no-cross-origin)
    /// are skipped and only adjacent pairs are compared; the report says so.
    /// </summary>
    public const int QuadraticLimit = 1500;

    public readonly record struct ElementId(long Client, long Clock)
    {
        public override string ToString() => $"{Client}:{Clock}";
    }

    public sealed record Report(IReadOnlyList<string> Violations, IReadOnlyList<string> Notes, IReadOnlyList<ElementId> Order, IReadOnlyList<bool> Deleted);

    private const int First = -1;
    private const int Unresolved = int.MinValue;
    private const int MaxReported = 12;

    /// <summary>
    /// Checks <paramref name="runs"/>. <paramref name="full"/> adds the quadratic checks (up to
    /// <see cref="QuadraticLimit"/> elements); without it the check is linear apart from the
    /// memoized order relation on adjacent pairs. <paramref name="previous"/> is the element
    /// order and deleted flags of the same list after an earlier update, if any.
    /// </summary>
    public static Report Check(IReadOnlyList<ItemRun> runs, bool full, Report previous = null)
    {
        var violations = new List<string>();
        var notes = new List<string>();
        void Fail(string message)
        {
            if (violations.Count < MaxReported)
            {
                violations.Add(message);
            }
        }

        // Representation. No Lean counterpart: Lean's list is an Array, Ycs' a doubly linked list
        // of structs whose parent is the sequence.
        foreach (var run in runs)
        {
            if (run.Kind != ItemRun.ItemKind)
            {
                Fail($"{run.Kind} struct {run.Client}:{run.Clock} in the item list; it has no origins, so the Lean invariant cannot be checked past it.");
            }
            else if (!run.LinkedBack)
            {
                Fail($"Item {run.Client}:{run.Clock}: Left is not the preceding struct of the list.");
            }
            else if (!run.InParent)
            {
                Fail($"Item {run.Client}:{run.Clock}: Parent is not this sequence, or ParentSub is set.");
            }
            else if (run.Length <= 0)
            {
                Fail($"Item {run.Client}:{run.Clock}: length {run.Length}.");
            }
        }

        // Expansion into Lean's single-element items.
        var items = runs.Where(r => r.Kind == ItemRun.ItemKind).ToList();
        var n = items.Sum(r => r.Length);
        var last = n;
        var ids = new ElementId[n];
        var deleted = new bool[n];
        var report = new Report(violations, notes, ids, deleted);

        // Ids are resolved per run (binary search over each client's runs in clock order), not per
        // element: the check runs after every update of the convergence test.
        var index = new Dictionary<long, List<(long Clock, int Length, int Start)>>();
        var at = 0;
        foreach (var run in items)
        {
            for (var k = 0; k < run.Length; k++)
            {
                ids[at + k] = new ElementId(run.Client, run.Clock + k);
                deleted[at + k] = run.Deleted;
            }

            if (!index.TryGetValue(run.Client, out var list))
            {
                index[run.Client] = list = [];
            }

            list.Add((run.Clock, run.Length, at));
            at += run.Length;
        }

        // YjsArray.lean `YjsArrInvariant.unique` (`uniqueId`: List.Pairwise (x.id ≠ y.id)), which
        // is also Order/ItemSetInvariant.lean `ItemSetInvariant.id_unique` (I2) on ArrSet.
        foreach (var (client, list) in index)
        {
            list.Sort((a, b) => a.Clock.CompareTo(b.Clock));
            for (var k = 1; k < list.Count; k++)
            {
                if (list[k].Clock < list[k - 1].Clock + list[k - 1].Length)
                {
                    Fail($"uniqueId: id {client}:{list[k].Clock} occurs at positions {list[k - 1].Start + (list[k].Clock - list[k - 1].Clock)} and {list[k].Start}.");
                }
            }
        }

        // YjsArray.lean `YjsArrInvariant.closed`: ItemSet.lean `IsClosedItemSet (ArrSet arr)`,
        // fields `closedLeft` / `closedRight`: every origin and right origin is an item of the same
        // list (or a sentinel). A null origin is `YjsPtr.first` and a null right origin
        // `YjsPtr.last`, as IntegrateInput.toItem (Insert/Basic.lean) maps them, so the derived
        // facts "a right origin is never first" (ItemSetInvariant.lean `not_rightOrigin_first`) and
        // "an origin is never last" hold by construction and need no check.
        var origin = new int[n];
        var right = new int[n];
        at = 0;
        foreach (var run in items)
        {
            var o = run.Origin is (long oc, long ok) ? Position(new ElementId(oc, ok)) : First;
            var r = run.RightOrigin is (long rc, long rk) ? Position(new ElementId(rc, rk)) : last;
            if (o == Unresolved)
            {
                Fail($"closedLeft: origin {run.Origin.Value.Client}:{run.Origin.Value.Clock} of {ids[at]} is not in the list.");
            }

            if (r == Unresolved)
            {
                Fail($"closedRight: right origin {run.RightOrigin.Value.Client}:{run.RightOrigin.Value.Clock} of {ids[at]} is not in the list.");
            }

            for (var k = 0; k < run.Length; k++)
            {
                origin[at + k] = k == 0 ? o : at + k - 1;
                right[at + k] = r;
            }

            at += run.Length;
        }

        if (violations.Count > 0)
        {
            return report;
        }

        // Item.lean: `YjsItem` / `YjsPtr` are an inductive type, so the origin graph is well
        // founded (`YjsPtr.size` is finite); every recursion of the order relies on it.
        if (FindCycle(n, origin, right) is int cyclic)
        {
            Fail($"YjsItem is inductive (Item.lean): the origins of {ids[cyclic]} lead back to it.");
            return report;
        }

        // ItemOrder.lean `YjsLt'.origin_lt_self` and `YjsLt'.self_lt_rightOrigin` together with
        // `sorted`: an item's origin is to its left and its right origin to its right.
        for (var i = 0; i < n; i++)
        {
            if (origin[i] >= i)
            {
                Fail($"origin_lt_self + sorted: origin {ids[origin[i]]} of {ids[i]} (position {i}) is at position {origin[i]}, not to its left.");
            }

            if (right[i] <= i)
            {
                Fail($"self_lt_rightOrigin + sorted: right origin {ids[right[i]]} of {ids[i]} (position {i}) is at position {right[i]}, not to its right.");
            }
        }

        // Insert/Basic.lean `insert_ok_exists_insertIdx` (an insert is Array.insertIdx: nothing
        // already there moves or disappears), Delete/Basic.lean `deleteById` (a delete changes
        // only deletedIds, which only grows) and Network/Yjs/YjsNetwork.lean
        // `effect_list_preserves_mem`: the previous order is a subsequence of this one, and a
        // tombstone stays a tombstone.
        // known[i]: element i was there at the previous check; knownPair[i]: so were i and i + 1,
        // already adjacent. Items never change, so neither does YjsLt' between two of them: the fast
        // check only decides what an update can have changed.
        var known = new bool[n];
        var knownPair = new bool[n];
        if (previous != null)
        {
            var before = -1;
            for (var k = 0; k < previous.Order.Count; k++)
            {
                var id = previous.Order[k];
                var p = Position(id);
                if (p == Unresolved)
                {
                    Fail($"insert_ok_exists_insertIdx / effect_list_preserves_mem: {id} was in the list and is gone.");
                }
                else if (p <= before)
                {
                    Fail($"insert_ok_exists_insertIdx: {id} moved: it was after {previous.Order[k - 1]} and now is before it.");
                }
                else
                {
                    if (previous.Deleted[k] && !deleted[p])
                    {
                        Fail($"deleteById: {id} was deleted and is not any more.");
                    }

                    known[p] = true;
                    if (before >= 0 && p == before + 1)
                    {
                        knownPair[before] = true;
                    }

                    before = p;
                }
            }
        }

        if (violations.Count > 0)
        {
            return report;
        }

        var lt = new YjsOrder(ids, origin, right);
        OnLargeStackIfDeep(n, () =>
        {
            var quadratic = full && n <= QuadraticLimit;
            if (full && !quadratic)
            {
                notes.Add($"{n} elements > {QuadraticLimit}: sorted checked on adjacent pairs only (pairwise follows by Transitivity.lean `yjs_lt_trans` if I1-I3 hold); I3 and no_cross_origin not checked.");
            }

            // YjsArray.lean `YjsArrInvariant.sorted`: List.Pairwise YjsLt' arr, with YjsLt' the
            // inductive relation of ItemOrder.lean, decided by YjsOrder below. Without `full`, only
            // adjacent pairs not compared at the previous check (enough by `yjs_lt_trans` once the
            // rest of the invariant holds).
            for (var i = 0; i < n; i++)
            {
                if (!full && knownPair[i])
                {
                    continue;
                }

                for (var j = i + 1; j < (quadratic ? n : Math.Min(n, i + 2)); j++)
                {
                    if (!lt.Lt(i, j))
                    {
                        Fail($"sorted: {lt.Name(i)} at {i} is not YjsLt' {lt.Name(j)} at {j}.");
                    }
                }
            }

            // ItemSetInvariant.lean `origin_not_leq` (I1): YjsLt' o r for every item.
            for (var i = 0; i < n; i++)
            {
                if ((full || !known[i]) && !lt.Lt(origin[i], right[i]))
                {
                    Fail($"origin_not_leq (I1): for {lt.Name(i)}, origin is not YjsLt' right origin.");
                }
            }

            if (!quadratic)
            {
                return;
            }

            // ItemSetInvariant.lean `origins_adjacent_in_reachable` (I3): every pointer reachable
            // from an item through origin / right-origin steps (ItemOrder.lean `OriginReachable`)
            // is YjsLeq' its origin or its right origin is YjsLeq' it. This is the condition the
            // Lean counterexample (Insert/Basic.lean `InconsistencyExample`) violates.
            for (var i = 0; i < n; i++)
            {
                foreach (var z in Reachable(i, n, origin, right))
                {
                    if (!lt.LeqEither(z, origin[i], right[i], z))
                    {
                        Fail($"origins_adjacent_in_reachable (I3): {lt.Name(z)} is reachable from {lt.Name(i)} and lies strictly between its origin and right origin.");
                        break;
                    }
                }
            }

            // Order/NoCrossOrigin.lean `no_cross_origin` (a theorem, from I1-I3): for x <Y y,
            // y.origin ≤Y x.origin or x ≤Y y.origin. On the sorted list, x before y.
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (!lt.LeqEither(origin[j], origin[i], i, origin[j]))
                    {
                        Fail($"no_cross_origin: {lt.Name(j)} at {j} has its origin strictly between the origin of {lt.Name(i)} at {i} and that item.");
                    }
                }
            }
        });

        return report;

        int Position(ElementId id)
        {
            if (!index.TryGetValue(id.Client, out var list))
            {
                return Unresolved;
            }

            int lo = 0, hi = list.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (id.Clock < list[mid].Clock)
                {
                    hi = mid - 1;
                }
                else if (id.Clock >= list[mid].Clock + list[mid].Length)
                {
                    lo = mid + 1;
                }
                else
                {
                    return list[mid].Start + (int)(id.Clock - list[mid].Clock);
                }
            }

            return Unresolved;
        }
    }

    /// <summary>The element order compressed into maximal runs (client, clock, length, deleted), the shape gen.mjs records from Yjs.</summary>
    public static string Runs(Report report)
    {
        var parts = new List<string>();
        int i = 0, n = report.Order.Count;
        while (i < n)
        {
            var j = i + 1;
            while (j < n && report.Order[j].Client == report.Order[i].Client && report.Order[j].Clock == report.Order[j - 1].Clock + 1 && report.Deleted[j] == report.Deleted[i])
            {
                j++;
            }

            parts.Add($"{report.Order[i]}+{j - i}{(report.Deleted[i] ? "d" : "")}");
            i = j;
        }

        return string.Join(" ", parts);
    }

    private static int? FindCycle(int n, int[] origin, int[] right)
    {
        // Iterative DFS; 0 = unvisited, 1 = on the stack, 2 = done.
        var state = new byte[n];
        var stack = new Stack<(int Node, int Edge)>();
        for (var s = 0; s < n; s++)
        {
            if (state[s] != 0)
            {
                continue;
            }

            stack.Push((s, 0));
            state[s] = 1;
            while (stack.Count > 0)
            {
                var (node, edge) = stack.Pop();
                if (edge == 2)
                {
                    state[node] = 2;
                    continue;
                }

                stack.Push((node, edge + 1));
                var next = edge == 0 ? origin[node] : right[node];
                if (next < 0 || next >= n)
                {
                    continue;
                }

                if (state[next] == 1)
                {
                    return next;
                }

                if (state[next] == 0)
                {
                    state[next] = 1;
                    stack.Push((next, 0));
                }
            }
        }

        return null;
    }

    /// <summary>Every pointer (element index, or -1 / n for the sentinels) reachable from item i in one or more steps (ItemOrder.lean `OriginReachable`).</summary>
    private static HashSet<int> Reachable(int i, int n, int[] origin, int[] right)
    {
        var seen = new HashSet<int>();
        var todo = new Stack<int>();
        todo.Push(origin[i]);
        todo.Push(right[i]);
        while (todo.Count > 0)
        {
            var p = todo.Pop();
            if (!seen.Add(p) || p < 0 || p >= n)
            {
                continue;
            }

            todo.Push(origin[p]);
            todo.Push(right[p]);
        }

        return seen;
    }

    // The order relation recurses along origin chains, as deep as the list is long in the worst
    // case (one client typing); a thread with a large stack only pays off for long lists.
    private static void OnLargeStackIfDeep(int n, Action action)
    {
        if (n < 1000)
        {
            action();
            return;
        }

        lock (DeepWork)
        {
            DeepWork.Add(action);
            DeepDone.Wait();
            DeepDone.Reset();
            var error = _deepError;
            _deepError = null;
            if (error != null)
            {
                throw new InvalidOperationException("ItemListInvariants failed.", error);
            }
        }
    }

    // One background thread with a 512 MiB stack, started on first use and reused: creating such
    // a thread per check would dominate the per-update checks of long lists.
    private static readonly ManualResetEventSlim DeepDone = new();
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> DeepWork = StartDeepWorker();
    private static Exception _deepError;

    private static System.Collections.Concurrent.BlockingCollection<Action> StartDeepWorker()
    {
        var work = new System.Collections.Concurrent.BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _deepError = e;
                }

                DeepDone.Set();
            }
        }, 512 * 1024 * 1024)
        { IsBackground = true, Name = "ItemListInvariants" };
        thread.Start();
        return work;
    }

    /// <summary>
    /// Decides ItemOrder.lean's `YjsLt'` (∃ h, YjsLt h x y) and `YjsLeq'` on the pointers of one
    /// list: element indices 0..n-1, -1 for `YjsPtr.first`, n for `YjsPtr.last`. Every rule of the
    /// mutual inductive definition is tried; every premise is about a pair of strictly smaller
    /// total `YjsPtr.size`, so on a well-founded list the recursion terminates, and the memo keeps
    /// it at one evaluation per pair. The answer is the inductive relation's, never the list's: the
    /// list position only decides which rule is tried first. On a sorted list the first rule
    /// tried holds, so a check costs about a constant per pair; proving YjsLt' false (which a
    /// violation needs) explores every rule.
    /// </summary>
    private sealed class YjsOrder(IReadOnlyList<ElementId> ids, int[] origin, int[] right)
    {
        private readonly int _n = ids.Count;
        private readonly Dictionary<long, bool> _memo = [];

        public string Name(int p) => p == First ? "first" : p == _n ? "last" : ids[p].ToString();

        // `YjsLeq.leqSame` (pointer equality; ids are unique, checked before) or `YjsLeq.leqLt`.
        public bool Leq(int p, int q) => p == q || Lt(p, q);

        /// <summary>Leq(a, b) || Leq(c, d), trying first the one the list positions say holds.</summary>
        public bool LeqEither(int a, int b, int c, int d) =>
            a <= b ? Leq(a, b) || Leq(c, d) : Leq(c, d) || Leq(a, b);

        public bool Lt(int p, int q)
        {
            var key = ((long)(p + 1) * (_n + 2)) + q + 1;
            if (_memo.TryGetValue(key, out var known))
            {
                return known;
            }

            var result = Decide(p, q);
            _memo[key] = result;
            return result;
        }

        private bool Decide(int p, int q)
        {
            var pItem = p >= 0 && p < _n;
            var qItem = q >= 0 && q < _n;

            // `YjsLt.ltOriginOrder` with `OriginLt.lt_first`, `lt_last`, `lt_first_last`.
            if ((p == First && (qItem || q == _n)) || (pItem && q == _n))
            {
                return true;
            }

            // Positions as a guess: a rule whose premises the positions satisfy goes first.
            var byOrigin = qItem && p <= origin[q];
            var byRight = pItem && right[p] <= q;
            if (byOrigin && LtOrigin())
            {
                return true;
            }

            if (byRight && LtRightOrigin())
            {
                return true;
            }

            return (!byOrigin && LtOrigin()) || (!byRight && LtRightOrigin()) || (pItem && qItem && (OriginSame() || OriginDiff()));

            // `YjsLt.ltOrigin`: x ≤ item.origin → x < item.
            bool LtOrigin() => qItem && Leq(p, origin[q]);

            // `YjsLt.ltRightOrigin`: item.rightOrigin ≤ x → item < x.
            bool LtRightOrigin() => pItem && Leq(right[p], q);

            // `YjsLt.ltConflict` with `ConflictLt.ltOriginSame`: same origin, each before the
            // other's right origin, and id1 < id2 (Item.lean `instance : LT YjsId`).
            bool OriginSame() => origin[p] == origin[q] && IdLt(ids[p], ids[q]) && Lt(p, right[q]) && Lt(q, right[p]);

            // `ConflictLt.ltOriginDiff`: l2 < l1, x < r2, l1 < y, y < r1. The premises the
            // positions contradict go first, so a rule that does not apply fails early.
            bool OriginDiff()
            {
                (int, int)[] premises = [(origin[q], origin[p]), (p, right[q]), (origin[p], q), (q, right[p])];
                return premises.OrderBy(x => x.Item1 < x.Item2).All(x => Lt(x.Item1, x.Item2));
            }
        }

        // Item.lean `instance : LT YjsId`: same client → the larger clock is smaller; otherwise
        // the smaller client id. The client-id half is the tie-break of Yjs' integrate
        // (`o.id.client < this.id.client`, Insert/Basic.lean `findIntegratedIndex`).
        private static bool IdLt(ElementId a, ElementId b) => a.Client == b.Client ? a.Clock > b.Clock : a.Client < b.Client;
    }
}
