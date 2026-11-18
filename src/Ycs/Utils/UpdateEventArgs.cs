using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>
/// What <see cref="YDoc.Updated"/> and <see cref="YDoc.UpdatedV2"/> report: the change of one transaction as an
/// update. <see cref="Update"/> is backed by an array made for this event alone: the handler owns it, may keep it
/// and may pass it on without copying, and the document never writes to it again or reuses it for a later event.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The payload of an event, not an EventArgs subclass.")]
public readonly record struct UpdateEventArgs(ReadOnlyMemory<byte> Update, object? Origin, Transaction Transaction);

/// <summary>What <see cref="YDoc.SubdocsChanged"/> reports: the subdocuments a transaction loaded, added and removed.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The payload of an event, not an EventArgs subclass.")]
public readonly record struct SubdocsEventArgs(IReadOnlySet<YDoc> Loaded, IReadOnlySet<YDoc> Added, IReadOnlySet<YDoc> Removed);
