namespace Common
{
    public sealed class ProgressReporter
    {
        private readonly string _prefix;
        private DateTime _lastReportUtc;
        private readonly TimeSpan _interval;

        public ProgressReporter(string prefix, TimeSpan? interval = null)
        {
            _prefix = prefix;
            _interval = interval ?? TimeSpan.FromSeconds(2);
            _lastReportUtc = DateTime.MinValue;
        }

        public void Report(string message, bool force = false)
        {
            DateTime now = DateTime.UtcNow;
            if (!force && now - _lastReportUtc < _interval)
            {
                return;
            }

            _lastReportUtc = now;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_prefix} {message}");
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.##} {units[unit]}";
        }

        public static string FormatRate(long bytes, TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds <= 0)
            {
                return "n/a";
            }

            double rate = bytes / elapsed.TotalSeconds;
            return $"{FormatBytes((long)rate)}/s";
        }
    }
}