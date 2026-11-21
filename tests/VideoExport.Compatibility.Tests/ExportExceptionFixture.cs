namespace AAVideoExport.Core;

// The compatibility fixture source-links only the adapter. Keep the exception
// shape identical to the production boundary without pulling Unity-facing Core
// options into this isolated Harmony test process.
public sealed class ExportException : Exception
{
    public string Code { get; }
    public ExportException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}
