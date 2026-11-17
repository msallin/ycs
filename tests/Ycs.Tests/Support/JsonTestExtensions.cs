namespace Ycs;

internal static class JsonTestExtensions
{
    /// <summary>The JSON of a type as plain objects (a dictionary, a list, a string ...), for comparisons that do not care about the text.</summary>
    public static object? ToJsonObject(this YType type) => YValue.Parse(type.ToJsonString()).ToObject();
}
