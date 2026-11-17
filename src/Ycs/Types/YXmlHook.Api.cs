namespace Ycs;

public sealed partial class YXmlHook
{
    public YXmlHook(string hookName)
    {
        ArgumentNullException.ThrowIfNull(hookName);
        HookName = hookName;
    }

    public string HookName { get; }

    public new YXmlHook Clone() => (YXmlHook)InternalClone();
}
