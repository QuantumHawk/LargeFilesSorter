using System.Globalization;
using System.Text;
using Common;

namespace TestFileGenerator;

public sealed class TestDataGenerator
{
    public void Generate(GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)) ?? ".");

        long targetBytes = options.TargetSizeMb * 1024L * 1024L;
        var random = new Random(options.Seed);
        var progress = new ProgressReporter("generator");

        List<string> pool = BuildStringPool(options.DistinctStrings, random);

        long writtenBytes = 0;
        long lineCount = 0;
        DateTime startedAt = DateTime.UtcNow;

        using var stream = new FileStream(
            options.OutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            options.WriterBufferSize);

        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            options.WriterBufferSize);

        while (writtenBytes < targetBytes)
        {
            ulong number = NextUInt64(random, 1_000_000_000_000_000_000UL);
            string text = pool[random.Next(pool.Count)];

            string line = string.Create(CultureInfo.InvariantCulture, $"{number}. {text}");
            writer.WriteLine(line);

            writtenBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            lineCount++;

            if (lineCount % 1_000_000 == 0)
            {
                progress.Report(
                    $"written={lineCount:n0} lines, size≈{ProgressReporter.FormatBytes(writtenBytes)}, " +
                    $"rate={ProgressReporter.FormatRate(writtenBytes, DateTime.UtcNow - startedAt)}");
            }
        }

        writer.Flush();

        progress.Report(
            $"done, lines={lineCount:n0}, size≈{ProgressReporter.FormatBytes(writtenBytes)}",
            force: true);
    }

    private static List<string> BuildStringPool(int count, Random random)
    {
        string[] adjectives =
        [
            "quick", "silent", "blue", "bright", "ancient", "fancy", "gentle", "rapid", "lucky", "wild",
            "red", "golden", "curious", "wise", "bold", "calm", "fresh", "smart", "cool", "warm"
        ];

        string[] nouns =
        [
            "apple", "banana", "cherry", "forest", "river", "mountain", "planet", "ocean", "window", "engine",
            "market", "system", "garden", "library", "castle", "signal", "bridge", "flower", "device", "paper"
        ];

        string[] suffixes =
        [
            "is great", "for testing", "and more", "in the morning", "under load", "with many duplicates",
            "for external sort", "in production", "at scale", "with random data", "for benchmark"
        ];

        var result = new List<string>(count);

        for (int i = 0; i < count; i++)
        {
            int wordCount = random.Next(2, 8);
            var sb = new StringBuilder(64);

            for (int w = 0; w < wordCount; w++)
            {
                if (w > 0)
                {
                    sb.Append(' ');
                }

                int choice = random.Next(3);
                switch (choice)
                {
                    case 0:
                        sb.Append(adjectives[random.Next(adjectives.Length)]);
                        break;
                    case 1:
                        sb.Append(nouns[random.Next(nouns.Length)]);
                        break;
                    default:
                        sb.Append(suffixes[random.Next(suffixes.Length)]);
                        break;
                }
            }

            string value = sb.ToString();
            if (value.Length > 100)
            {
                value = value[..100];
            }

            result.Add(value);
        }

        return result;
    }

    private static ulong NextUInt64(Random random, ulong maxExclusive)
    {
        Span<byte> buffer = stackalloc byte[8];
        random.NextBytes(buffer);
        ulong value = BitConverter.ToUInt64(buffer);
        return value % maxExclusive;
    }
}