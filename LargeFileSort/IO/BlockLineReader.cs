using System.Buffers;
using System.Text;

namespace LargeFileSorter
{
    /// <summary>
    /// High-throughput UTF-8 line reader that reads the file in large byte blocks
    /// and scans for newline characters manually, avoiding the overhead of
    /// <see cref="System.IO.StreamReader"/> per-line allocations inside the hot path.
    /// Handles both LF and CRLF line endings.
    /// </summary>
    public sealed class BlockLineReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _buffer;

        private int _bufferCount;
        private int _bufferOffset;
        private bool _endOfStream;

        private byte[] _lineBuffer;
        private int _lineBufferCount;

        public BlockLineReader(string path, int bufferSize)
        {
            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan);

            _buffer     = ArrayPool<byte>.Shared.Rent(bufferSize);
            _lineBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        }

        public bool TryReadLine(out string? line)
        {
            line = null;

            while (true)
            {
                if (TryExtractLineFromBuffer(out line))
                {
                    return true;
                }

                if (_endOfStream)
                {
                    // Flush any remaining bytes as the last (unterminated) line.
                    if (_lineBufferCount > 0)
                    {
                        line = DecodeLine(_lineBuffer.AsSpan(0, _lineBufferCount));
                        _lineBufferCount = 0;
                        return true;
                    }

                    return false;
                }

                FillBuffer();
            }
        }

        private bool TryExtractLineFromBuffer(out string? line)
        {
            line = null;

            for (int i = _bufferOffset; i < _bufferCount; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;

                int length = i - _bufferOffset;
                EnsureLineCapacity(_lineBufferCount + length);

                Buffer.BlockCopy(_buffer, _bufferOffset, _lineBuffer, _lineBufferCount, length);
                _lineBufferCount += length;

                // Strip trailing CR for CRLF.
                if (_lineBufferCount > 0 && _lineBuffer[_lineBufferCount - 1] == (byte)'\r')
                {
                    _lineBufferCount--;
                }

                line = DecodeLine(_lineBuffer.AsSpan(0, _lineBufferCount));
                _lineBufferCount = 0;
                _bufferOffset = i + 1;
                return true;
            }

            int remaining = _bufferCount - _bufferOffset;
            if (remaining > 0)
            {
                EnsureLineCapacity(_lineBufferCount + remaining);
                Buffer.BlockCopy(_buffer, _bufferOffset, _lineBuffer, _lineBufferCount, remaining);
                _lineBufferCount += remaining;
            }

            _bufferOffset = _bufferCount;
            return false;
        }

        private void FillBuffer()
        {
            _bufferOffset = 0;
            _bufferCount  = _stream.Read(_buffer, 0, _buffer.Length);
            _endOfStream  = _bufferCount == 0;
        }

        private void EnsureLineCapacity(int needed)
        {
            if (_lineBuffer.Length >= needed) return;

            int newSize = Math.Max(_lineBuffer.Length * 2, needed);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_lineBuffer, 0, newBuffer, 0, _lineBufferCount);
            ArrayPool<byte>.Shared.Return(_lineBuffer);
            _lineBuffer = newBuffer;
        }

        private static string DecodeLine(ReadOnlySpan<byte> bytes) =>
            Encoding.UTF8.GetString(bytes);

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            ArrayPool<byte>.Shared.Return(_lineBuffer);
            _stream.Dispose();
        }
    }
}

