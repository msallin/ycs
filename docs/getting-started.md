# Getting started

## Install

Ycs is not on NuGet. It targets .NET 11, so it needs the .NET 11 SDK. Two ways to use it:

- Clone the repository (or add it as a git submodule) and reference the project:

  ```xml
  <ProjectReference Include="path/to/ycs/src/Ycs/Ycs.csproj" />
  ```

- Or build a package and add it from a local folder:

  ```bash
  dotnet pack src/Ycs -c Release -o ./packages
  dotnet add package Ycs --version 0.0.1 --source ./packages
  ```

Ycs has no dependency beyond the .NET runtime. Everything is in the namespace `Ycs`.

## The model in one paragraph

A `YDoc` holds named shared types: `YText`, `YMap`, `YArray` and the XML types. Every change to a document produces
an *update*, a few bytes that any other copy of the document can apply, in any order and any number of times. Copies
that have applied the same updates have the same content, whoever made the changes and whenever they arrive. How the
updates travel (WebSocket, a database, a file) is up to you; [Networking](networking.md) shows the standard way.

## Two documents in sync

```csharp
using Ycs;

var alice = new YDoc();
var bob = new YDoc();

// Each local change raises Updated with the bytes to send to the other replicas.
alice.Updated += (_, e) => { if (e.Origin != bob) bob.ApplyUpdate(e.Update.ToArray(), alice); };
bob.Updated += (_, e) => { if (e.Origin != alice) alice.ApplyUpdate(e.Update.ToArray(), bob); };

alice.GetText("content").Insert(0, "Hello");
bob.GetText("content").Insert(5, ", world");

Console.WriteLine(alice.GetText("content")); // Hello, world
Console.WriteLine(bob.GetText("content"));   // Hello, world
```

The second argument of `ApplyUpdate` is the *origin*: it is passed to every handler of the change, so a handler can
tell its own changes from those it received and does not send an update back where it came from.

## Shared types

```csharp
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
settings.Set("theme", "dark");
settings.Set("fontSize", 14);
var tags = new YArray();
settings.Set("tags", tags);
tags.Add(["draft", "review"]);

Console.WriteLine(settings.Get("theme"));           // dark
Console.WriteLine(tags.Length);                     // 2
Console.WriteLine(string.Join(", ", tags.Slice())); // draft, review
```

- `doc.GetText(name)`, `GetMap`, `GetArray` and `Get<T>` return the root type of that name, created on first use.
  Peers that use the same name and type share it.
- Values are `null`, `bool`, `int`, `long`, `double`, `BigInteger`, `string`, `byte[]`, lists and string-keyed
  dictionaries of those, and shared types. Anything else is rejected when it is inserted.
- Indexes into a text count UTF-16 code units, as in Yjs and in .NET strings.
- `YMap.Get` returns null for a missing key; `TryGet` tells a missing key from a null value.

## Catching up after being offline

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

This is what the y-protocols sync does on every connect ([Networking](networking.md)).

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
- `Observe` (typed by the shared type: `Action<YArrayEvent>`, `YMapEvent`, `YTextEvent`, `YXmlEvent`) and `ObserveDeep` (the type and
  everything nested in it) return an `IDisposable` that unsubscribes. The document raises `Updated`, `UpdatedV2` and the
  other transaction events as .NET events.
- An event and its transaction are valid only during the handler: compute what you need there and keep that.

## Undo

```csharp
var doc = new YDoc();
var text = doc.GetText("content");
var undo = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });

text.Insert(0, "one");
text.Insert(3, " two");
undo.Undo();
Console.WriteLine(text); // one
undo.Redo();
Console.WriteLine(text); // one two
```

The `UndoManager` tracks changes to its scope made in transactions whose origin is in `TrackedOrigins` (by default:
no origin). Apply peers' updates with an origin, as above, and it never undoes another user's edits. Changes within
`CaptureTimeout` (500 ms by default) form one step.

## Saving and loading

The state of a document is one update. Store it and apply it to a new document:

```csharp
var path = Path.Combine(Path.GetTempPath(), "notes.ydoc");

var doc = new YDoc();
doc.GetText("content").Insert(0, "Saved text");
File.WriteAllBytes(path, doc.EncodeStateAsUpdate());

// The file is your own state: apply it without the limits meant for peers.
var restored = new YDoc();
restored.ApplyUpdate(File.ReadAllBytes(path), null, limits: UpdateLimits.Unlimited);
Console.WriteLine(restored.GetText("content")); // Saved text
```

To keep a log instead, append each update as it happens and compact the log now and then with
`YUpdate.Merge`, which needs no document.

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
- [API reference](api.md): every type and contract, compared with Yjs.
- Two runnable samples: `samples/Ycs.AotSample` (a Native AOT console app that reads an update written by Yjs) and
  `samples/Ycs.WasmSample` (the same program on `browser-wasm`, run in Node).
