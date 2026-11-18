using System.Collections;
using System.Runtime.CompilerServices;

namespace Ycs;

public sealed partial class YArray
{
    /// <summary>The struct enumerator of <see cref="YArray"/>: the values in order, as <see cref="YValue"/>s.</summary>
    public struct Enumerator : IEnumerator<YValue>
    {
        private RawEnumerator _raw;

        internal Enumerator(RawEnumerator raw) => _raw = raw;

        public readonly YValue Current => YValue.FromStored(_raw.Stored);

        readonly object IEnumerator.Current => Current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _raw.MoveNext();

        public void Dispose() => _raw.Dispose();

        void IEnumerator.Reset() => throw new NotSupportedException();
    }

    /// <summary>
    /// The values as stored, in order. A run's values are a copy taken when the walk reaches it (an observer that edits the
    /// array while the caller enumerates changes the structs, not the copy), returned by <see cref="Dispose"/>. After a
    /// run the walk resumes where the run's id lies now (<see cref="StructStore.NextAfter"/>).
    /// </summary>
    internal struct RawEnumerator : IDisposable
    {
        private readonly YDoc? _doc;
        private List<object?>.Enumerator _prelim;
        private ItemRef _item;
        private ItemId _id;
        private int _departures;
        private StoredContent _content;
        private object?[]? _values;
        private int _index;
        private int _length;
        private bool _inRun;
        private bool _opened;

        internal RawEnumerator(YArray array)
        {
            if (array._prelimContent != null)
            {
                _prelim = array._prelimContent.GetEnumerator();
                return;
            }

            _doc = array.Doc;
            _item = array.FirstItem;
        }

        // The value as the walk holds it: an Any's value is the stored object itself (YValue never hands a byte array out
        // uncopied), so the YValue enumerator reads this and skips the copy of Current.
        internal object? Stored { readonly get; private set; }

        public readonly object? Current => _values != null ? ValueFreezer.OnRead(Stored) : Stored;

        public readonly RawEnumerator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_index < _length && _values != null)
            {
                Stored = _values[_index++];
                return true;
            }

            return MoveNextSlow();
        }

        private bool MoveNextSlow()
        {
            if (_doc == null)
            {
                if (!_prelim.MoveNext())
                {
                    return false;
                }

                Stored = _prelim.Current;
                return true;
            }

            if (!_opened)
            {
                _opened = true;
                OpenRun();
            }

            while (true)
            {
                if (_inRun && _index < _length)
                {
                    Stored = _values != null ? _values[_index] : _content.ValueAt(_index);
                    _index++;
                    return true;
                }

                if (_item.IsNull)
                {
                    EndRun();
                    return false;
                }

                EndRun();
                _item = _doc!.Store.NextAfter(_item, _id, _departures);
                OpenRun();
            }
        }

        private void OpenRun()
        {
            if (_item.IsNull)
            {
                return;
            }

            _id = _item.Id;
            _departures = _doc!.Store.Departures;
            if (_item.Countable && !_item.Deleted)
            {
                _length = _item.Length;
                _content = _item.Stored;
                _values = _content.AnyValues;
                _index = 0;
                _inRun = true;
            }
        }

        private void EndRun()
        {
            if (_inRun)
            {
                _content.Release();
                _content = default;
                _values = null;
                _length = 0;
                _inRun = false;
            }
        }

        public void Dispose() => EndRun();
    }
}
