using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

/// <summary>
/// Represents text in a Dom Element. In <see cref="ToString"/>, a formatting attribute (see
/// <see cref="YText"/>) whose value is an object is rendered as a nested tag with that object's keys
/// as XML attributes (a 'link' with value '{ href: "..." }' renders as '&lt;link href="..."&gt;'); one
/// with a boolean or number value (e.g. 'bold: true') renders as a tag without attributes.
/// </summary>
public sealed partial class YXmlText : YText
{
    internal const int YXmlTextRefId = 6;

    internal override YType InternalCopy()
    {
        return new YXmlText();
    }

    internal override YType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var text = new YXmlText();
        CloneContentInto(text);
        return text;
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YXmlTextRefId);
    }
}
