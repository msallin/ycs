namespace Ycs;

public partial class YDoc
{
    /// <summary>Yjs: <c>doc.getXmlFragment(name)</c>.</summary>
    public YXmlFragment GetXmlFragment(string name = "") => Get<YXmlFragment>(name);

    /// <summary>Yjs: <c>doc.getXmlText(name)</c>.</summary>
    public YXmlText GetXmlText(string name = "") => Get<YXmlText>(name);
}
