using System;
using System.Collections.Generic;
using System.Drawing;

namespace GravityCapture.Services
{
    /// <summary>
    /// Legacy holder for OCR ingestion helpers. The real OCR work now happens
    /// in IOcrService implementations; this remains for compatibility.
    /// </summary>
    public sealed partial class OcrIngestor
    {
        /// <summary>A single OCR line (text + confidence + bounding box).</summary>
        public readonly record struct OcrLine(string Text, float Confidence, Rectangle Bbox);

        // Small rolling set of recently seen lines to reduce duplicate posts.
        private readonly RollingWindow<string> _recent = new(200);

        public OcrIngestor() { }

        /// <summary>Record a line if it hasn't been seen recently.</summary>
        public bool TryRegisterLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var key = text.Trim();
            if (_recent.Contains(key)) return false;
            _recent.Add(key);
            return true;
        }

        /// <summary>Clears the in-memory duplicate filter.</summary>
        public void ResetRecent() => _recent.Clear();
    }

    /// <summary>Fixed-size FIFO set with O(1) add/contains for small N.</summary>
    internal sealed class RollingWindow<T>
    {
        private readonly int _capacity;
        private readonly Queue<T> _queue;
        private readonly HashSet<T> _set;

        public RollingWindow(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _queue = new Queue<T>(capacity);
            _set = new HashSet<T>();
        }

        public void Add(T value)
        {
            if (_set.Contains(value)) return;
            _queue.Enqueue(value);
            _set.Add(value);
            if (_queue.Count > _capacity)
            {
                var old = _queue.Dequeue();
                _set.Remove(old);
            }
        }

        public bool Contains(T value) => _set.Contains(value);

        public void Clear()
        {
            _queue.Clear();
            _set.Clear();
        }
    }
}
