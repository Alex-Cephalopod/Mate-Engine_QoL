using System;
using System.Collections.Generic;
using MateEngineQoL.AI;

namespace MateEngineQoL.Voice
{
    /// <summary>
    /// Decodes a 16-bit PCM WAV stream arriving in arbitrary byte chunks into mono floats (channels are averaged).
    /// Streamed WAVs often carry a placeholder data size, so the data chunk is read until the stream ends.
    /// Anything other than 16-bit integer PCM is refused with a <see cref="ProviderException"/>.
    /// </summary>
    public sealed class WavStreamDecoder
    {
        const int MaxHeaderBytes = 64 * 1024;

        readonly List<byte> _header = new List<byte>();
        int _channels;
        bool _sawFormat;
        byte[] _carry = Array.Empty<byte>();

        /// <summary>True once the data chunk has started; <see cref="SampleRate"/> is valid from then on.</summary>
        public bool HeaderDone { get; private set; }

        public int SampleRate { get; private set; }

        public float[] Decode(byte[] bytes, int count)
        {
            if (count <= 0) return Array.Empty<float>();
            if (HeaderDone) return DecodeFrames(bytes, 0, count);

            for (int i = 0; i < count; i++) _header.Add(bytes[i]);
            int dataStart = ParseHeader();
            if (dataStart < 0)
            {
                if (_header.Count > MaxHeaderBytes) throw new ProviderException("The WAV header is too large.");
                return Array.Empty<float>();
            }

            HeaderDone = true;
            byte[] rest = _header.GetRange(dataStart, _header.Count - dataStart).ToArray();
            _header.Clear();
            return DecodeFrames(rest, 0, rest.Length);
        }

        /// <returns>Offset of the first sample byte, or -1 if more header bytes are needed.</returns>
        int ParseHeader()
        {
            if (_header.Count < 12) return -1;
            if (!Tag(0, "RIFF") || !Tag(8, "WAVE")) throw new ProviderException("The server sent audio that is not a WAV file.");

            int pos = 12;
            while (pos + 8 <= _header.Count)
            {
                long size = ReadUInt32(pos + 4);
                int body = pos + 8;
                if (Tag(pos, "data"))
                {
                    if (!_sawFormat) throw new ProviderException("The WAV file has no format chunk before its data.");
                    return body;
                }
                if (body + size > _header.Count) return -1; // chunk not complete yet
                if (Tag(pos, "fmt ")) ReadFormat(body, (int)size);
                pos = body + (int)size + (int)(size & 1); // chunks are padded to an even length
            }
            return -1;
        }

        void ReadFormat(int at, int size)
        {
            if (size < 16) throw new ProviderException("The WAV format chunk is too short.");
            int format = ReadUInt16(at);
            _channels = ReadUInt16(at + 2);
            SampleRate = (int)ReadUInt32(at + 4);
            int bits = ReadUInt16(at + 14);
            if (format == 0xFFFE && size >= 26) format = ReadUInt16(at + 24); // WAVE_FORMAT_EXTENSIBLE: sub-format GUID starts with the tag
            if (format != 1 || bits != 16)
                throw new ProviderException("The server sent WAV audio that is not 16-bit PCM (format " + format + ", " + bits + " bits).");
            if (_channels < 1 || _channels > 8) throw new ProviderException("The WAV audio has " + _channels + " channels.");
            if (SampleRate < 8000 || SampleRate > 192000) throw new ProviderException("The WAV sample rate " + SampleRate + " Hz is not supported.");
            _sawFormat = true;
        }

        float[] DecodeFrames(byte[] bytes, int offset, int count)
        {
            int frameBytes = 2 * _channels;
            byte[] data = bytes;
            int start = offset, length = count;
            if (_carry.Length > 0)
            {
                data = new byte[_carry.Length + count];
                Array.Copy(_carry, data, _carry.Length);
                Array.Copy(bytes, offset, data, _carry.Length, count);
                start = 0;
                length = data.Length;
            }

            int frames = length / frameBytes;
            var samples = new float[frames];
            int p = start;
            for (int f = 0; f < frames; f++)
            {
                int sum = 0;
                for (int c = 0; c < _channels; c++, p += 2) sum += (short)(data[p] | (data[p + 1] << 8));
                samples[f] = sum / (32768f * _channels);
            }

            int left = length - frames * frameBytes;
            _carry = new byte[left];
            Array.Copy(data, start + frames * frameBytes, _carry, 0, left);
            return samples;
        }

        bool Tag(int at, string tag)
        {
            for (int i = 0; i < 4; i++)
                if (_header[at + i] != tag[i]) return false;
            return true;
        }

        int ReadUInt16(int at) => _header[at] | (_header[at + 1] << 8);

        long ReadUInt32(int at) =>
            (uint)(_header[at] | (_header[at + 1] << 8) | (_header[at + 2] << 16) | (_header[at + 3] << 24));
    }
}
