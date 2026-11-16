// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// You can manage binding to a custom (non-Xml) type with YXmlHook. It behaves exactly like a
/// <see cref="YMap"/> (including which events it raises), plus a 'HookName'.
/// </summary>
public sealed partial class YXmlHook : YMap
{
    internal const int YXmlHookRefId = 5;

    internal override YType InternalCopy()
    {
        return new YXmlHook(HookName);
    }

    internal override YType InternalClone()
    {
        var el = new YXmlHook(HookName);
        foreach (var kvp in EnumerateEntries())
        {
            el.SetRaw(kvp.Key, kvp.Value);
        }

        return el;
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YXmlHookRefId);
        encoder.WriteKey(HookName);
    }

    internal static new YXmlHook Read(IUpdateDecoder decoder)
    {
        return new YXmlHook(decoder.ReadKey());
    }
}
