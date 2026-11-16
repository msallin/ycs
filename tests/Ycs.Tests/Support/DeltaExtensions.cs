using System.Collections.Generic;
using System.Linq;

namespace Ycs;

/// <summary>Reads of one operation of a delta, for tests that know which kind it is.</summary>
internal static class DeltaExtensions
{
    public static YValue Inserted(this Delta delta) => ((Delta.Insert)delta).Value;

    public static int Retained(this Delta delta) => ((Delta.Retain)delta).Length;

    public static int Deleted(this Delta delta) => ((Delta.Delete)delta).Length;

    /// <summary>The operation as Yjs' JSON has it: <c>insert</c> (a list for <see cref="Delta.InsertItems"/>), <c>retain</c> or <c>delete</c>, and <c>attributes</c> if any, as plain values.</summary>
    public static Dictionary<string, object> ToPlain(this Delta delta)
    {
        var plain = new Dictionary<string, object>();
        switch (delta)
        {
            case Delta.Insert insert:
                plain["insert"] = insert.Value.ToObject();
                break;
            case Delta.InsertItems items:
                plain["insert"] = items.Items.Select(item => item.ToObject()).ToList();
                break;
            case Delta.Retain retain:
                plain["retain"] = retain.Length;
                break;
            case Delta.Delete delete:
                plain["delete"] = delete.Length;
                break;
        }

        if (delta.Attributes != null)
        {
            plain["attributes"] = delta.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value.ToObject());
        }

        return plain;
    }
}
