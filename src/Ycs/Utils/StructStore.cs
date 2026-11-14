// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>The structs read from an update for one client, and how many were consumed.</summary>
    internal sealed class ClientStructRefs
    {
        public int NextReadOperation { get; set; }
        public List<AbstractStruct> Refs { get; set; } = new List<AbstractStruct>(1);
    }

    /// <summary>
    /// Structs that could not be integrated yet, kept as an encoded v2 update (Yjs' <c>store.pendingStructs</c>),
    /// and per client the lowest clock that is missing for them.
    /// <br/>
    /// They are re-read from the update when they are retried, so a root type realized in between (e.g. with
    /// <see cref="YDoc.GetText"/>) is resolved again instead of a stale placeholder being kept.
    /// </summary>
    internal sealed class PendingStructs
    {
        public Dictionary<long, long> Missing { get; init; }
        public byte[] Update { get; set; }
    }

    // TODO: [alekseyk] To private?
    public readonly IDictionary<long, List<AbstractStruct>> Clients = new Dictionary<long, List<AbstractStruct>>();

    internal PendingStructs Pending { get; set; }

    private readonly IList<DSDecoderV2> _pendingDeleteReaders = new List<DSDecoderV2>();

    /// <summary>
    /// Return the states as a Map<int,int>.
    /// Note that clock refers to the next expected clock id.
    /// </summary>
    public IDictionary<long, long> GetStateVector()
    {
        var result = new Dictionary<long, long>(Clients.Count);

        foreach (var kvp in Clients)
        {
            var str = kvp.Value[kvp.Value.Count - 1];
            result[kvp.Key] = str.Id.Clock + str.Length;
        }

        return result;
    }

    public long GetState(long clientId)
    {
        if (Clients.TryGetValue(clientId, out var structs))
        {
            var lastStruct = structs[structs.Count - 1];
            return lastStruct.Id.Clock + lastStruct.Length;
        }

        return 0;
    }

    public void IntegrityCheck()
    {
        foreach (var structs in Clients.Values)
        {
            if (structs.Count == 0)
            {
                throw new Exception($"{nameof(StructStore)} failed integrity check: no structs for client");
            }

            for (int i = 1; i < structs.Count; i++)
            {
                var left = structs[i - 1];
                var right = structs[i];

                if (left.Id.Clock + left.Length != right.Id.Clock)
                {
                    throw new Exception($"{nameof(StructStore)} failed integrity check: missing struct");
                }
            }
        }

        if (_pendingDeleteReaders.Count != 0 || Pending != null)
        {
            throw new Exception($"{nameof(StructStore)} failed integrity check: still have pending items");
        }
    }

    public void AddStruct(AbstractStruct str)
    {
        if (!Clients.TryGetValue(str.Id.Client, out var structs))
        {
            structs = new List<AbstractStruct>();
            Clients[str.Id.Client] = structs;
        }
        else
        {
            var lastStruct = structs[structs.Count - 1];
            if (lastStruct.Id.Clock + lastStruct.Length != str.Id.Clock)
            {
                throw new Exception("Unexpected");
            }
        }

        structs.Add(str);
    }

    /// <summary>
    /// Perform a binary search on a sorted array.
    /// </summary>
    // TODO: [alekseyk] IList<AbstractStruct> to custom class, and move this method there?
    public static int FindIndexSS(IList<AbstractStruct> structs, long clock)
    {
        Debug.Assert(structs.Count > 0);

        var left = 0;
        var right = structs.Count - 1;
        var mid = structs[right];
        var midClock = mid.Id.Clock;

        if (midClock == clock)
        {
            return right;
        }

        // @todo does it even make sense to pivot the search?
        // If a good split misses, it might actually increase the time to find the correct item.
        // Currently, the only advantage is that search with pivoting might find the item on the first try.
        int midIndex = (int)((clock * right) / (midClock + mid.Length - 1));
        while (left <= right)
        {
            mid = structs[midIndex];
            midClock = mid.Id.Clock;

            if (midClock <= clock)
            {
                if (clock < midClock + mid.Length)
                {
                    return midIndex;
                }

                left = midIndex + 1;
            }
            else
            {
                right = midIndex - 1;
            }

            midIndex = (left + right) / 2;
        }

        // Always check state before looking for a struct in StructStore
        // Therefore the case of not finding a struct is unexpected.
        throw new Exception("Unexpected");
    }

    /// <summary>
    /// Expects that id is actually in store. This function throws or is an infinite loop otherwise.
    /// </summary>
    public AbstractStruct Find(ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception($"No structs for client: {id.Client}");
        }

        int index = FindIndexSS(structs, id.Clock);
        if (index < 0 || index >= structs.Count)
        {
            throw new Exception($"Invalid struct index: {index}, max: {structs.Count}");
        }

        return structs[index];
    }

    public int FindIndexCleanStart(Transaction transaction, List<AbstractStruct> structs, long clock)
    {
        int index = FindIndexSS(structs, clock);
        var str = structs[index];
        if (str.Id.Clock < clock && str is Item item)
        {
            structs.Insert(index + 1, item.SplitItem(transaction, (int)(clock - item.Id.Clock)));
            return index + 1;
        }

        return index;
    }

    public AbstractStruct GetItemCleanStart(Transaction transaction, ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception();
        }

        int indexCleanStart = FindIndexCleanStart(transaction, structs, id.Clock);
        Debug.Assert(indexCleanStart >= 0 && indexCleanStart < structs.Count);
        return structs[indexCleanStart];
    }

    public AbstractStruct GetItemCleanEnd(Transaction transaction, ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception();
        }

        int index = FindIndexSS(structs, id.Clock);
        var str = structs[index];

        if ((id.Clock != str.Id.Clock + str.Length - 1) && !(str is GC))
        {
            structs.Insert(index + 1, (str as Item).SplitItem(transaction, (int)(id.Clock - str.Id.Clock + 1)));
        }

        return str;
    }

    public void ReplaceStruct(AbstractStruct oldStruct, AbstractStruct newStruct)
    {
        if (!Clients.TryGetValue(oldStruct.Id.Client, out var structs))
        {
            throw new Exception();
        }

        int index = FindIndexSS(structs, oldStruct.Id.Clock);
        structs[index] = newStruct;
    }

    public void IterateStructs(Transaction transaction, List<AbstractStruct> structs, long clockStart, long length, Predicate<AbstractStruct> fun)
    {
        if (length <= 0)
        {
            return;
        }

        var clockEnd = clockStart + length;

        var index = FindIndexCleanStart(transaction, structs, clockStart);
        AbstractStruct str;

        do
        {
            str = structs[index];

            if (clockEnd < str.Id.Clock + str.Length)
            {
                FindIndexCleanStart(transaction, structs, clockEnd);
            }

            if (!fun(str))
            {
                break;
            }

            index++;
        } while (index < structs.Count && structs[index].Id.Clock < clockEnd);
    }

    public (AbstractStruct item, int diff) FollowRedone(ID id)
    {
        ID? nextId = id;
        int diff = 0;
        AbstractStruct item;

        do
        {
            if (diff > 0)
            {
                nextId = new ID(nextId.Value.Client, nextId.Value.Clock + diff);
            }

            item = Find(nextId.Value);
            diff = (int)(nextId.Value.Clock - item.Id.Clock);
            nextId = (item as Item)?.Redone;
        } while (nextId != null && item is Item);

        return (item, diff);
    }

    public void ReadAndApplyDeleteSet(IDSDecoder decoder, Transaction transaction)
    {
        var unappliedDs = new DeleteSet();
        var numClients = decoder.Reader.ReadVarUint();

        for (int i = 0; i < numClients; i++)
        {
            decoder.ResetDsCurVal();

            var client = decoder.Reader.ReadVarUint();
            var numberOfDeletes = decoder.Reader.ReadVarUint();

            if (!Clients.TryGetValue(client, out var structs))
            {
                structs = new List<AbstractStruct>();
                // NOTE: Clients map is not updated.
            }

            var state = GetState(client);

            for (int deleteIndex = 0; deleteIndex < numberOfDeletes; deleteIndex++)
            {
                var clock = decoder.ReadDsClock();
                var clockEnd = clock + decoder.ReadDsLength();
                if (clock < state)
                {
                    if (state < clockEnd)
                    {
                        unappliedDs.Add(client, state, clockEnd - state);
                    }

                    var index = StructStore.FindIndexSS(structs, clock);

                    // We can ignore the case of GC and Delete structs, because we are going to skip them.
                    var str = structs[index];

                    // Split the first item if necessary.
                    if (!str.Deleted && str.Id.Clock < clock)
                    {
                        var splitItem = (str as Item).SplitItem(transaction, (int)(clock - str.Id.Clock));
                        structs.Insert(index + 1, splitItem);

                        // Increase, we now want to use the next struct.
                        index++;
                    }

                    while (index < structs.Count)
                    {
                        str = structs[index++];
                        if (str.Id.Clock < clockEnd)
                        {
                            if (!str.Deleted)
                            {
                                if (clockEnd < str.Id.Clock + str.Length)
                                {
                                    var splitItem = (str as Item).SplitItem(transaction, (int)(clockEnd - str.Id.Clock));
                                    structs.Insert(index, splitItem);
                                }

                                str.Delete(transaction);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                else
                {
                    unappliedDs.Add(client, clock, clockEnd - clock);
                }
            }
        }

        if (unappliedDs.Clients.Count > 0)
        {
            // @TODO: No need for encoding+decoding ds anymore.
            var unappliedDsEncoder = new DSEncoderV2();
            unappliedDs.Write(unappliedDsEncoder);
            _pendingDeleteReaders.Add(new DSDecoderV2(new Lib0Reader(unappliedDsEncoder.ToArray())));
        }
    }

    /// <summary>
    /// Integrates the structs read from an update (Yjs' <c>integrateStructs</c>).
    /// <br/>
    /// While there is something to do, we integrate structs in this order:
    /// 1. Top element on stack, if stack is not empty.
    /// 2. Next element from current struct reader (if empty, use next struct reader).
    /// <br/>
    /// If struct causally depends on another struct (ref.missing), we put next reader of
    /// 'ref.id.client' on top of stack.
    /// <br/>
    /// At some point we find a struct that has no causal dependencies, then we start
    /// emptying the stack.
    /// <br/>
    /// It is not possible to have circles: i.e. struct1 (from client1) depends on struct2 (from client2)
    /// depends on struct3 (from client1). Therefore, the max stack size is equal to 'structReaders.length'.
    /// <br/>
    /// When a struct depends on something that is not there, it and the rest of its client's structs
    /// are set aside and everything else is still integrated. Returns what was set aside, or null.
    /// </summary>
    internal PendingStructs IntegrateStructs(Transaction transaction, IDictionary<long, ClientStructRefs> clientsStructRefs)
    {
        var stack = new List<AbstractStruct>();

        // Sort them so that we take the higher id first, in case of conflicts the lower id will probably not conflict with the id from the higher user.
        var clientsStructRefsIds = clientsStructRefs.Keys.ToList();
        clientsStructRefsIds.Sort();
        if (clientsStructRefsIds.Count == 0)
        {
            return null;
        }

        ClientStructRefs getNextStructTarget()
        {
            if (clientsStructRefsIds.Count == 0)
            {
                return null;
            }

            var nextStructsTarget = clientsStructRefs[clientsStructRefsIds[^1]];
            while (nextStructsTarget.Refs.Count == nextStructsTarget.NextReadOperation)
            {
                clientsStructRefsIds.RemoveAt(clientsStructRefsIds.Count - 1);
                if (clientsStructRefsIds.Count > 0)
                {
                    nextStructsTarget = clientsStructRefs[clientsStructRefsIds[^1]];
                }
                else
                {
                    return null;
                }
            }

            return nextStructsTarget;
        }

        var curStructsTarget = getNextStructTarget();
        if (curStructsTarget == null)
        {
            return null;
        }

        var restStructs = new StructStore();
        var missingSv = new Dictionary<long, long>();

        void updateMissingSv(long client, long clock)
        {
            if (!missingSv.TryGetValue(client, out var mclock) || mclock > clock)
            {
                missingSv[client] = clock;
            }
        }

        var stackHead = curStructsTarget.Refs[curStructsTarget.NextReadOperation++];

        // Caching the state because it is used very often.
        var state = new Dictionary<long, long>();

        void addStackToRestSS()
        {
            foreach (var item in stack)
            {
                var client = item.Id.Client;
                if (clientsStructRefs.TryGetValue(client, out var inapplicableItems))
                {
                    // Decrement because we weren't able to apply previous operation.
                    inapplicableItems.NextReadOperation--;
                    restStructs.Clients[client] = inapplicableItems.Refs.GetRange(inapplicableItems.NextReadOperation, inapplicableItems.Refs.Count - inapplicableItems.NextReadOperation);
                    clientsStructRefs.Remove(client);
                    inapplicableItems.NextReadOperation = 0;
                    inapplicableItems.Refs = new List<AbstractStruct>();
                }
                else
                {
                    // Item was the last item on clientsStructRefs and the field was already cleared. Add item to restStructs and continue.
                    restStructs.Clients[client] = new List<AbstractStruct> { item };
                }

                // Remove client from clientsStructRefsIds to prevent users from applying the same update again.
                clientsStructRefsIds.RemoveAll(c => c == client);
            }

            stack.Clear();
        }

        // Iterate over all struct readers until we are done.
        while (true)
        {
            if (stackHead is not Skip)
            {
                if (!state.TryGetValue(stackHead.Id.Client, out var localClock))
                {
                    localClock = GetState(stackHead.Id.Client);
                    state[stackHead.Id.Client] = localClock;
                }

                var offset = localClock - stackHead.Id.Clock;
                if (offset < 0)
                {
                    // Update from the same client is missing.
                    stack.Add(stackHead);
                    updateMissingSv(stackHead.Id.Client, stackHead.Id.Clock - 1);

                    // Hit a dead wall, add all items from stack to restSS.
                    addStackToRestSS();
                }
                else
                {
                    var missing = stackHead.GetMissing(transaction, this);
                    if (missing != null)
                    {
                        stack.Add(stackHead);

                        // Get the struct reader that has the missing struct.
                        if (!clientsStructRefs.TryGetValue(missing.Value, out var structRefs))
                        {
                            structRefs = new ClientStructRefs();
                        }

                        if (structRefs.Refs.Count == structRefs.NextReadOperation)
                        {
                            // This update message causally depends on another update message that doesn't exist yet.
                            updateMissingSv(missing.Value, GetState(missing.Value));
                            addStackToRestSS();
                        }
                        else
                        {
                            stackHead = structRefs.Refs[structRefs.NextReadOperation++];
                            continue;
                        }
                    }
                    else if (offset == 0 || offset < stackHead.Length)
                    {
                        // All fine, apply the stackhead.
                        stackHead.Integrate(transaction, (int)offset);
                        state[stackHead.Id.Client] = stackHead.Id.Clock + stackHead.Length;
                    }
                }
            }

            // Iterate to next stackHead.
            if (stack.Count > 0)
            {
                stackHead = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            else if (curStructsTarget != null && curStructsTarget.NextReadOperation < curStructsTarget.Refs.Count)
            {
                stackHead = curStructsTarget.Refs[curStructsTarget.NextReadOperation++];
            }
            else
            {
                curStructsTarget = getNextStructTarget();
                if (curStructsTarget == null)
                {
                    // We are done!
                    break;
                }

                stackHead = curStructsTarget.Refs[curStructsTarget.NextReadOperation++];
            }
        }

        if (restStructs.Clients.Count > 0)
        {
            var encoder = new UpdateEncoderV2();
            EncodingUtils.WriteClientsStructs(encoder, restStructs, new Dictionary<long, long>());

            // Write an empty delete set: there is no need for an extra function call, just write 0 deletes.
            encoder.RestWriter.WriteVarUint(0);
            return new PendingStructs { Missing = missingSv, Update = encoder.ToArray() };
        }

        return null;
    }

    internal void TryResumePendingDeleteReaders(Transaction transaction)
    {
        var pendingReaders = _pendingDeleteReaders.ToArray();
        _pendingDeleteReaders.Clear();

        for (int i = 0; i < pendingReaders.Length; i++)
        {
            ReadAndApplyDeleteSet(pendingReaders[i], transaction);
        }
    }
}
