// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Globalization;
using System.Text;

namespace Ycs;

/// <summary>
/// Represents text in a Dom Element. In <see cref="ToString"/>, a formatting attribute (see
/// <see cref="YText"/>) whose value is an object is rendered as a nested tag with that object's keys
/// as XML attributes (a 'link' with value '{ href: "..." }' renders as '&lt;link href="..."&gt;'); one
/// with a boolean or number value (e.g. 'bold: true') renders as a tag without attributes.
/// </summary>
public class YXmlText : YText
{
    internal const int YXmlTextRefId = 6;

    // JS has no explicit constructor here: a derived class without one implicitly forwards its
    // arguments to 'super(...)', so 'new Y.XmlText(str)' still works (verified against real Yjs).
    // C# doesn't do that forwarding, so it's spelled out explicitly to match.
    public YXmlText()
    {
        // Do nothing.
    }

    public YXmlText(string str)
        : base(str)
    {
        // Do nothing.
    }

    /// <summary>
    /// The next sibling in this text node's parent, or null.
    /// </summary>
    public object NextSibling
    {
        get
        {
            var n = YXmlNodeHelpers.NextVisible(_item);
            return n != null ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    /// <summary>
    /// The previous sibling in this text node's parent, or null.
    /// </summary>
    public object PrevSibling
    {
        get
        {
            var n = YXmlNodeHelpers.PrevVisible(_item);
            return n != null ? YXmlNodeHelpers.ContentTypeOf(n) : null;
        }
    }

    internal override AbstractType InternalCopy()
    {
        return new YXmlText();
    }

    internal override AbstractType InternalClone()
    {
        var text = new YXmlText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    public new YXmlText Clone() => InternalClone() as YXmlText;

    /// <summary>
    /// Yjs' <c>toString</c>: each attribute of a run as a tag around it, whose XML attributes are the
    /// value's own properties as JavaScript's <c>for..in</c> lists them (a map's keys, a list's or a
    /// string's indexes, nothing for a number or a boolean), sorted; values and embeds are written
    /// as JavaScript's <c>String(value)</c> writes them.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();

        foreach (var delta in ToDelta())
        {
            var nestedNodes = new List<(string NodeName, List<(string Key, object Value)> Attrs)>();

            if (delta.Attributes != null)
            {
                foreach (var kvp in delta.Attributes)
                {
                    var attrs = new List<(string Key, object Value)>();
                    switch (kvp.Value)
                    {
                        case IDictionary<string, object> valueMap:
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

            sb.Append(JsValue.ToJsString(delta.Insert == Delta.NullEmbed ? null : delta.Insert));

            for (int i = nestedNodes.Count - 1; i >= 0; i--)
            {
                sb.Append("</").Append(nestedNodes[i].NodeName).Append('>');
            }
        }

        return sb.ToString();
    }

    public override object ToJson() => ToString();

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YXmlTextRefId);
    }

    internal static new YXmlText Read(IUpdateDecoder decoder)
    {
        return new YXmlText();
    }
}
