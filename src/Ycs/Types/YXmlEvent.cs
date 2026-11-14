// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// An event that describes changes on a YXmlElement or YXmlFragment.
/// </summary>
public class YXmlEvent : YEvent
{
    /// <summary>
    /// Whether the children changed.
    /// </summary>
    public bool ChildListChanged { get; private set; }

    /// <summary>
    /// The set of all changed attributes.
    /// </summary>
    public ISet<string> AttributesChanged { get; } = new HashSet<string>();

    internal YXmlEvent(AbstractType target, ISet<string> subs, Transaction transaction)
        : base(target, transaction)
    {
        if (subs != null)
        {
            foreach (var sub in subs)
            {
                if (sub == null)
                {
                    ChildListChanged = true;
                }
                else
                {
                    AttributesChanged.Add(sub);
                }
            }
        }
    }
}
