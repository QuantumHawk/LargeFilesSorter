using Common;

namespace TestFileGenerator;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        if (!long.TryParse(args[1], out long targetSizeMb) || targetSizeMb <= 0)
        {
            Console.Error.WriteLine("Invalid targetSizeMb.");
            return 1;
        }

        int distinctStrings = 10000;
        if (args.Length >= 3 && (!int.TryParse(args[2], out distinctStrings) || distinctStrings <= 0))
        {
            Console.Error.WriteLine("Invalid distinctStrings.");
            return 1;
        }

        int seed = 12345;
        if (args.Length >= 4 && !int.TryParse(args[3], out seed))
        {
            Console.Error.WriteLine("Invalid seed.");
            return 1;
        }

        try
        {
            var options = new GeneratorOptions
            {
                OutputPath = args[0],
                TargetSizeMb = targetSizeMb,
                DistinctStrings = distinctStrings,
                Seed = seed
            };

            var generator = new TestDataGenerator();
            generator.Generate(options);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  TestFileGenerator <outputPath> <targetSizeMb> [distinctStrings=10000] [seed=12345]");
    }
}