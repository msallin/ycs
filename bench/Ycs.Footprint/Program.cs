using System.Collections;
using System.Reflection;

// dotnet run -c Release --project bench/Ycs.Footprint -- <path to Ycs.dll> [empty|hello|subdoc] [detail]
// What one document holds on the heap, by type: the objects reachable from it (a type's own bytes, fields and array
// elements), as a 64-bit runtime lays them out (16 B object header and method table, 8 B alignment). Estimates, but the
// same estimate for both builds; the sums agree with Ycs.Layout small to within a few percent.
var asm = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var shape = args.Length > 1 ? args[1] : "empty";
var detail = args.Length > 2;
var docType = asm.GetType("Ycs.YDoc")!;
var optType = asm.GetType("Ycs.YDocOptions")!;
var opts = Activator.CreateInstance(optType)!;
optType.GetProperty("ClientId")!.SetValue(opts, 1L);
var doc = Activator.CreateInstance(docType, opts)!;
var text = docType.GetMethod("GetText", new[] { typeof(string) })!.Invoke(doc, new object[] { "text" })!;
if (shape == "hello")
{
    asm.GetType("Ycs.YText")!.GetMethods().First(m => m.Name == "Insert" && m.GetParameters().Length >= 2 && m.GetParameters()[1].ParameterType == typeof(string)).Invoke(text, new object[] { 0, "hello", null });
}

var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
var owners = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
var owner = "root";
var sizes = new SortedDictionary<string, (int Count, long Bytes)>();
var stack = new Stack<object>();
stack.Push(doc);
seen.Add(doc);
long total = 0;
while (stack.Count > 0)
{
    var o = stack.Pop();
    owner = owners.GetValueOrDefault(o, "?");
    var t = o.GetType();
    long size;
    if (o is string s)
    {
        size = 22 + 2 * s.Length;
    }
    else if (o is Array a)
    {
        var et = t.GetElementType()!;
        var elem = et.IsValueType ? ElementSize(et) : 8;
        size = 24 + (long)elem * a.Length;
        if (!et.IsPrimitive)
        {
            foreach (var e in a)
            {
                Visit(e, owner);
            }
        }
    }
    else if (o is Delegate || o is Type || o is Assembly || o is System.Threading.Thread)
    {
        size = 32;
    }
    else
    {
        size = 16;
        for (var c = t; c != null && c != typeof(object); c = c.BaseType)
        {
            foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                size += f.FieldType.IsValueType ? ElementSize(f.FieldType) : 8;
                if (!f.FieldType.IsPrimitive && !f.FieldType.IsPointer)
                {
                    VisitField(f, o);
                }
            }
        }
    }

    size = (size + 7) & ~7L;
    total += size;
    var name = detail ? owner + " -> " + (o is Array arr0 ? t.Name + "[" + arr0.Length + "]" : t.Name) : t.IsGenericType ? t.Name + "<" + string.Join(",", t.GetGenericArguments().Select(g => g.Name)) + ">" : t.Name;
    sizes.TryGetValue(name, out var cur);
    sizes[name] = (cur.Count + 1, cur.Bytes + size);
}

foreach (var kv in sizes.OrderByDescending(k => k.Value.Bytes))
{
    Console.WriteLine($"{kv.Value.Bytes,7} B  {kv.Value.Count,4} x  {kv.Key}");
}

Console.WriteLine($"{total,7} B  total ({shape})");
GC.KeepAlive(doc);

void VisitField(FieldInfo f, object o)
{
    string label = null;
    try
    {
        var v = f.GetValue(o);
        label = o.GetType().Namespace != null && o.GetType().Namespace.StartsWith("System", StringComparison.Ordinal) ? owners.GetValueOrDefault(o, "?") : o.GetType().Name + "." + f.Name;
        if (f.FieldType.IsValueType)
        {
            WalkStruct(v);
        }
        else
        {
            Visit(v, label);
        }
    }
    catch (Exception)
    {
    }
}

void WalkStruct(object v)
{
    if (v == null)
    {
        return;
    }

    foreach (var f in v.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
    {
        if (!f.FieldType.IsPrimitive && !f.FieldType.IsPointer && !f.FieldType.IsEnum)
        {
            var x = f.GetValue(v);
            if (f.FieldType.IsValueType)
            {
                WalkStruct(x);
            }
            else
            {
                Visit(x);
            }
        }
    }
}

void Visit(object v, string label = null)
{
    if (v == null || v.GetType().IsPointer || v is Pointer || v.GetType().IsValueType)
    {
        if (v != null && v.GetType().IsValueType && !v.GetType().IsPrimitive && !v.GetType().IsEnum)
        {
            WalkStruct(v);
        }

        return;
    }

    if (seen.Add(v))
    {
        owners[v] = label;
        stack.Push(v);
    }
}

static int ElementSize(Type t) => t.IsValueType
    ? (int)typeof(System.Runtime.CompilerServices.Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(t).Invoke(null, null)!
    : 8;
