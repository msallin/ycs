namespace Ycs;

/// <summary>
/// One step of <see cref="YEvent.Path"/>: the <paramref name="Key"/> of a map entry, or, when it is null, the
/// <paramref name="Index"/> of an element of a list (an array, a text, an XML fragment).
/// </summary>
public readonly record struct PathSegment(string? Key, int Index)
{
    /// <summary>Whether this step goes through a map key (<see cref="Key"/>) rather than a list index.</summary>
    public bool IsKey => Key != null;

    /// <summary>The key, or the index in invariant digits.</summary>
    public override string ToString() => Key ?? Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
