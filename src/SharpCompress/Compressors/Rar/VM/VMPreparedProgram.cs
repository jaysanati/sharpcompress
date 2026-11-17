namespace SharpCompress.Compressors.Rar.VM;

internal class VMPreparedProgram
{
    internal VMStandardFilters Type { get; set; }
    internal int[] InitR = new int[7];
    internal int FilteredDataOffset { get; set; }
    internal int FilteredDataSize { get; set; }
}
