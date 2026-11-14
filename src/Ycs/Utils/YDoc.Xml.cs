// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs
{
    public partial class YDoc
    {
        /// <summary>Yjs: <c>doc.getXmlFragment(name)</c>.</summary>
        public YXmlFragment GetXmlFragment(string name = "") => Get<YXmlFragment>(name);

        /// <summary>Yjs: <c>doc.getXmlElement(name)</c>.</summary>
        public YXmlElement GetXmlElement(string name = "") => Get<YXmlElement>(name);

        /// <summary>Yjs: <c>doc.getXmlText(name)</c>.</summary>
        public YXmlText GetXmlText(string name = "") => Get<YXmlText>(name);
    }
}
