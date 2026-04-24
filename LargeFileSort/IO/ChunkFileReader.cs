using System.Buffers;
using System.Buffers.Binary;
using Common;

namespace LargeFileSorter
{
    public sealed class ChunkFileReader : IDisposable
    {
        private readonly FileStream _stream;
        private byte[] _headerBuffer;
        private byte[] _textBuffer;

        public string FilePath { get; }

        public ChunkFileReader(string filePath, int bufferSize)
        {
            FilePath = filePath;
            _stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan);

            ChunkBinaryFormat.ValidateHeader(_stream);
            _headerBuffer = ArrayPool<byte>.Shared.Rent(ChunkBinaryFormat.RecordHeaderSize);
            _textBuffer   = ArrayPool<byte>.Shared.Rent(1024);
        }

        public bool TryRead(out Record record)
        {
            record = default;

            if (_stream.Position >= _stream.Length)
                return false;

            int read = _stream.Read(_headerBuffer, 0, ChunkBinaryFormat.RecordHeaderSize);
            if (read == 0)
                return false;

            if (read != ChunkBinaryFormat.RecordHeaderSize)
                throw new InvalidDataException("Corrupted chunk record header.");

            ulong  number        = BinaryPrimitives.ReadUInt64LittleEndian(_headerBuffer.AsSpan(0, 8));
            ushort textByteCount = BinaryPrimitives.ReadUInt16LittleEndian(_headerBuffer.AsSpan(8, 2));

            EnsureTextBuffer(textByteCount);
            ChunkBinaryFormat.ReadExactly(_stream, _textBuffer.AsSpan(0, textByteCount));

            // Keep UTF-8 bytes — no UTF-16 string allocation in the sort pipeline
            byte[] utf8 = new byte[textByteCount];
            _textBuffer.AsSpan(0, textByteCount).CopyTo(utf8);
            record = new Record(number, utf8);
            return true;
        }

        private void EnsureTextBuffer(int required)
        {
            if (_textBuffer.Length >= required) return;

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(required);
            ArrayPool<byte>.Shared.Return(_textBuffer);
            _textBuffer = newBuffer;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_headerBuffer);
            ArrayPool<byte>.Shared.Return(_textBuffer);
            _stream.Dispose();
        }
    }
}
