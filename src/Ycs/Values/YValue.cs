using System.Collections;
using System.Globalization;
using System.Numerics;

namespace Ycs;

/// <summary>
/// A value a shared type holds, of the closed set a Yjs document can store: null, a boolean, a number, a big integer, a
/// string, bytes, an array, an object (string keys), a shared type or a subdocument. Immutable, like
/// <see cref="System.Text.Json.JsonElement"/>: arrays, objects and bytes are copied in, and
/// nothing a caller holds changes what a document holds.
/// </summary>
/// <remarks>
/// Numbers follow JavaScript: there is one kind, a double. <see cref="TryGetInt64"/> and <see cref="TryGetInt32"/> succeed
/// for an integral number within ±2^53 (within the range of an <see cref="int"/>); <see cref="TryGetDouble"/> for every number,
/// whichever CLR type or wire form (integer, float32, float64) it came from. Equality is by value, JavaScript's way for numbers
/// (so <c>0</c> and <c>-0</c> are equal, <c>NaN</c> equals itself) and the order of an object's keys does not matter.
/// </remarks>
public readonly struct YValue : IEquatable<YValue>
{
    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;

    private const double MaxExactInteger = 9007199254740992.0; // 2^53

    // Null: nothing. Boolean: _bits 0 or 1. Number: the bits of the double. BigInteger: the value. Other kinds: _object only.
    private readonly object? _object;
    private readonly long _bits;
    private readonly YValueKind _kind;

    private YValue(YValueKind kind, object? o, long bits)
    {
        _kind = kind;
        _object = o;
        _bits = bits;
    }

    public YValueKind Kind => _kind;

    public bool IsNull => _kind == YValueKind.Null;

    public static YValue Null => default;

    public static YValue True { get; } = new(YValueKind.Boolean, null, 1);

    public static YValue False { get; } = new(YValueKind.Boolean, null, 0);

    public static YValue Bytes(ReadOnlySpan<byte> bytes) => new(YValueKind.Bytes, bytes.ToArray(), 0);

    /// <summary>An array of <paramref name="items"/>. A shared type or a subdocument is not a value of an array: the encoding has no form for it.</summary>
    /// <exception cref="ArgumentException">An item is a shared type or a subdocument.</exception>
    public static YValue Array(params ReadOnlySpan<YValue> items)
    {
        var list = new FrozenList(items.Length);
        foreach (var item in items)
        {
            list.Append(Contained(item));
            Raise(list, item);
        }

        return new YValue(YValueKind.Array, list, 0);
    }

#pragma warning disable CA1720 // The name of the kind and of the factory, as in JsonElement.
    /// <summary>An object of <paramref name="members"/>, in this order; a repeated key keeps its first place and the last value.</summary>
    /// <exception cref="ArgumentException">A value is a shared type or a subdocument.</exception>
    /// <exception cref="ArgumentNullException">A key is null.</exception>
    public static YValue Object(params ReadOnlySpan<KeyValuePair<string, YValue>> members)
    {
        var map = new FrozenMap(members.Length);
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member.Key, nameof(members));
            map.Set(member.Key, Contained(member.Value));
            Raise(map, member.Value);
        }

        return new YValue(YValueKind.Object, map, 0);
    }
