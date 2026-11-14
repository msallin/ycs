// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs
{
    /// <summary>
    /// You can manage binding to a custom (non-Xml) type with YXmlHook. It behaves exactly like a
    /// <see cref="YMap"/> (including which events it raises), plus a 'HookName'.
    /// </summary>
    public class YXmlHook : YMap
    {
        internal const int YXmlHookRefId = 5;

        public YXmlHook(string hookName)
        {
            HookName = hookName;
        }

        public string HookName { get; }

        internal override AbstractType InternalCopy()
        {
            return new YXmlHook(HookName);
        }

        internal override AbstractType InternalClone()
        {
            var el = new YXmlHook(HookName);
            foreach (var kvp in this)
            {
                el.Set(kvp.Key, kvp.Value);
            }

            return el;
        }

        public new YXmlHook Clone() => InternalClone() as YXmlHook;

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
}
