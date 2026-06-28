namespace ArchiveImageLabler.Models;

public sealed class LibraryOptions
{
    public string RootPath { get; set; } = "/library";

    public string DataPath { get; set; } = "/app/data";

    public int MaxNestedZipDepth { get; set; } = 3;

    public int ScanSaveBatchSize { get; set; } = 250;

    public int ScanParallelism { get; set; } = Math.Min(Math.Max(Environment.ProcessorCount, 1), 8);

    public int DatabaseMutationBatchSize { get; set; } = 500;

    public long MaxNestedZipBytesInMemory { get; set; } = 256L * 1024L * 1024L;
}
