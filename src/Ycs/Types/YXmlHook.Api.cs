// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

public sealed partial class YXmlHook
{
    public YXmlHook(string hookName)
    {
        HookName = hookName;
    }

    public string HookName { get; }

    public new YXmlHook Clone() => (YXmlHook)InternalClone();
}
