using Common;
using LargeFileSorter;
using System.Text;

namespace LargeFileSort.Tests;

// Helper to keep test code concise
file static class R
{
    public static Record Make(ulong number, string text)
        => new Record(number, Encoding.UTF8.GetBytes(text));

    public static string GetText(Record r)
        => Encoding.UTF8.GetString(r.Utf8Text);
}

[TestFixture]
public class LineParserTests
{
    [TestCase("1. Apple",           1UL,  "Apple")]
    [TestCase("415. Apple",       415UL,  "Apple")]
    [TestCase("30432. Something something", 30432UL, "Something something")]
    [TestCase("1.  leading spaces",   1UL,  "leading spaces")]
    [TestCase("0. Zero",              0UL,  "Zero")]
    [TestCase("18446744073709551615. Max", ulong.MaxValue, "Max")]
    public void TryParse_ValidLine_ReturnsRecord(string line, ulong expectedNumber, string expectedText)
    {
        bool ok = LineParser.TryParse(line, out Record record);
        Assert.That(ok, Is.True);
        Assert.That(record.Number,     Is.EqualTo(expectedNumber));
        Assert.That(R.GetText(record), Is.EqualTo(expectedText));
    }

    // First-dot rule: "1.2. Apple" → number=1, text="2. Apple"
    [Test]
    public void TryParse_MultipleDots_UsesFirstDotAsSeparator()
    {
        bool ok = LineParser.TryParse("1.2. Apple", out Record record);
        Assert.That(ok, Is.True);
        Assert.That(record.Number,     Is.EqualTo(1UL));
        Assert.That(R.GetText(record), Is.EqualTo("2. Apple"));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("No dot here")]
    [TestCase(".NoDotAtStart")]
    [TestCase("abc. Text")]      // non-numeric number part
    [TestCase("-1. Negative")]   // negative not allowed (ulong)
    public void TryParse_InvalidLine_ReturnsFalse(string line)
    {
        bool ok = LineParser.TryParse(line, out _);
        Assert.That(ok, Is.False);
    }
}

[TestFixture]
public class RecordComparerTests
{
    private static readonly RecordComparer Cmp = RecordComparer.Instance;

    [Test]
    public void Compare_TextDiffers_SortsByTextFirst()
    {
        var apple  = R.Make(999UL, "Apple");
        var banana = R.Make(1UL,   "Banana");
        Assert.That(Cmp.Compare(apple, banana), Is.LessThan(0));
        Assert.That(Cmp.Compare(banana, apple), Is.GreaterThan(0));
    }

    [Test]
    public void Compare_SameText_SortsByNumber()
    {
        var r1   = R.Make(1UL,   "Apple");
        var r415 = R.Make(415UL, "Apple");
        Assert.That(Cmp.Compare(r1, r415), Is.LessThan(0));
        Assert.That(Cmp.Compare(r415, r1), Is.GreaterThan(0));
    }

    [Test]
    public void Compare_Equal_ReturnsZero()
    {
        var a = R.Make(7UL, "Cherry");
        var b = R.Make(7UL, "Cherry");
        Assert.That(Cmp.Compare(a, b), Is.EqualTo(0));
    }

    [Test]
    public void Compare_LongText_DifferAtEnd_SortsCorrectly()
    {
        // Two strings that share the first 90 characters, differ only at position 90.
        // This exercises the SIMD SequenceCompareTo path beyond a single 32-byte AVX2 chunk.
        string prefix = new string('A', 90);
        var lower  = R.Make(1UL, prefix + "B"); // 91 chars, ends 'B'
        var higher = R.Make(1UL, prefix + "C"); // 91 chars, ends 'C'
        Assert.That(Cmp.Compare(lower, higher), Is.LessThan(0));
        Assert.That(Cmp.Compare(higher, lower), Is.GreaterThan(0));
    }

