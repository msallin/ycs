namespace Ycs;

public sealed partial class YDoc
{
    /// <summary>The JSON of this document as Yjs' <c>Doc.toJSON()</c>: each root type by name; a root nobody defined yet is left out.</summary>
    internal List<KeyValuePair<string, object?>> JsonRoots()
    {
        var roots = new List<KeyValuePair<string, object?>>(_share?.Count ?? 0);
        foreach (var (name, type) in _share ?? [])
        {
            if (type is not YTypePlaceholder)
            {
                roots.Add(new(name, type.ToJsonTree()));
            }
        }

        return roots;
    }
}
