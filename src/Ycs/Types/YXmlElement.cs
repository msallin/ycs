// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// An YXmlElement imitates the behavior of a DOM Element:
/// * it has attributes (key/value pairs);
/// * it has child elements that must inherit from YXmlElement / YXmlText.
/// </summary>
public partial class YXmlElement : YXmlFragment
{
    internal const int YXmlElementRefId = 3;

    private Dictionary<string, object> _prelimAttrs;

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        foreach (var kvp in _prelimAttrs)
        {
            SetAttribute(kvp.Key, kvp.Value);
        }

        _prelimAttrs = null;
    }

    internal override AbstractType InternalCopy()
    {
        return new YXmlElement(NodeName);
    }

    internal override AbstractType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var el = new YXmlElement(NodeName);

        foreach (var kvp in GetAttributes())
        {
            el.SetAttribute(kvp.Key, kvp.Value);
        }

        var children = new List<object>();
        foreach (var v in ToArray())
        {
            children.Add(v is AbstractType at ? at.InternalClone() : v);
        }

        el.Insert(0, children);
        return el;
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YXmlElementRefId);
        encoder.WriteKey(NodeName);
    }

    internal static new YXmlElement Read(IUpdateDecoder decoder)
    {
        return new YXmlElement(decoder.ReadKey());
    }
}
