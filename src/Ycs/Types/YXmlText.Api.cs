// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable enable

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

public sealed partial class YXmlText
{
    // JS has no explicit constructor here: a derived class without one implicitly forwards its
    // arguments to 'super(...)', so 'new Y.XmlText(str)' still works (verified against real Yjs).
    // C# doesn't do that forwarding, so it's spelled out explicitly to match.
    public YXmlText(string? text = null)
        : base(text)
    {
        // Do nothing.
    }

    /// <summary>
    /// The next sibling in this text node's parent, or null.
    /// </summary>
    public YType? NextSibling
    {
        get
        {
            var n = YXmlNodeHelpers.NextVisible(_item);
            return !n.IsNull ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    /// <summary>
    /// The previous sibling in this text node's parent, or null.
    /// </summary>
    public YType? PrevSibling
    {
        get
        {
            var n = YXmlNodeHelpers.PrevVisible(_item);
            return !n.IsNull ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    public new YXmlText Clone() => (YXmlText)InternalClone();

    /// <summary>
    /// Yjs' <c>toString</c>: each attribute of a run as a tag around it, whose XML attributes are the
    /// value's own properties as JavaScript's <c>for..in</c> lists them (a map's keys, a list's or a
    /// string's indexes, nothing for a number or a boolean), sorted; values and embeds are written
    /// as JavaScript's <c>String(value)</c> writes them.
    /// </summary>
    public override string ToString()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var sb = new StringBuilder();

        foreach (var delta in ToDelta())
        {
            var nestedNodes = new List<(string NodeName, List<(string Key, object? Value)> Attrs)>();

            if (delta.Attributes != null)
            {
                foreach (var kvp in delta.Attributes)
                {
                    var attrs = new List<(string Key, object? Value)>();
                    switch (kvp.Value.Stored())
                    {
                        case IDictionary<string, object?> valueMap:
                            foreach (var akv in valueMap)
                            {
                                attrs.Add((akv.Key, akv.Value));
                            }

                            break;
                        case string str:
                            for (var i = 0; i < str.Length; i++)
                            {
                                attrs.Add((i.ToString(CultureInfo.InvariantCulture), str[i].ToString()));
                            }

                            break;
                        case System.Collections.IList list:
                            for (var i = 0; i < list.Count; i++)
                            {
                                attrs.Add((i.ToString(CultureInfo.InvariantCulture), list[i]));
                            }

                            break;
                    }

                    attrs.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                    nestedNodes.Add((kvp.Key, attrs));
                }
            }

            // Sort node order to get a unique order.
            nestedNodes.Sort((a, b) => string.CompareOrdinal(a.NodeName, b.NodeName));

            foreach (var node in nestedNodes)
            {
                sb.Append('<').Append(node.NodeName);
                foreach (var attr in node.Attrs)
                {
                    sb.Append(' ').Append(attr.Key).Append("=\"").Append(JsValue.ToJsString(attr.Value)).Append('"');
                }

                sb.Append('>');
            }

            sb.Append(JsValue.ToJsString(((Delta.Insert)delta).Value.Stored()));

            for (int i = nestedNodes.Count - 1; i >= 0; i--)
            {
                sb.Append("</").Append(nestedNodes[i].NodeName).Append('>');
            }
        }

        return sb.ToString();
    }

    public override object ToJson() => ToString();
}
