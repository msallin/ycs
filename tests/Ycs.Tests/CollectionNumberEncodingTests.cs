using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A number inserted as a <see cref="YValue"/> into an array or a map encodes as the CLR number it came from did: Yjs'
/// 'any' encoding writes an integer within 31 bits as a varint, a float32 or a float64 otherwise.
/// </summary>
[TestClass]
public class CollectionNumberEncodingTests
{
    public static IEnumerable<object[]> Numbers =>
    [
        [0], [1], [-1], [127], [128], [int.MaxValue], [-int.MaxValue], [int.MinValue],
        [2147483648L], [-2147483649L], [3_000_000_000L], [4294967296L], [16777217L], [9007199254740991L], [-9007199254740991L],
        [1.5], [0.1], [-0.0], [0.0], [double.NaN], [double.PositiveInfinity], [double.NegativeInfinity], [1e21], [1e300], [4294967296.5], [5.0], [2147483647.0], [2147483648.0],
    ];

    private static YValue ValueOf(object number) => number switch
    {
        int i => i,
        long l => l,
        double d => d,
        _ => throw new ArgumentException(null, nameof(number)),
    };

    private static bool Contains(byte[] haystack, byte[] needle, int times)
    {
        var found = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                found++;
                i += needle.Length - 1;
            }
        }

        return found == times;
    }

    [TestMethod]
    [DynamicData(nameof(Numbers))]
    public void Add_NumberAsYValue_EncodesAsTheClrNumberDid(object number)
    {
        var expected = new Lib0Writer();
        expected.WriteAny(number);
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetArray("a").Add(ValueOf(number));
        doc.GetArray("a").Insert(0, ValueOf(number));
        doc.GetMap("m")["k"] = ValueOf(number);

        // A run of two values in the array, and the value of the map.
        Assert.IsTrue(Contains(doc.EncodeStateAsUpdate(), expected.ToArray(), times: 3));
    }

    [TestMethod]
    [DynamicData(nameof(Numbers))]
    public void Read_NumberFromAnUpdate_IsANumberAndEncodesAgainTheSame(object number)
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetArray("a").Add(ValueOf(number));
        source.GetMap("m")["k"] = ValueOf(number);
        var update = source.EncodeStateAsUpdate();
        var copy = new YDoc(new YDocOptions { ClientId = 2 });

        copy.ApplyUpdate(update);

        var read = copy.GetArray("a")[0];
        Assert.AreEqual((YValueKind.Number, true), (read.Kind, read.Equals(ValueOf(number))));
        Assert.AreEqual(read, copy.GetMap("m")["k"]);
        CollectionAssert.AreEqual(update, copy.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Read_IntegersOfAnyWidth_ReadAsTheSameJavaScriptNumber()
    {
        var doc = new YDoc();
        doc.GetArray("a").Add(7, 2147483648L, 7.0, 1.5);

        Assert.AreEqual((7, 2147483648L, 7L, 1.5), (doc.GetArray("a")[0].GetInt32(), doc.GetArray("a")[1].GetInt64(), doc.GetArray("a")[2].GetInt64(), doc.GetArray("a")[3].GetDouble()));
    }
}
