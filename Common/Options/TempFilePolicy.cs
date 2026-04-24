namespace Common
{
    /// <summary>
    /// Controls when temporary chunk/merge files are deleted.
    /// </summary>
    public enum TempFilePolicy
    {
        /// <summary>Never delete temp files (useful for debugging).</summary>
        KeepAll,

        /// <summary>Delete temp files only after a successful sort (default).</summary>
        DeleteOnSuccess,

        /// <summary>Delete temp files regardless of success or failure.</summary>
        DeleteAlways
    }
}

