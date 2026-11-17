using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ycs;

public sealed partial class YXmlElement
{
    public YXmlElement(string nodeName)
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        NodeName = nodeName;
        _prelimAttrs = new Dictionary<string, object?>();
    }

    public string NodeName { get; }

    /// <summary>
    /// The next sibling in this element's parent, or null.
    /// </summary>
    public YType? NextSibling
    {
        get
        {
            var n = YXmlNodeHelpers.NextVisible(CurrentItem());
            return !n.IsNull ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    /// <summary>
    /// The previous sibling in this element's parent, or null.
    /// </summary>
    public YType? PrevSibling
    {
        get
        {
            var n = YXmlNodeHelpers.PrevVisible(CurrentItem());
            return !n.IsNull ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    public new YXmlElement Clone() => (YXmlElement)InternalClone();

    /// <summary>
    /// Removes an attribute from this element.
    /// </summary>
    public void RemoveAttribute(string attributeName)
    {
        ArgumentNullException.ThrowIfNull(attributeName);
        if (Doc != null)
        {
            Doc.Transact((self: this, attributeName), static (s, tr) => s.self.TypeMapDelete(tr, s.attributeName));
        }
        else
        {
            Debug.Assert(_prelimAttrs != null);
            _prelimAttrs.Remove(attributeName);
        }
    }

    /// <summary>
    /// Sets or updates an attribute; the element holds a copy of the value.
    /// </summary>
    public void SetAttribute(string attributeName, YValue attributeValue) => SetAttributeRaw(attributeName, attributeValue.Stored());

    /// <summary>
    /// Returns the value of the given attribute, or <see cref="YValue.Null"/> if it isn't set (<see cref="TryGetAttribute"/>
    /// tells it from an attribute set to null).
    /// </summary>
    public YValue GetAttribute(string attributeName) => TryGetAttribute(attributeName, out var value) ? value : YValue.Null;

    /// <summary>Reads the attribute <paramref name="attributeName"/>; false when it isn't set.</summary>
    public bool TryGetAttribute(string attributeName, out YValue value)
    {
        ArgumentNullException.ThrowIfNull(attributeName);
        object? stored;
        if (_prelimAttrs != null ? _prelimAttrs.TryGetValue(attributeName, out stored) : TryTypeMapGet(attributeName, out stored))
        {
            value = YValue.FromStored(stored);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Returns whether an attribute exists.
    /// </summary>
    public bool HasAttribute(string attributeName) =>
        _prelimAttrs != null ? _prelimAttrs.ContainsKey(attributeName) : TryGetMapItem(attributeName, out var item) && !item.Deleted;

    /// <summary>
    /// Returns all attribute name/value pairs (null values included), as of <paramref name="snapshot"/> when it is given
    /// (it only reads: an item merged across the snapshot's boundary is seen whole, as in Yjs).
    /// </summary>
    public IReadOnlyDictionary<string, YValue> GetAttributes(Snapshot? snapshot = null)
    {
        if (snapshot != null)
        {
            return MapAtSnapshot(snapshot);
        }

        var attributes = new Dictionary<string, YValue>();
        foreach (var kvp in EnumerateAttributes())
        {
            attributes[kvp.Key] = YValue.FromStored(kvp.Value);
        }

        return attributes;
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
            parts.Add($"{key}=\"{JsValue.ToJsString(attrs[key].Stored())}\"");
        }

        var nodeName = NodeName.ToLowerInvariant();
        var attrsString = parts.Count > 0 ? " " + string.Join(" ", parts) : string.Empty;
        return $"<{nodeName}{attrsString}>{base.ToString()}</{nodeName}>";
    }

    internal void SetAttributeRaw(string attributeName, object? stored)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, attributeName, stored), static (s, tr) => s.self.TypeMapSet(tr, s.attributeName, s.stored));
        }
        else
        {
            Debug.Assert(_prelimAttrs != null);
            _prelimAttrs[attributeName] = stored;
        }
    }

    private IEnumerable<KeyValuePair<string, object?>> EnumerateAttributes() => _prelimAttrs ?? TypeMapEnumerateValues();
}
