# Ycs.ValueAlloc

Not a test: the bytes allocated (`GC.GetAllocatedBytesForCurrentThread`) by `ApplyUpdate`, `EncodeStateAsUpdate` and
`YArray.Add` of 20,000 objects `{a: int, b: [int, string, double], c: string}`, on a warmed process.

```bash
dotnet run -c Release --project bench/Ycs.ValueAlloc
```

Results (bytes, one run; deterministic apart from a few hundred), 2026-11-16:

| Operation | Allocated |
|---|---|
| decode | 11,124,960 |
| encode | 6,860,680 |
| insert | 688,440 |

The objects are converted to `YValue`s before the insert is measured, so the insert figure is `YArray.Add` of 20,000
ready `YValue`s.
