using System.Buffers.Binary;

namespace Common
{
    public static class ChunkBinaryFormat
    {
        public const int Version = 2;
        public static ReadOnlySpan<byte> Magic => "LFSCHNK1"u8;

        public const int HeaderSize = 8 + 4;
        public const int RecordHeaderSize = 8 + 2;

        public static void WriteHeader(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[HeaderSize];
            Magic.CopyTo(buffer[..8]);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[8..12], Version);
            stream.Write(buffer);
        }

        public static void ValidateHeader(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[HeaderSize];
            ReadExactly(stream, buffer);

            if (!buffer[..8].SequenceEqual(Magic))
            {
                throw new InvalidDataException("Invalid chunk file magic.");
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(buffer[8..12]);
            if (version != Version)
            {
                throw new InvalidDataException($"Unsupported chunk version: {version}");
            }
        }

        public static int EncodeRecord(Record record, Span<byte> destination)
        {
            int textByteCount = record.Utf8Text.Length;
            if (textByteCount > ushort.MaxValue)
                throw new InvalidDataException("Text is too large for chunk format.");

            int total = RecordHeaderSize + textByteCount;
            if (destination.Length < total)
                throw new ArgumentException("Destination buffer is too small.", nameof(destination));

            BinaryPrimitives.WriteUInt64LittleEndian(destination[..8], record.Number);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[8..10], (ushort)textByteCount);
            record.Utf8Text.CopyTo(destination[10..(10 + textByteCount)]);

            return total;
        }

        public static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            int offset = 0;

            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer[offset..]);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream.");
                }

                offset += read;
            }
        }
    }
}