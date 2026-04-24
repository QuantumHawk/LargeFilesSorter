namespace Common
{
    public sealed class RecordComparer : IComparer<Record>
    {
        public static readonly RecordComparer Instance = new();

        private RecordComparer() { }

        /// <summary>
        /// Compares by UTF-8 text bytes first (ordinal byte order == correct lexicographic order
        /// for the same reason string.CompareOrdinal works — byte values are compared directly).
        /// SequenceCompareTo is SIMD-accelerated and operates on half the data vs UTF-16 strings.
        /// </summary>
        public int Compare(Record x, Record y)
        {
            int textCompare = x.Utf8Text.AsSpan().SequenceCompareTo(y.Utf8Text.AsSpan());
            if (textCompare != 0)
                return textCompare;

            return x.Number.CompareTo(y.Number);
        }
    }
}