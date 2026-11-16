using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>
/// What <see cref="YDoc.Updated"/> and <see cref="YDoc.UpdatedV2"/> report: the change of one transaction as an
/// update, a new array for each event that a handler may keep.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The payload of an event, not an EventArgs subclass.")]
public readonly record struct UpdateEventArgs(ReadOnlyMemory<byte> Update, object? Origin, Transaction Transaction);

/// <summary>What <see cref="YDoc.SubdocsChanged"/> reports: the subdocuments a transaction loaded, added and removed.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The payload of an event, not an EventArgs subclass.")]
public readonly record struct SubdocsEventArgs(IReadOnlySet<YDoc> Loaded, IReadOnlySet<YDoc> Added, IReadOnlySet<YDoc> Removed);
