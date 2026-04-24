using System.Text;

namespace Common
{
    public readonly record struct Record(ulong Number, byte[] Utf8Text)
    {
        // Called only in FinalizePhase — once per record at final text output
        public override string ToString()
            => $"{Number}. {Encoding.UTF8.GetString(Utf8Text)}";
    }
}