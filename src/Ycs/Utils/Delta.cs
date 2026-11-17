#nullable enable

namespace Ycs;

/// <summary>
/// One operation of a delta (Yjs' <c>{ insert | retain | delete, attributes }</c>): what a text's
/// <see cref="YText.ToDelta()"/> and an event's <c>Delta</c> hold, and what <see cref="YText.ApplyDelta"/> takes. A closed set:
/// <see cref="Insert"/>, <see cref="InsertItems"/>, <see cref="Retain"/> and <see cref="Delete"/>.
/// </summary>
public abstract record Delta
{
    private protected Delta()
    {
    }

    /// <summary>
    /// The formatting attributes of the operation; null for none. On a <see cref="Retain"/> a null value removes the
    /// attribute from the range; on an <see cref="Insert"/> from <see cref="YText.ToDelta()"/> the attributes of the text,
    /// with <c>ychange</c> where a snapshot asked for it.
    /// </summary>
    public YAttributes? Attributes { get; init; }

    /// <summary>
    /// Text, or one embed (a nested shared type or a value) of a text. An embed whose value is null (Yjs'
    /// <c>{ insert: null }</c>, which only another peer can write) is <see cref="YValue.Null"/>, an embed of length 1 as
    /// every embed. An empty string inserts nothing.
    /// </summary>
    public sealed record Insert(YValue Value) : Delta;

    /// <summary>The items an array or an XML fragment gained, as its events' deltas list them.</summary>
    public sealed record InsertItems(IReadOnlyList<YValue> Items) : Delta
    {
        /// <summary>Equal when the attributes and the items, in order, are equal.</summary>
        public bool Equals(InsertItems? other) =>
            other is not null && Equals(Attributes, other.Attributes) && Items.SequenceEqual(other.Items);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Attributes);
            foreach (var item in Items)
            {
                hash.Add(item);
            }

            return hash.ToHashCode();
        }

        protected override bool PrintMembers(System.Text.StringBuilder builder)
        {
            base.PrintMembers(builder);
            builder.Append(", Items = [").Append(string.Join(", ", Items)).Append(']');
            return true;
        }
    }

    public sealed record Retain(int Length) : Delta;

    public sealed record Delete(int Length) : Delta;
}
