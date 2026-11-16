# Getting started

## Install

Ycs is not on NuGet. It targets .NET 11, so it needs the .NET 11 SDK. Clone the repository (or add it as a git
submodule) and reference the project:

```xml
<ProjectReference Include="path/to/ycs/src/Ycs/Ycs.csproj" />
```

Ycs has no dependency beyond the .NET runtime. Everything is in the namespace `Ycs`.

## The model in one paragraph

A `YDoc` holds named shared types: `YText`, `YMap`, `YArray` and the XML types. Every change to a document produces
an *update*, a few bytes that any other copy of the document can apply, in any order and any number of times. Copies
that have applied the same updates have the same content, whoever made the changes and whenever they arrive. How the
updates travel (WebSocket, a database, a file) is up to you; [Networking](networking.md) shows the standard way.

## Shared types and values

```csharp
using Ycs;

var doc = new YDoc();

// Text, with formatting.
var text = doc.GetText("notes");
text.Insert(0, "Hello world");
text.Format(0, 5, new Dictionary<string, YValue> { ["bold"] = true });
foreach (var d in text.ToDelta())
{
    Console.WriteLine($"{((Delta.Insert)d).Value} {(d.Attributes is null ? "" : string.Join(",", d.Attributes.Keys))}");
}
// Hello bold
//  world

// A map, holding a nested array.
var settings = doc.GetMap("settings");
settings["theme"] = "dark";
settings["fontSize"] = 14;
var tags = new YArray();
settings["tags"] = tags;
tags.Add("draft", "review");

Console.WriteLine(settings["theme"]);               // dark
Console.WriteLine(tags.Count);                      // 2
Console.WriteLine(string.Join(", ", tags[0..2]));   // draft, review
```

- `doc.GetText(name)`, `GetMap`, `GetArray` and the XML getters return the root type of that name, created on first
  use. Peers that use the same name and type share it.
- Values are `YValue`s: `null`, `bool`, a number, `BigInteger`, `string`, `byte[]`, arrays and objects of those, and
  shared types. The implicit conversions cover the scalars, `YValue.FromObject` the CLR lists and dictionaries.
  Anything else is rejected when it is converted. Read one back with `GetString()`, `TryGetInt32(out var n)`, `Kind` and
  the like.
- Indexes into a text count UTF-16 code units, as in Yjs and in .NET strings.
- `YMap` and `YArray` read as a dictionary and a list of `YValue`s: the indexer of a map throws for a missing key and
  `TryGetValue` tells a missing key from a null value; the indexer of an array throws for an index out of range.

## Events and transactions

```csharp
var doc = new YDoc();
var text = doc.GetText("content");

using var subscription = text.Observe(e =>
{
    foreach (var d in e.Delta)
    {
        Console.WriteLine(d switch
        {
            Delta.Insert i => $"insert {i.Value}",
            Delta.Retain r => $"retain {r.Length}",
            Delta.Delete x => $"delete {x.Length}",
            _ => d.ToString(),
        });
    }
});

text.Insert(0, "abc"); // insert abc

// One transaction: one event, one Updated, one undo step. The origin is passed to every handler.
doc.Transact(tr =>
{
    text.Remove(0, 1);
    text.Insert(2, "d");
}, origin: "my-editor");
// delete 1
// retain 2
// insert d
```

- Every change runs in a transaction; without `Transact`, each call is its own.
- Observers run synchronously, on the thread that made the change, after the transaction.
- `Observe` (typed by the shared type: `Action<YArrayEvent>`, `YMapEvent`, `YTextEvent`, `YXmlEvent`) and `ObserveDeep`
  (the type and everything nested in it) return an `IDisposable` that unsubscribes. The document raises `Updated`,
  `UpdatedV2` and the other transaction events as .NET events.
- An event and its transaction are valid only during the handler: compute what you need there and keep that.

## Encode and apply an update

The state of a document is one update. Apply it to another document and the two have the same content:

```csharp
var source = new YDoc();
source.GetText("content").Insert(0, "Saved text");
byte[] update = source.EncodeStateAsUpdate();

var copy = new YDoc();
copy.ApplyUpdate(update);
Console.WriteLine(copy.GetText("content")); // Saved text
```