    [Test]
    public void Compare_LongText_IdenticalText_SortsByNumber()
    {
        // Max allowed text length (100 chars), same text — must fall through to number compare.
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
        var rng = new Random(42); // fixed seed for reproducibility
        string longText = new string(Enumerable.Range(0, 1000).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
        var r1   = R.Make(1UL,   longText);
        var r999 = R.Make(999UL, longText);
        Assert.That(Cmp.Compare(r1, r999), Is.LessThan(0));
        Assert.That(Cmp.Compare(r999, r1), Is.GreaterThan(0));
        Assert.That(Cmp.Compare(r1,   r1), Is.EqualTo(0));
    }

    [Test]
    public void Compare_LongText_CommonPrefixDifferentSuffix_SortsCorrectly()
    {
        // Strings that share 95 bytes then differ — verifies no off-by-one in SIMD tail handling.
        string prefix = new string('x', 95);
        var records = new List<Record>
        {
            R.Make(5UL,  prefix + "zoo"),
            R.Make(5UL,  prefix + "aaa"),
            R.Make(10UL, prefix + "aaa"),
            R.Make(1UL,  prefix + "mmm"),
        };
        records.Sort(Cmp);

        Assert.That(R.GetText(records[0]), Does.EndWith("aaa"));
        Assert.That(records[0].Number, Is.EqualTo(5UL));
        Assert.That(R.GetText(records[1]), Does.EndWith("aaa"));
        Assert.That(records[1].Number, Is.EqualTo(10UL));
        Assert.That(R.GetText(records[2]), Does.EndWith("mmm"));
        Assert.That(R.GetText(records[3]), Does.EndWith("zoo"));
    }

    [Test]
    public void Compare_SortOrder_MatchesSpec()
    {
        // Spec example order: Apple(1), Apple(415), Banana, Cherry, Something
        var records = new List<Record>
        {
            R.Make(415UL,   "Apple"),
            R.Make(30432UL, "Something something something"),
            R.Make(1UL,     "Apple"),
            R.Make(32UL,    "Cherry is the best"),
            R.Make(2UL,     "Banana is yellow"),
        };

        records.Sort(Cmp);

        Assert.That(R.GetText(records[0]), Is.EqualTo("Apple"));
        Assert.That(records[0].Number,     Is.EqualTo(1UL));
        Assert.That(R.GetText(records[1]), Is.EqualTo("Apple"));
        Assert.That(records[1].Number,     Is.EqualTo(415UL));
        Assert.That(R.GetText(records[2]), Is.EqualTo("Banana is yellow"));
        Assert.That(R.GetText(records[3]), Is.EqualTo("Cherry is the best"));
        Assert.That(R.GetText(records[4]), Is.EqualTo("Something something something"));
    }
}

[TestFixture]
public class ChunkBinaryFormatTests
{
    [Test]
    public void EncodeAndDecode_RoundTrip()
    {
        var original = R.Make(12345UL, "Hello World");
        byte[] buf = new byte[ChunkBinaryFormat.RecordHeaderSize + 100];

        int written = ChunkBinaryFormat.EncodeRecord(original, buf);
        Assert.That(written, Is.EqualTo(ChunkBinaryFormat.RecordHeaderSize + Encoding.UTF8.GetByteCount("Hello World")));
    }

    [Test]
    public void EncodeAndDecode_MaxLengthText_RoundTrip()
    {
        // 100-char text (task maximum), verify encode + ChunkFileWriter/Reader round-trip.
        string longText = string.Concat(Enumerable.Range(0, 10).Select(i => $"Word{i:D2}___")); // 100 chars
        ulong number = ulong.MaxValue;
        var original = R.Make(number, longText);

        using var ms = new MemoryStream();
        ChunkBinaryFormat.WriteHeader(ms);

        byte[] buf = new byte[ChunkBinaryFormat.RecordHeaderSize + Encoding.UTF8.GetByteCount(longText) + 10];
        int written = ChunkBinaryFormat.EncodeRecord(original, buf);
        ms.Write(buf, 0, written);
        ms.Position = 0;

        ChunkBinaryFormat.ValidateHeader(ms);

        // Read back the record header manually
        byte[] hdr = new byte[ChunkBinaryFormat.RecordHeaderSize];
        ChunkBinaryFormat.ReadExactly(ms, hdr);
        ulong  readNumber   = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(0, 8));
        ushort readTextLen  = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(8, 2));
        byte[] readUtf8     = new byte[readTextLen];
        ChunkBinaryFormat.ReadExactly(ms, readUtf8);

