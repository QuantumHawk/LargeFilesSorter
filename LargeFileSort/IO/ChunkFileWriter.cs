using System.Buffers;
using Common;

namespace LargeFileSorter
{
    public sealed class ChunkFileWriter : IDisposable
    {
        private readonly FileStream _stream;
        private byte[] _buffer;

        public string FilePath { get; }

        public ChunkFileWriter(string filePath, int bufferSize)
        {
            FilePath = filePath;
            _stream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize);

            ChunkBinaryFormat.WriteHeader(_stream);
            _buffer = ArrayPool<byte>.Shared.Rent(1024);
        }

        public void WriteRecord(Record record)
        {
            int maxNeeded = ChunkBinaryFormat.RecordHeaderSize + record.Utf8Text.Length;
            EnsureBuffer(maxNeeded);

            int written = ChunkBinaryFormat.EncodeRecord(record, _buffer);
            _stream.Write(_buffer, 0, written);
        }

        private void EnsureBuffer(int required)
        {
            if (_buffer.Length >= required) return;

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(required);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _stream.Dispose();
        }
    }
}
