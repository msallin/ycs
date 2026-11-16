using System.Diagnostics.CodeAnalysis;

// The names mirror Yjs ('guid', the 'String' content kind) and are public API; renaming them is a protocol-visible break.
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the Yjs names; public API.", Scope = "member", Target = "~P:Ycs.YDoc.Guid")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the Yjs names; public API.", Scope = "member", Target = "~P:Ycs.YDocOptions.Guid")]
[assembly: SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors the Yjs content kind; public API.", Scope = "member", Target = "~F:Ycs.ItemKind.String")]