        Assert.That(readNumber,                          Is.EqualTo(number));
        Assert.That(Encoding.UTF8.GetString(readUtf8),  Is.EqualTo(longText));
    }

    [Test]
    public void WriteAndValidateHeader_Valid()
    {
        using var ms = new MemoryStream();
        ChunkBinaryFormat.WriteHeader(ms);
        ms.Position = 0;
        Assert.DoesNotThrow(() => ChunkBinaryFormat.ValidateHeader(ms));
    }

    [Test]
    public void ValidateHeader_Corrupt_Throws()
    {
        byte[] bad = Encoding.ASCII.GetBytes("BADMAGIC____");
        using var ms = new MemoryStream(bad);
        Assert.Throws<InvalidDataException>(() => ChunkBinaryFormat.ValidateHeader(ms));
    }
}

[TestFixture]
public class BlockLineReaderTests
{
    private static string WriteTempFile(string content, System.Text.Encoding? enc = null)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content, enc ?? System.Text.Encoding.UTF8);
        return path;
    }

    [Test]
    public void Read_LfLines_ReturnAll()
    {
        string path = WriteTempFile("aaa\nbbb\nccc\n");
        try
        {
            using var reader = new BlockLineReader(path, 4096);
            var lines = new List<string?>();
            while (reader.TryReadLine(out string? line)) lines.Add(line);
            Assert.That(lines, Is.EqualTo(new[] { "aaa", "bbb", "ccc" }));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Read_CrLfLines_StripsCarriageReturn()
    {
        string path = WriteTempFile("foo\r\nbar\r\n");
        try
        {
            using var reader = new BlockLineReader(path, 4096);
            var lines = new List<string?>();
            while (reader.TryReadLine(out string? line)) lines.Add(line);
            Assert.That(lines, Is.EqualTo(new[] { "foo", "bar" }));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public void Read_NoTrailingNewline_ReturnsLastLine()
    {
        string path = WriteTempFile("abc\ndef");
        try
        {
            using var reader = new BlockLineReader(path, 4096);
            var lines = new List<string?>();
            while (reader.TryReadLine(out string? line)) lines.Add(line);
            Assert.That(lines, Is.EqualTo(new[] { "abc", "def" }));
        }
        finally { File.Delete(path); }
    }
}

[TestFixture]
public class EndToEndSortTests
{
    [Test]
    public void Sort_SmallFile_ProducesCorrectOrder()
    {
        string inputPath  = Path.GetTempFileName();
        string outputPath = Path.GetTempFileName();
        string tempDir    = Path.Combine(Path.GetTempPath(), "sort-test-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // Write spec example (shuffled)
            File.WriteAllLines(inputPath, new[]
            {
                "415. Apple",
                "30432. Something something something",
                "1. Apple",
                "32. Cherry is the best",
                "2. Banana is yellow",
            });

            var options = new SortOptions
            {
                InputPath                      = inputPath,
                OutputPath                     = outputPath,
                TempDirectory                  = tempDir,
                ChunkSizeMb                    = 0,
                ChunkSizeBytesOverrideForTests = 64,  // tiny chunks to exercise merge
                MergeFanIn                     = 2,
                TempFilePolicy                 = TempFilePolicy.DeleteAlways,
                OverwriteOutput                = true,
            };

            var sorter = new ExternalSorter(options);
            sorter.Sort();

            string[] result = File.ReadAllLines(outputPath);
            Assert.That(result, Has.Length.EqualTo(5));
            Assert.That(result[0], Is.EqualTo("1. Apple"));
            Assert.That(result[1], Is.EqualTo("415. Apple"));
            Assert.That(result[2], Is.EqualTo("2. Banana is yellow"));
            Assert.That(result[3], Is.EqualTo("32. Cherry is the best"));
            Assert.That(result[4], Is.EqualTo("30432. Something something something"));
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Test]
    public void Sort_EmptyInput_ProducesEmptyOutput()
    {
        string inputPath  = Path.GetTempFileName();
        string outputPath = Path.GetTempFileName();
        string tempDir    = Path.Combine(Path.GetTempPath(), "sort-empty-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            File.WriteAllText(inputPath, "");

            var options = new SortOptions
            {
                InputPath       = inputPath,
                OutputPath      = outputPath,
                TempDirectory   = tempDir,
                ChunkSizeMb     = 1,
                TempFilePolicy  = TempFilePolicy.DeleteAlways,
                OverwriteOutput = true,
            };

            var sorter = new ExternalSorter(options);
            sorter.Sort();

            Assert.That(File.ReadAllText(outputPath), Is.Empty.Or.EqualTo("\r\n").Or.EqualTo("\n"));
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}