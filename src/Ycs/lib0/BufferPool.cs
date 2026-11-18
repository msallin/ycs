using System.Buffers;

namespace Ycs;

/// <summary>
/// <see cref="ArrayPool{T}.Shared"/> with a count of the arrays rented and not yet returned on the
/// current thread, so tests can check that every path, exceptions included, returns what it rented.
/// Debug builds also catch an array returned twice (or never rented) and overwrite a returned byte
/// array, so a use after return reads garbage instead of plausible data.
/// </summary>
internal static class BufferPool<T>
{
    [ThreadStatic]
    private static int t_outstanding;

#if DEBUG
    private static readonly HashSet<T[]> s_rented = new();
#endif

    /// <summary>Arrays rented on this thread and not returned yet.</summary>
    public static int OutstandingOnCurrentThread => t_outstanding;

    public static T[] Rent(int minimumLength)
    {
        // Never length 0: the pool hands out one shared empty array for that.
        var array = ArrayPool<T>.Shared.Rent(Math.Max(minimumLength, 1));
#if DEBUG
        lock (s_rented)
        {
            if (!s_rented.Add(array))
            {
                throw new InvalidOperationException("The pool handed out an array that is still rented.");
            }
        }
#endif
        t_outstanding++;
        return array;
    }

    /// <summary>Returns an array that holds no document content (state vectors, counts).</summary>
    public static void Return(T[] array) => Return(array, 0);

    /// <summary>Clears the first <paramref name="used"/> elements, the only ones the renter wrote, and returns the array: a buffer of content does not leave the process's pool holding it.</summary>
    public static void Return(T[] array, int used)
    {
#if DEBUG
        lock (s_rented)
        {
            if (!s_rented.Remove(array))
            {
                throw new InvalidOperationException("An array was returned to the pool twice, or was never rented from it.");
            }
        }

        if (array is byte[] bytes)
        {
            bytes.AsSpan().Fill(0xCD);
        }
#endif
        t_outstanding--;
        array.AsSpan(0, used).Clear();
        ArrayPool<T>.Shared.Return(array);
    }
}