#pragma warning restore CA1720

    /// <summary>
    /// The value of a CLR object: null, a <see cref="YValue"/>, a <see cref="bool"/>, a number of any built-in type, a
    /// <see cref="BigInteger"/>, a <see cref="string"/>, a <c>byte[]</c> (copied), a list (any <see cref="IList"/>), a dictionary
    /// with string keys (<see cref="IDictionary{TKey, TValue}"/>, <see cref="IReadOnlyDictionary{TKey, TValue}"/> or
    /// <see cref="IDictionary"/>), a shared type or a subdocument; lists and dictionaries are copied, with whatever they hold.
    /// </summary>
    /// <exception cref="ArgumentException">The value, or something in it, is of another type, is nested deeper than
    /// <see cref="UpdateLimits.DefaultMaxDepth"/> or contains itself, or an integer lies beyond ±(2^53 - 1) or a big
    /// integer beyond 64 bits (<see cref="ArgumentOutOfRangeException"/>).</exception>
    public static YValue FromObject(object? value)
    {
        switch (value)
        {
            case YValue same:
                return same;
            case AbstractType or YDoc:
                return FromStored(value);
        }

        try
        {
            return FromStored(ValueFreezer.Freeze(value));
        }
        catch (NotSupportedException e)
        {
            throw new ArgumentException(e.Message, nameof(value), e);
        }
    }

    /// <summary>
    /// The value as a CLR object, the types a document decodes to: null, <see cref="bool"/>, <see cref="int"/> for an
    /// integral number in its range (<see cref="long"/> within ±2^53, otherwise <see cref="double"/>), <see cref="BigInteger"/>,
    /// <see cref="string"/>, a copy of the bytes as <c>byte[]</c>, an array as a read-only <see cref="IList{T}"/> of
    /// objects, an object as a read-only <see cref="IDictionary{TKey, TValue}"/> in key order, the shared type or the
    /// subdocument itself.
    /// </summary>
    public object? ToObject() => _kind switch
    {
        YValueKind.Null => null,
        YValueKind.Boolean => _bits != 0,
        YValueKind.Number => NumberObject(BitConverter.Int64BitsToDouble(_bits)),
        YValueKind.BigInteger => new BigInteger(_bits),
        YValueKind.Bytes => ((byte[])_object!).Clone(),
        _ => _object,
    };

    /// <summary>The value of something a document stores (a scalar, a frozen container, a type, a subdocument), without a copy; for the code that reads documents.</summary>
    internal static YValue FromStored(object? stored)
    {
        switch (stored)
        {
            case null:
                return default;
            case bool b:
                return b ? True : False;
            case string s:
                return new YValue(YValueKind.String, s, 0);
            case int i:
                return Number(i);
            case double d:
                return Number(d);
            case long l:
                return Number(l);
            case float f:
                return Number(f);
            case short sh:
                return Number(sh);
            case sbyte sb:
                return Number(sb);
            case byte ub:
                return Number(ub);
            case ushort us:
                return Number(us);
            case uint ui:
                return Number(ui);
            case ulong ul:
                return Number(ul);
            case BigInteger bi:
                return new YValue(YValueKind.BigInteger, null, (long)bi);
            case byte[] bytes:
                return new YValue(YValueKind.Bytes, bytes, 0);
            case FrozenList list:
                return new YValue(YValueKind.Array, list, 0);
            case FrozenMap map:
                return new YValue(YValueKind.Object, map, 0);
            case AbstractType type:
                return new YValue(YValueKind.SharedType, type, 0);
            case YDoc doc:
                return new YValue(YValueKind.Document, doc, 0);
            default:
                if (ReferenceEquals(stored, JsValue.Undefined))
                {
                    return default;
                }

                throw AnyEncoding.Unsupported(stored);
        }
    }

    private static YValue Number(double d) => new(YValueKind.Number, null, BitConverter.DoubleToInt64Bits(d));

    private static object NumberObject(double d)
    {
        object boxed = d;
        if (Math.Floor(d) == d && !double.IsInfinity(d) && !(d == 0 && double.IsNegative(d)))
        {
            if (d >= int.MinValue && d <= int.MaxValue)
            {
                boxed = (int)d;
            }
            else if (Math.Abs(d) <= MaxExactInteger)
            {
                boxed = (long)d;
            }
        }

        return boxed;
    }

    private static object? Contained(YValue item) =>
        item._kind is YValueKind.SharedType or YValueKind.Document
            ? throw new ArgumentException("A shared type or a subdocument cannot be a member of an array or an object value.", nameof(item))
            : item.Stored();

    /// <summary>What a document stores for this value, without a copy (a byte array stays private to the value).</summary>
    internal object? Stored() => _kind switch
    {
        YValueKind.Null => null,
        YValueKind.Boolean => _bits != 0 ? BoxedTrue : BoxedFalse,
        YValueKind.Number => NumberObject(BitConverter.Int64BitsToDouble(_bits)),
        YValueKind.BigInteger => new BigInteger(_bits),
        _ => _object,
    };

    private static void Raise(object container, YValue item)
    {
        var depth = item._object switch
        {
            FrozenList l => l.Depth + 1,
            FrozenMap m => m.Depth + 1,
            _ => 1,
        };
        if (container is FrozenList list)
        {
            list.Depth = Math.Max(list.Depth, depth);
        }
        else
        {
            var map = (FrozenMap)container;
            map.Depth = Math.Max(map.Depth, depth);
        }
    }

    public static implicit operator YValue(bool value) => value ? True : False;

    public static implicit operator YValue(int value) => Number(value);

    /// <exception cref="ArgumentOutOfRangeException">The integer lies beyond ±(2^53 - 1): a JavaScript number cannot hold it exactly; use a <see cref="BigInteger"/>.</exception>
    public static implicit operator YValue(long value) =>
        value >= -JsValue.MaxSafeInteger && value <= JsValue.MaxSafeInteger ? Number(value) : throw AnyEncoding.OutOfRange(value);

    public static implicit operator YValue(double value) => Number(value);

    /// <exception cref="ArgumentOutOfRangeException">The value does not fit 64 bits.</exception>
    public static implicit operator YValue(BigInteger value) =>
        value >= long.MinValue && value <= long.MaxValue
            ? new YValue(YValueKind.BigInteger, null, (long)value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, "A BigInt value must fit into 64 bits.");

    public static implicit operator YValue(string? value) => value == null ? default : new YValue(YValueKind.String, value, 0);

    /// <summary>The bytes are copied.</summary>
    public static implicit operator YValue(byte[]? value) => value == null ? default : new YValue(YValueKind.Bytes, value.Clone(), 0);

    public static implicit operator YValue(AbstractType? value) => value == null ? default : new YValue(YValueKind.SharedType, value, 0);

    public static implicit operator YValue(YDoc? value) => value == null ? default : new YValue(YValueKind.Document, value, 0);

    public static explicit operator bool(YValue value) => value.GetBoolean();

    public static explicit operator int(YValue value) => value.GetInt32();

    public static explicit operator long(YValue value) => value.GetInt64();

    public static explicit operator double(YValue value) => value.GetDouble();

    public static explicit operator BigInteger(YValue value) => value.GetBigInteger();

    public static explicit operator string(YValue value) => value.GetString();

    /// <summary>A copy of the bytes.</summary>
    public static explicit operator byte[](YValue value) => value.GetBytes();

    public bool TryGetBoolean(out bool value)
    {
        value = _bits != 0;
        return _kind == YValueKind.Boolean;
    }

    public bool TryGetDouble(out double value)
    {
        value = _kind == YValueKind.Number ? BitConverter.Int64BitsToDouble(_bits) : 0;
        return _kind == YValueKind.Number;
    }

    /// <summary>A number with no fraction within ±2^53, as JavaScript sees an integer: the value of an <see cref="int"/>, a <see cref="long"/> or a double alike.</summary>
    public bool TryGetInt64(out long value)
    {
        value = 0;
        if (_kind != YValueKind.Number)
        {
            return false;
        }

        var d = BitConverter.Int64BitsToDouble(_bits);
        if (Math.Floor(d) != d || Math.Abs(d) > MaxExactInteger)
        {
            return false;
        }

        value = (long)d;
        return true;
    }

    public bool TryGetInt32(out int value)
    {
        value = 0;
        if (!TryGetInt64(out var l) || l < int.MinValue || l > int.MaxValue)
        {
            return false;
        }

        value = (int)l;
        return true;
    }

    public bool TryGetBigInteger(out BigInteger value)
    {
        value = _kind == YValueKind.BigInteger ? new BigInteger(_bits) : default;
        return _kind == YValueKind.BigInteger;
    }

    public bool TryGetString([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        value = _kind == YValueKind.String ? (string)_object! : null;
        return value != null;
    }

    /// <summary>A view of the bytes the value holds; it cannot change them.</summary>
    public bool TryGetBytes(out ReadOnlyMemory<byte> value)
    {
        value = _kind == YValueKind.Bytes ? (byte[])_object! : default;
        return _kind == YValueKind.Bytes;
    }

    public bool TryGetShared([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AbstractType? value)
    {
        value = _object as AbstractType;
        return value != null;
    }

    public bool TryGetDocument([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out YDoc? value)
    {
        value = _object as YDoc;
        return value != null;
    }

    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    public bool GetBoolean() => TryGetBoolean(out var v) ? v : throw NotA("a boolean");

    /// <exception cref="InvalidOperationException">The value is not a number.</exception>
    public double GetDouble() => TryGetDouble(out var v) ? v : throw NotA("a number");

    /// <exception cref="InvalidOperationException">The value is not an integral number within ±2^53.</exception>
    public long GetInt64() => TryGetInt64(out var v) ? v : throw NotA("an integer within ±2^53");

    /// <exception cref="InvalidOperationException">The value is not an integral number within the range of an <see cref="int"/>.</exception>
    public int GetInt32() => TryGetInt32(out var v) ? v : throw NotA("an integer within the range of an int");

    /// <exception cref="InvalidOperationException">The value is not a big integer.</exception>
    public BigInteger GetBigInteger() => TryGetBigInteger(out var v) ? v : throw NotA("a big integer");

    /// <exception cref="InvalidOperationException">The value is not a string.</exception>
    public string GetString() => TryGetString(out var v) ? v : throw NotA("a string");

    /// <summary>A copy of the bytes.</summary>
    /// <exception cref="InvalidOperationException">The value is not bytes.</exception>
    public byte[] GetBytes() => TryGetBytes(out var v) ? v.ToArray() : throw NotA("bytes");

    private InvalidOperationException NotA(string what) => new($"The value is {_kind}, not {what}.");

    /// <exception cref="InvalidOperationException">The value is not an array.</exception>
    public int Length => _object is FrozenList list ? list.Count : throw NotA("an array");

    /// <exception cref="InvalidOperationException">The value is not an array.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public YValue this[int index] =>
        _object is FrozenList list ? FromStored(list.RawAt(index)) : throw NotA("an array");

    public IEnumerable<YValue> EnumerateArray()
    {
        var list = _object as FrozenList ?? throw NotA("an array");
        for (var i = 0; i < list.Count; i++)
        {
            yield return FromStored(list.RawAt(i));
        }
    }

    /// <exception cref="InvalidOperationException">The value is not an object.</exception>
    public int MemberCount => _object is FrozenMap map ? map.Count : throw NotA("an object");

    /// <exception cref="InvalidOperationException">The value is not an object.</exception>
    /// <exception cref="KeyNotFoundException">The object has no member <paramref name="name"/>.</exception>
    public YValue this[string name] => TryGetMember(name, out var v) ? v : throw new KeyNotFoundException($"The object has no member '{name}'.");

    /// <exception cref="InvalidOperationException">The value is not an object.</exception>
    public bool TryGetMember(string name, out YValue value)
    {
        var map = _object as FrozenMap ?? throw NotA("an object");
        if (map.TryGetRaw(name, out var stored))
        {
            value = FromStored(stored);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>The members in key order.</summary>
    public IEnumerable<KeyValuePair<string, YValue>> EnumerateObject()
    {
        var map = _object as FrozenMap ?? throw NotA("an object");
        foreach (var raw in map.EnumerateRaw())
        {
            yield return new KeyValuePair<string, YValue>(raw.Key, FromStored(raw.Value));
        }
    }

    /// <summary>JavaScript's <c>String(value)</c>: <c>"null"</c>, <c>"true"</c>, a number as JavaScript prints it, an array joined with commas, <c>"[object Object]"</c> for an object.</summary>
    public override string ToString() => _kind switch
    {
        YValueKind.Null => "null",
        YValueKind.Boolean => _bits != 0 ? "true" : "false",
        YValueKind.BigInteger => _bits.ToString(CultureInfo.InvariantCulture),
        YValueKind.Number => JsValue.ToJsString(BitConverter.Int64BitsToDouble(_bits)),
        _ => JsValue.ToJsString(_object!),
    };

    public bool Equals(YValue other)
    {
        if (_kind != other._kind)
        {
            return false;
        }

        return _kind switch
        {
            YValueKind.Null => true,
            YValueKind.Boolean or YValueKind.BigInteger => _bits == other._bits,
            YValueKind.Number => BitConverter.Int64BitsToDouble(_bits).Equals(BitConverter.Int64BitsToDouble(other._bits)),
            YValueKind.String => string.Equals((string)_object!, (string)other._object!, StringComparison.Ordinal),
            YValueKind.Bytes => ((byte[])_object!).AsSpan().SequenceEqual((byte[])other._object!),
            YValueKind.Array or YValueKind.Object => ContainersEqual(_object!, other._object!),
            _ => ReferenceEquals(_object, other._object),
        };
    }

    public override bool Equals(object? obj) => obj is YValue other && Equals(other);

    public override int GetHashCode() => _kind switch
    {
        YValueKind.Null => 0,
        YValueKind.Boolean or YValueKind.BigInteger => HashCode.Combine(_kind, _bits),
        YValueKind.Number => HashCode.Combine(_kind, BitConverter.Int64BitsToDouble(_bits).GetHashCode()),
        YValueKind.String => HashCode.Combine(_kind, string.GetHashCode((string)_object!, StringComparison.Ordinal)),
        YValueKind.Bytes => HashCode.Combine(_kind, ((byte[])_object!).Length),
        YValueKind.Array => HashCode.Combine(_kind, ((FrozenList)_object!).Count),
        YValueKind.Object => HashCode.Combine(_kind, ((FrozenMap)_object!).Count),
        _ => HashCode.Combine(_kind, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_object)),
    };

    public static bool operator ==(YValue left, YValue right) => left.Equals(right);

    public static bool operator !=(YValue left, YValue right) => !left.Equals(right);

    // Without recursion: the nesting is bounded only by the limits of an update.
    private static bool ContainersEqual(object a, object b)
    {
        var pending = new Stack<(object A, object B)>();
        pending.Push((a, b));
        while (pending.Count > 0)
        {
            var (x, y) = pending.Pop();
            if (ReferenceEquals(x, y))
            {
                continue;
            }

            if (x is FrozenList lx && y is FrozenList ly)
            {
                if (lx.Count != ly.Count)
                {
                    return false;
                }

                for (var i = 0; i < lx.Count; i++)
                {
                    if (!MembersEqual(lx.RawAt(i), ly.RawAt(i), pending))
                    {
                        return false;
                    }
                }
            }
            else if (x is FrozenMap mx && y is FrozenMap my)
            {
                if (mx.Count != my.Count)
                {
                    return false;
                }

                foreach (var entry in mx.EnumerateRaw())
                {
                    if (!my.TryGetRaw(entry.Key, out var other) || !MembersEqual(entry.Value, other, pending))
                    {
                        return false;
                    }
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool MembersEqual(object? x, object? y, Stack<(object, object)> pending)
    {
        if (x is FrozenList or FrozenMap)
        {
            if (y is not (FrozenList or FrozenMap))
            {
                return false;
            }

            pending.Push((x, y));
            return true;
        }

        return FromStored(x).Equals(FromStored(y));
    }
}
