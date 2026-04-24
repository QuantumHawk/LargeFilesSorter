using Common;

namespace LargeFileSorter
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || HasHelp(args))
                {
                    PrintUsage();
                    return 0;
                }

                ParseResult parseResult = ParseArguments(args);
                if (!parseResult.Success)
                {
                    Console.Error.WriteLine(parseResult.ErrorMessage);
                    Console.Error.WriteLine();
                    PrintUsage();
                    return 1;
                }

                var sorter = new ExternalSorter(parseResult.Options!);
                sorter.Sort();

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 2;
            }
        }

        private static bool HasHelp(string[] args)
        {
            return args.Any(a =>
                string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/?", StringComparison.OrdinalIgnoreCase));
        }

        private static ParseResult ParseArguments(string[] args)
        {
            if (args.Length < 2)
            {
                return ParseResult.Fail("At least <inputPath> and <outputPath> are required.");
            }

            string inputPath = args[0];
            string outputPath = args[1];

            string tempDir = Path.Combine(Environment.CurrentDirectory, "tmp-sort");
            int chunkSizeMb = 512;
            long? chunkSizeBytesOverrideForTests = null;
            int mergeFanIn = 64;

            int maxParallelChunkSorters = Math.Max(1, Environment.ProcessorCount / 2);
            int chunkQueueCapacity = 2;
            int maxConcurrentMerges = 2;

            InvalidLineMode invalidLineMode = InvalidLineMode.Strict;
            string? invalidLogPath = null;
            string? metricsPath = null;

            bool resumeIfManifestExists = true;
            TempFilePolicy tempFilePolicy = TempFilePolicy.DeleteOnSuccess;
            bool useBlockReader = true;
            bool overwriteOutput = true;

            int readerBufferSize = 1 << 20;
            int writerBufferSize = 1 << 20;
            int inputReadBufferBytes = 4 * 1024 * 1024;
            int chunkWriteBufferBytes = 1 * 1024 * 1024;
            int finalWriteBufferBytes = 1 * 1024 * 1024;

            int i = 2;
            while (i < args.Length)
            {
                string arg = args[i];

                switch (arg)
                {
                    case "--chunk-size-mb":
                        chunkSizeMb = ParseIntOption(args, ref i, "--chunk-size-mb");
                        break;

                    case "--chunk-size-bytes-override-for-tests":
                        chunkSizeBytesOverrideForTests = ParseLongOption(args, ref i, "--chunk-size-bytes-override-for-tests");
                        break;

                    case "--temp-dir":
                        tempDir = ParseStringOption(args, ref i, "--temp-dir");
                        break;

                    case "--merge-fan-in":
                        mergeFanIn = ParseIntOption(args, ref i, "--merge-fan-in");
                        break;

                    case "--max-parallel-chunk-sorters":
                        maxParallelChunkSorters = ParseIntOption(args, ref i, "--max-parallel-chunk-sorters");
                        break;

                    case "--chunk-queue-capacity":
                        chunkQueueCapacity = ParseIntOption(args, ref i, "--chunk-queue-capacity");
                        break;

                    case "--max-concurrent-merges":
                        maxConcurrentMerges = ParseIntOption(args, ref i, "--max-concurrent-merges");
                        break;

                    case "--invalid-mode":
                    {
                        string value = ParseStringOption(args, ref i, "--invalid-mode");
                        if (!Enum.TryParse<InvalidLineMode>(value, ignoreCase: true, out invalidLineMode))
                        {
                            return ParseResult.Fail(
                                $"Invalid value for --invalid-mode: {value}. Allowed: Strict, SkipInvalid, LogInvalid.");
                        }
                        break;
                    }

                    case "--invalid-log":
                        invalidLogPath = ParseStringOption(args, ref i, "--invalid-log");
                        break;

                    case "--metrics-path":
                        metricsPath = ParseStringOption(args, ref i, "--metrics-path");
                        break;

                    case "--resume":
                        resumeIfManifestExists = true;
                        i++;
                        break;

                    case "--no-resume":
                        resumeIfManifestExists = false;
                        i++;
                        break;

                    case "--keep-temp":
                        tempFilePolicy = TempFilePolicy.KeepAll;
                        i++;
                        break;

                    case "--delete-temp-on-failure":
                        tempFilePolicy = TempFilePolicy.DeleteAlways;
                        i++;
                        break;

                    case "--use-block-reader":
                        useBlockReader = true;
                        i++;
                        break;

                    case "--no-block-reader":
                        useBlockReader = false;
                        i++;
                        break;

                    case "--overwrite-output":
                        overwriteOutput = true;
                        i++;
                        break;

                    case "--no-overwrite-output":
                        overwriteOutput = false;
                        i++;
                        break;

                    case "--reader-buffer-size":
                        readerBufferSize = ParseIntOption(args, ref i, "--reader-buffer-size");
                        break;

                    case "--writer-buffer-size":
                        writerBufferSize = ParseIntOption(args, ref i, "--writer-buffer-size");
                        break;

                    case "--input-read-buffer-bytes":
                        inputReadBufferBytes = ParseIntOption(args, ref i, "--input-read-buffer-bytes");
                        break;

                    case "--chunk-write-buffer-bytes":
                        chunkWriteBufferBytes = ParseIntOption(args, ref i, "--chunk-write-buffer-bytes");
                        break;

                    case "--final-write-buffer-bytes":
                        finalWriteBufferBytes = ParseIntOption(args, ref i, "--final-write-buffer-bytes");
                        break;

                    default:
                        return ParseResult.Fail($"Unknown argument: {arg}");
                }
            }

            if (invalidLineMode == InvalidLineMode.LogInvalid && string.IsNullOrWhiteSpace(invalidLogPath))
            {
                return ParseResult.Fail("--invalid-log must be specified when --invalid-mode LogInvalid is used.");
            }

            var options = new SortOptions
            {
                InputPath = inputPath,
                OutputPath = outputPath,
                TempDirectory = tempDir,

                ChunkSizeMb = chunkSizeMb,
                ChunkSizeBytesOverrideForTests = chunkSizeBytesOverrideForTests,
                MergeFanIn = mergeFanIn,

                MaxParallelChunkSorters = maxParallelChunkSorters,
                ChunkQueueCapacity = chunkQueueCapacity,
                MaxConcurrentMerges = maxConcurrentMerges,

                InvalidLineMode = invalidLineMode,
                InvalidLinesLogPath = invalidLogPath,
                MetricsPath = metricsPath,

                ResumeIfManifestExists = resumeIfManifestExists,
                TempFilePolicy = tempFilePolicy,

                UseBlockReader = useBlockReader,
                OverwriteOutput = overwriteOutput,

                ReaderBufferSize = readerBufferSize,
                WriterBufferSize = writerBufferSize,
                InputReadBufferBytes = inputReadBufferBytes,
                ChunkWriteBufferBytes = chunkWriteBufferBytes,
                FinalWriteBufferBytes = finalWriteBufferBytes,

                VerboseProgress = true
            };

            return ParseResult.Ok(options);
        }

        private static int ParseIntOption(string[] args, ref int index, string optionName)
        {
            string value = ParseStringOption(args, ref index, optionName);

            if (!int.TryParse(value, out int result))
            {
                throw new ArgumentException($"Invalid integer value for {optionName}: {value}");
            }

            return result;
        }

        private static long ParseLongOption(string[] args, ref int index, string optionName)
        {
            string value = ParseStringOption(args, ref index, optionName);

            if (!long.TryParse(value, out long result))
            {
                throw new ArgumentException($"Invalid long value for {optionName}: {value}");
            }

            return result;
        }

        private static string ParseStringOption(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {optionName}");
            }

            string value = args[index + 1];
            index += 2;
            return value;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  LargeFileSorter <inputPath> <outputPath> [options]");
            Console.WriteLine();
            Console.WriteLine("Required:");
            Console.WriteLine("  <inputPath>                         Path to source text file");
            Console.WriteLine("  <outputPath>                        Path to sorted output file");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --chunk-size-mb <int>               Chunk size in MB (default: 512)");
            Console.WriteLine("  --chunk-size-bytes-override-for-tests <long>");
            Console.WriteLine("                                      Exact chunk size in bytes, useful for tests");
            Console.WriteLine("  --temp-dir <path>                   Temp directory (default: ./tmp-sort)");
            Console.WriteLine("  --merge-fan-in <int>                Number of files merged per group (default: 64)");
            Console.WriteLine("  --max-parallel-chunk-sorters <int>  Number of parallel chunk sort workers");
            Console.WriteLine("  --chunk-queue-capacity <int>        Bounded queue capacity for split pipeline");
            Console.WriteLine("  --max-concurrent-merges <int>       Max parallel merge jobs per pass");
            Console.WriteLine("  --invalid-mode <mode>               Strict | SkipInvalid | LogInvalid");
            Console.WriteLine("  --invalid-log <path>                File for invalid lines when LogInvalid is used");
            Console.WriteLine("  --metrics-path <path>               Save metrics JSON to this file");
            Console.WriteLine("  --resume                            Resume from manifest if it exists (default)");
            Console.WriteLine("  --no-resume                         Ignore existing manifest");
            Console.WriteLine("  --keep-temp                         Keep all temp files (TempFilePolicy=KeepAll)");
            Console.WriteLine("  --delete-temp-on-failure            Delete temp files on failure too (TempFilePolicy=DeleteAlways)");
            Console.WriteLine("  --use-block-reader                  Use block-based input reader (default)");
            Console.WriteLine("  --no-block-reader                   Use StreamReader-based line reading");
            Console.WriteLine("  --overwrite-output                  Overwrite output if exists (default)");
            Console.WriteLine("  --no-overwrite-output               Fail if output already exists");
            Console.WriteLine("  --reader-buffer-size <int>          Reader buffer size in bytes");
            Console.WriteLine("  --writer-buffer-size <int>          Writer buffer size in bytes");
            Console.WriteLine("  --input-read-buffer-bytes <int>     Block reader buffer size");
            Console.WriteLine("  --chunk-write-buffer-bytes <int>    Chunk file write buffer");
            Console.WriteLine("  --final-write-buffer-bytes <int>    Final text output write buffer");
            Console.WriteLine("  --help                              Show this help");
        }

        private sealed class ParseResult
        {
            public bool Success { get; }
            public SortOptions? Options { get; }
            public string? ErrorMessage { get; }

            private ParseResult(bool success, SortOptions? options, string? errorMessage)
            {
                Success = success;
                Options = options;
                ErrorMessage = errorMessage;
            }

            public static ParseResult Ok(SortOptions options) => new(true, options, null);

            public static ParseResult Fail(string message) => new(false, null, message);
        }
    }
}