# Ycs.ValueAlloc

Not a test: the bytes allocated (`GC.GetAllocatedBytesForCurrentThread`) by `ApplyUpdate`, `EncodeStateAsUpdate` and
`YArray.Add` of 20,000 objects `{a: int, b: [int, string, double], c: string}`, on a warmed process.

```bash
dotnet run -c Release --project bench/Ycs.ValueAlloc
```

Results (bytes, one run; deterministic apart from a few hundred):

| Operation | Before A3 (95b11eb) | First A3 version (list and dictionary wrappers) | A3, array-backed containers |
|---|---|---|---|
| decode | 12,564,344 | 13,844,568 | 11,123,784 |
| encode | 6,860,704 | 6,863,992 | 6,860,680 |
| insert | 6,767,224 | 14,607,224 | 5,967,224 |

The insert copies every list, object and byte array; it allocates less than before because the walk uses no
enumerators and a reused stack, and a frozen container takes its keys and values by `CopyTo`.