This is also how to save and load: write the bytes to a file or a database and apply them to a new document. A document's
own stored state can be larger than the defaults allow for a peer's update; apply it with
`copy.ApplyUpdate(update, limits: UpdateLimits.Unlimited)` ([Untrusted input and threads](untrusted-input.md#trusted-input)).
To keep a log instead, append each update as it happens and compact the log now and then with `YUpdate.Merge`, which
needs no document. `UpdateFormat.V2` (a `format` argument) is the denser encoding; v1 is what Yjs providers speak.

## Two documents in sync

Each local change raises `Updated` with the bytes to send to the other replicas:

```csharp
var alice = new YDoc();
var bob = new YDoc();

alice.Updated += (_, e) => { if (e.Origin != bob) bob.ApplyUpdate(e.Update.Span, alice); };
bob.Updated += (_, e) => { if (e.Origin != alice) alice.ApplyUpdate(e.Update.Span, bob); };

alice.GetText("content").Insert(0, "Hello");
bob.GetText("content").Insert(5, ", world");

Console.WriteLine(alice.GetText("content")); // Hello, world
Console.WriteLine(bob.GetText("content"));   // Hello, world
```

The second argument of `ApplyUpdate` is the *origin*: it is passed to every handler of the change, so a handler can
tell its own changes from those it received and does not send an update back where it came from.

### Catching up after being offline

Instead of sending every update as it happens, two documents can exchange their *state vectors* (what each has
seen) and send each other only what the other lacks:

```csharp
var alice = new YDoc();
var bob = new YDoc();

// Both edit offline.
alice.GetText("content").Insert(0, "Alice was here. ");
bob.GetText("content").Insert(0, "Bob too. ");

// Exchange state vectors, then send each other only what the other lacks.
byte[] aliceState = alice.EncodeStateVector();
byte[] bobState = bob.EncodeStateVector();
byte[] forBob = alice.EncodeStateAsUpdate(bobState);
byte[] forAlice = bob.EncodeStateAsUpdate(aliceState);
bob.ApplyUpdate(forBob);
alice.ApplyUpdate(forAlice);

Console.WriteLine(alice.GetText("content").ToString() == bob.GetText("content").ToString()); // True
```

This is what the y-protocols sync does on every connect ([Networking](networking.md)); `SyncProtocol` writes and reads
those messages.

## Undo

```csharp
var doc = new YDoc();
var text = doc.GetText("content");
var undo = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });

text.Insert(0, "one");
text.Insert(3, " two");
undo.Undo();
Console.WriteLine(text); // one
undo.Redo();
Console.WriteLine(text); // one two
```

The `UndoManager` tracks changes to its scope made in transactions whose origin is in `TrackedOrigins` (by default:
no origin). Apply peers' updates with an origin, as above, and it never undoes another user's edits. Changes within
`CaptureTimeout` (500 ms by default, read from `UndoManagerOptions.TimeProvider`) form one step. `StopCapturing()`
ends the current step. The manager is `IDisposable`: dispose it to unhook it from the document.

`new UndoManager(type)`, `new UndoManager(types)` and `new UndoManager(doc)` choose the scope: a type with its children, a
list of types, or every change of the document. `CanUndo` and `CanRedo` are properties; `Undo()` and `Redo()` return the
stack item, or null when there was nothing to do.

## Updates from others

An update from a peer may be malformed. `ApplyUpdate` then throws `YUpdateException` and changes nothing;
`TryApplyUpdate` returns false instead:

```csharp
var doc = new YDoc();
byte[] fromPeer = [0, 1, 2, 3, 4];

if (!doc.TryApplyUpdate(fromPeer))
{
    Console.WriteLine("rejected; the document is unchanged");
}
```

What a hostile update can cost, and the limits that bound it: [Untrusted input and threads](untrusted-input.md).

## Next

- [Networking](networking.md): connect to Yjs clients over WebSocket or SignalR.
- [API reference](api.md): every type and contract.
- Two runnable samples: `samples/Ycs.AotSample` (a Native AOT console app that reads an update written by Yjs) and
  `samples/Ycs.WasmSample` (the same program on `browser-wasm`, run in Node).
