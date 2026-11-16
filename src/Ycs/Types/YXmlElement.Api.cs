// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

public sealed partial class YXmlElement
{
    public YXmlElement()
        : this("UNDEFINED")
    {
        // Do nothing.
    }

    public YXmlElement(string nodeName)
    {
        NodeName = nodeName;
        _prelimAttrs = new Dictionary<string, object>();
    }

    public string NodeName { get; }

    /// <summary>
    /// The next sibling in this element's parent, or null.
    /// </summary>
    public object NextSibling
    {
        get
        {
            var n = YXmlNodeHelpers.NextVisible(_item);
            return !n.IsNull ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    /// <summary>
    /// The previous sibling in this element's parent, or null.
    /// </summary>
    public object PrevSibling
    {
        get
        {
            var n = YXmlNodeHelpers.PrevVisible(_item);
            return !n.IsNull ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    public new YXmlElement Clone() => InternalClone() as YXmlElement;

    /// <summary>
    /// Removes an attribute from this element.
    /// </summary>
    public void RemoveAttribute(string attributeName)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, attributeName), static (s, tr) => s.self.TypeMapDelete(tr, s.attributeName));
        }
        else
        {
            _prelimAttrs.Remove(attributeName);
        }
    }

    /// <summary>
    /// Sets or updates an attribute.
    /// </summary>
    public void SetAttribute(string attributeName, object attributeValue)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, attributeName, attributeValue), static (s, tr) => s.self.TypeMapSet(tr, s.attributeName, s.attributeValue));
        }
        else
        {
            _prelimAttrs[attributeName] = attributeValue;
        }
    }

    /// <summary>
    /// Returns the value of the given attribute, or null if it isn't set.
    /// </summary>
    public object GetAttribute(string attributeName) => TryTypeMapGet(attributeName, out var value) ? value : null;

    /// <summary>
    /// Returns whether an attribute exists.
    /// </summary>
    public bool HasAttribute(string attributeName) => _map.TryGetValue(attributeName, out var item) && !item.Deleted;

    /// <summary>
    /// Returns all attribute name/value pairs (null values included), optionally as of a snapshot.
    /// </summary>
    public IDictionary<string, object> GetAttributes(Snapshot snapshot = null)
    {
        if (snapshot == null)
        {
            return TypeMapEnumerateValues().ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        return GetAllSnapshot(snapshot);
    }

    /// <summary>
    /// The XML serialization of this element: '&lt;nodename attr="value"&gt;...children...&lt;/nodename&gt;'.
    /// Attributes are ordered by attribute name, so this can be used to compare two elements; values are
    /// written as JavaScript's <c>String(value)</c> writes them, as in Yjs.
    /// </summary>
    public override string ToString()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var attrs = GetAttributes();
        var keys = attrs.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        var parts = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            parts.Add($"{key}=\"{JsValue.ToJsString(attrs[key])}\"");
        }

        var nodeName = NodeName.ToLowerInvariant();
        var attrsString = parts.Count > 0 ? " " + string.Join(" ", parts) : string.Empty;
        return $"<{nodeName}{attrsString}>{base.ToString()}</{nodeName}>";
    }
}
