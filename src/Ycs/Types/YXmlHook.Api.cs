// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public sealed partial class YXmlHook
{
    public YXmlHook(string hookName)
    {
        HookName = hookName;
    }

    public string HookName { get; }

    public new YXmlHook Clone() => InternalClone() as YXmlHook;
}
