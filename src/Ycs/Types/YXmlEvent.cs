// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// An event that describes changes on a YXmlElement or YXmlFragment.
/// </summary>
public sealed class YXmlEvent : YEvent
{
    private readonly ISet<string?>? _subs;
    private IReadOnlySet<string>? _attributesChanged;

    internal YXmlEvent(YType target, ISet<string?>? subs, Transaction transaction)
        : base(target, transaction)
    {
        _subs = subs;
    }

    /// <summary>
    /// Whether the children changed.
    /// </summary>
    public bool ChildListChanged => _subs?.Contains(null) ?? false;

    /// <summary>
    /// The set of all changed attributes.
    /// </summary>
    public IReadOnlySet<string> AttributesChanged => _attributesChanged ??= ChangedKeys.WithoutChildList(_subs);
}
