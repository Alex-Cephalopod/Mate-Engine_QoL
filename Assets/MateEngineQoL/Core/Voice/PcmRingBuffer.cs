using System;

namespace MateEngineQoL.Voice
{
    /// <summary>
    /// Fixed-size float FIFO shared by the TTS writer (any thread) and the audio callback (audio thread).
    /// Writes never block: they return how much fit, and the writer waits and retries.
    /// </summary>
    public sealed class PcmRingBuffer
    {
        readonly float[] _data;
        readonly object _lock = new object();
        int _read, _count;

        public PcmRingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _data = new float[capacity];
        }

        public int Capacity => _data.Length;

        public int Count
        {
            get { lock (_lock) return _count; }
        }

        public int FreeSpace
        {
            get { lock (_lock) return _data.Length - _count; }
        }

        /// <summary>Copies as many samples as fit and returns that number.</summary>
        public int Write(float[] source, int offset, int count)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (offset < 0 || count < 0 || offset + count > source.Length) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_lock)
            {
                int n = Math.Min(count, _data.Length - _count);
                int write = (_read + _count) % _data.Length;
                int first = Math.Min(n, _data.Length - write);
                Array.Copy(source, offset, _data, write, first);
                Array.Copy(source, offset + first, _data, 0, n - first);
                _count += n;
                return n;
            }
        }

        /// <summary>Copies up to <paramref name="count"/> samples out and returns how many; the rest of the target is untouched.</summary>
        public int Read(float[] target, int offset, int count)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (offset < 0 || count < 0 || offset + count > target.Length) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_lock)
            {
                int n = Math.Min(count, _count);
                int first = Math.Min(n, _data.Length - _read);
                Array.Copy(_data, _read, target, offset, first);
                Array.Copy(_data, 0, target, offset + first, n - first);
                _read = (_read + n) % _data.Length;
                _count -= n;
                return n;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _read = 0;
                _count = 0;
            }
        }
    }
}
