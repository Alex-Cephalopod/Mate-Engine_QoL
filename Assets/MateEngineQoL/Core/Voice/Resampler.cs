using System;
using System.Collections.Generic;

namespace MateEngineQoL.Voice
{
    /// <summary>
    /// Streaming linear resampler for mono audio. Keeps its position and last sample between chunks,
    /// so chunk boundaries don't click. Good enough for speech; not meant for music.
    /// </summary>
    public sealed class Resampler
    {
        readonly double _step; // input samples per output sample
        readonly List<float> _out = new List<float>();
        double _pos;           // next output position in input samples; -1 means the previous chunk's last sample
        float _prev;
        bool _hasPrev;

        public int InputRate { get; }
        public int OutputRate { get; }

        public Resampler(int inputRate, int outputRate)
        {
            if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
            if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
            InputRate = inputRate;
            OutputRate = outputRate;
            _step = (double)inputRate / outputRate;
        }

        public float[] Process(float[] input) => Process(input, input?.Length ?? 0);

        public float[] Process(float[] input, int count)
        {
            if (count <= 0) return Array.Empty<float>();
            if (InputRate == OutputRate)
            {
                var copy = new float[count];
                Array.Copy(input, copy, count);
                return copy;
            }

            _out.Clear();
            while (true)
            {
                int i = (int)Math.Floor(_pos);
                if (i + 1 >= count) break;
                if (i < 0 && !_hasPrev) { _pos = 0; continue; }
                float a = i < 0 ? _prev : input[i];
                float b = input[i + 1];
                _out.Add(a + (b - a) * (float)(_pos - i));
                _pos += _step;
            }
            _pos -= count;
            _prev = input[count - 1];
            _hasPrev = true;
            return _out.ToArray();
        }

        public void Reset()
        {
            _pos = 0;
            _prev = 0;
            _hasPrev = false;
        }
    }

    /// <summary>Decodes signed 16-bit little-endian PCM arriving in arbitrary byte chunks (an odd byte is kept for the next chunk).</summary>
    public sealed class Pcm16Decoder
    {
        bool _hasCarry;
        byte _carry;

        public float[] Decode(byte[] bytes, int count)
        {
            if (count <= 0) return Array.Empty<float>();
            int total = count + (_hasCarry ? 1 : 0);
            var samples = new float[total / 2];
            int src = 0, dst = 0;
            if (_hasCarry)
            {
                samples[dst++] = (short)(_carry | (bytes[0] << 8)) / 32768f;
                src = 1;
                _hasCarry = false;
            }
            for (; src + 1 < count; src += 2)
                samples[dst++] = (short)(bytes[src] | (bytes[src + 1] << 8)) / 32768f;
            if (src < count)
            {
                _carry = bytes[src];
                _hasCarry = true;
            }
            return samples;
        }

        public void Reset() => _hasCarry = false;
    }
}
