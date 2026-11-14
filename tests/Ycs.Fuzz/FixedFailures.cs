namespace Ycs.Fuzz;

/// <summary>
/// Violations the fuzzer found that are fixed (they were in <see cref="KnownFailures"/>): each must
/// now apply, or be rejected with the document unchanged. <see cref="PinnedCase.Signature"/> and
/// <see cref="PinnedCase.Reason"/> say how it failed before.
/// </summary>
internal static class FixedFailures
{
    public static readonly IReadOnlyList<PinnedCase> All =
    [
        new(
            "alloc-struct-count",
            "Allocation at EncodingUtils.ReadClientStructRefs",
            "ReadClientStructRefs passes the decoded struct count to new List<AbstractStruct>(count) before reading any struct.",
            Gc: true, V2: false,
            BaseState: "",
            Update: "AYCAgIAC",
            TextRoots: ["text"]),
        new(
            "alloc-struct-count-below-heap-limit",
            "Allocation under the heap limit",
            "The same unchecked struct count, small enough to fit under the heap limit: 2^24 structs, 128 MiB for 6 bytes.",
            Gc: true, V2: false,
            BaseState: "",
            Update: "AYCAgAg=",
            TextRoots: []),
        new(
            "alloc-any-length",
            "Allocation at ContentAny.Read",
            "ContentAny.Read passes the decoded length to new List<object>(length).",
            Gc: false, V2: false,
            BaseState: "",
            Update: "AQEAAEgAAP///3g=",
            TextRoots: []),
        new(
            "alloc-json-length",
            "Allocation at ContentJson.Read",
            "ContentJson.Read passes the decoded length to new List<object>(length).",
            Gc: false, V2: false,
            BaseState: "",
            Update: "N/cAAACiAACcz405",
            TextRoots: []),
        new(
            "nonatomic-truncated-delete-set",
            "NonAtomic: System.IO.EndOfStreamException at Lib0Reader.ReadByte",
            "The structs are integrated before the delete set is read; a truncated delete set leaves them in the document (here a corrupt one: reading it afterwards throws). Yjs' readUpdate integrates before it reads the delete set, too.",
            Gc: true, V2: false,
            BaseState: "",
            Update: "AwEAAAQBBAAAAAABAAIAAAQBBAAAAAAEAAAAAAAAAAEAcQA=",
            TextRoots: ["text"]),
        new(
            "nonatomic-truncated-v2",
            "NonAtomic: System.IO.EndOfStreamException at Lib0Reader.ReadByte",
            "A v2 update whose column decoders run dry after some structs were integrated leaves those structs in the document.",
            Gc: true, V2: true,
            BaseState: "AgEFAMgEAAQBAncEYzEuMX0LAgQACAEFYXJyYXkBfQGBBAAHAQQBAQc=",
            Update: "AAACBgQBDgABiAEAAAABAQEBAHcEcDJhn2Q=",
            TextRoots: []),
        new(
            "nonatomic-malformed-delete-set",
            "NonAtomic: System.IO.InvalidDataException at Lib0Reader.ReadVarUint",
            "A malformed var-uint in the delete set is found after the structs were integrated.",
            Gc: true, V2: false,
            BaseState: "AgIMAAQBBHRleHQE8J+RjYEMAQIDCwAEAQR0ZXh0AsOpRAsAGPCfkY3wn4+98J+YgPCfkY3wn4+98J2EnsQLAAwADPCfmIDwn5GN8J+PvQEMAQIC",
            Update: "AAELAgAB//////8A",
            TextRoots: ["text"]),
        new(
            "nonatomic-negative-delete-length",
            "NonAtomic: System.ArgumentOutOfRangeException at DSEncoderV2.WriteDsLength",
            "A delete range of length 0 that cannot be applied yet is kept as a pending delete set; re-encoding it, DSEncoderV2.WriteDsLength rejects the length, after the structs were integrated.",
            Gc: true, V2: false,
            BaseState: "AQQBAAQBBHRleHQCVGiEAQEBZcQBAQECAiBxhAECBHVpY2sA",
            Update: "AQEBCYQBAAABAAEAAA==",
            TextRoots: ["text"]),
        new(
            "nonatomic-find-index-out-of-range",
            "NonAtomic: System.ArgumentOutOfRangeException at StructStore.FindIndexSS",
            "StructStore.FindIndexSS indexes past the struct list for a clock the decoded structs do not cover, after some were integrated.",
            Gc: false, V2: false,
            BaseState: "",
            Update: "AQcKAAcBAQADAQAABgQAChEEAAAAACgAAAAFAAAAAAABdwEABwAAAAAAAAAAAAAAAAAAAAAA",
            TextRoots: []),
        new(
            "nonatomic-zero-length-struct",
            "NonAtomic: System.DivideByZeroException at StructStore.FindIndexSS",
            "A struct of length 0 makes StructStore.FindIndexSS divide by zero, after it was added to the store.",
            Gc: false, V2: false,
            BaseState: "",
            Update: "AQMAAAABAAAAAA==",
            TextRoots: []),
        new(
            "nonatomic-find-index-unexpected",
            "NonAtomic: System.Exception at StructStore.FindIndexSS",
            "StructStore.FindIndexSS throws its generic 'Unexpected' after some structs were integrated, and reading the document afterwards throws too.",
            Gc: true, V2: false,
            BaseState: "",
            Update: "AQEAAAEBAAAAAAA=",
            TextRoots: ["text"]),
        new(
            "nonatomic-clean-end-unexpected",
            "NonAtomic: System.Exception at StructStore.GetItemCleanEnd",
            "StructStore.GetItemCleanEnd throws its generic 'Unexpected' after some structs were integrated.",
            Gc: true, V2: true,
            BaseState: "",
            Update: "AAADAkEDAwAAAAEABwQBxAAEAIQkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQEAAAMBAAMAAQA=",
            TextRoots: ["text"]),
        new(
            "nonatomic-string-splice-out-of-range",
            "NonAtomic: System.ArgumentOutOfRangeException at ContentString.Splice",
            "ContentString.Splice (called from Item.SplitItem) is asked to split outside the string, after some structs were integrated.",
            Gc: true, V2: true,
            BaseState: "AwUDAMYBCQEKBWNvbG9yCSIjZmYwMDAwIsYBEwEUBWNvbG9yBG51bGxGASIEbGluaxYiaHR0cHM6Ly9leGFtcGxlLm9yZy8ihAEhBgoKCgoKCoYDCARsaW5rBG51bGwFAgDGAQUBBgZpdGFsaWMEdHJ1ZcYBFAEVBml0YWxpYwRudWxsRQEiFHsiaW1hZ2UiOiJsb2dvLnBuZyJ9xgEBAQIEbGluawRudWxsxgEoASkEbGluaxYiaHR0cHM6Ly9leGFtcGxlLm9yZy8iEQEABAEEdGV4dAJIZYQBAQNsbG+BAQQBgQEFA4QBCAFkhAEJBWVyZnVshAEOBSB3b3JshAETAWSEARQNIG9mIHJpY2ggdGV4dEYBAARib2xkBHRydWXGAQ4BDwRib2xkBG51bGzGAQQBBQl1bmRlcmxpbmUEdHJ1ZcYBJAEFBGJvbGQEbnVsbMQBJQEFAyB0aMQBKAEFA2VyZcYBKwEFCXVuZGVybGluZQRudWxsxgEsAQUEYm9sZAR0cnVlAQEBBQQ=",
            Update: "AAMAAwELA0EAAwEDAQAAAAAEGEQDAQMAAQIFxgDEAMYZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEFCn5+dxQAAAAAAAAAAAAAAAAAAAAAAAAAAHcA",
            TextRoots: ["text"]),
        new(
            "nonatomic-any-splice-out-of-range",
            "NonAtomic: System.ArgumentOutOfRangeException at ContentAny.Splice",
            "ContentAny.Splice is asked to split outside its values, after some structs were integrated.",
            Gc: false, V2: true,
            BaseState: "",
            Update: "AAACQQYGEhcAAAQIAQARBACEAScAAAAnAAgAKACoAERIPAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKCwoAAAAAAAAABQEBAAIAAgAABAEDAAABCgB3CgAAAAAAAAAAAAB4dwAAAAAA",
            TextRoots: ["text"]),
    ];
}
