using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar.Headers;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Lzw;
using SharpCompress.Compressors.PPMd.H;
using SharpCompress.Compressors.Rar;
using SharpCompress.Compressors.Rar.UnpackV1;
using SharpCompress.Compressors.Rar.VM;
using SharpCompress.IO;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;
using Xunit;
using Unpack3 = SharpCompress.Compressors.Rar.UnpackV1.Unpack;
using Unpack5 = SharpCompress.Compressors.Rar.UnpackV2017.Unpack;

namespace SharpCompress.Test.Rar;

[Collection(SharpCompress.Test.Streams.DictionarySizeLimitCollection.Name)]
public class RarDecoderBoundsTests : TestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Rar3_Custom_Jump_Program_Is_Rejected(int target)
    {
        // No static data, JMP with an immediate relative target: self or past end.
        var programBits = "0" + "100000" + "00" + DataBits(target);
        var code = new List<byte> { 0 };
        code.AddRange(Bits(programBits));
        for (var i = 1; i < code.Count; i++)
        {
            code[0] ^= code[i];
        }
        var record = Bits(
            DataBits(0)
                + DataBits(1)
                + DataBits(code.Count)
                + string.Concat(code.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')))
        );
        using var unpack = new Unpack3();
        Assert.Throws<InvalidFormatException>(() => Call(unpack, "AddVMCode", 0x20, record));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    [InlineData(0x40000000)]
    [InlineData(-1)]
    public void Rar3_Delta_And_Audio_Reject_Invalid_Channels(int channels)
    {
        using var vm = new RarVM();
        foreach (var type in new[] { VMStandardFilters.VMSF_DELTA, VMStandardFilters.VMSF_AUDIO })
        {
            var program = new VMPreparedProgram { Type = type };
            program.InitR[0] = channels;
            program.InitR[4] = 4;
            Assert.Throws<InvalidFormatException>(() => vm.execute(program));
        }
    }

    [Theory]
    [InlineData(-1, 3, 0)]
    [InlineData(131073, 3, 0)]
    [InlineData(8, 2, 0)]
    [InlineData(8, 12, 0)]
    [InlineData(8, 3, 3)]
    public void Rar3_RGB_Rejects_Invalid_Output_And_Width(int size, int width, int position)
    {
        using var vm = new RarVM();
        var program = new VMPreparedProgram { Type = VMStandardFilters.VMSF_RGB };
        program.InitR[0] = width;
        program.InitR[1] = position;
        program.InitR[4] = size;
        Assert.Throws<InvalidFormatException>(() => vm.execute(program));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Rar3_Narrow_RGB_Does_Not_Read_Previous_Filter_Data(int width)
    {
        using var vm = new RarVM();
        var program = new VMPreparedProgram { Type = VMStandardFilters.VMSF_RGB };
        program.InitR[0] = width;
        program.InitR[4] = 12;
        vm.Mem.AsSpan(0, 12).Fill(1);
        vm.execute(program);
        var expected = vm.Mem.AsSpan(12, 12).ToArray();
        vm.Mem.AsSpan(12, 12).Fill(0xA5);
        vm.execute(program);
        Assert.Equal(expected, vm.Mem.AsSpan(12, 12).ToArray());
    }

    [Fact]
    public void Rar3_Filter_Stack_Is_Bounded_And_Reuses_Executed_Slots()
    {
        using var unpack = new Unpack3();
        Get<List<UnpackFilter>>(unpack, "filters").Add(new UnpackFilter());
        Get<List<int>>(unpack, "oldFilterLengths").Add(1);
        var stack = Get<List<UnpackFilter>>(unpack, "prgStack");
        var record = Bits(DataBits(0));
        for (var i = 0; i < 8192; i++)
        {
            Assert.True((bool)Call(unpack, "AddVMCode", 0, record)!);
        }
        Assert.Throws<InvalidFormatException>(() => Call(unpack, "AddVMCode", 0, record));
        stack[0] = null!;
        Assert.True((bool)Call(unpack, "AddVMCode", 0, record)!);
        Assert.Equal(8192, stack.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar3_Chained_Delta_Filters_Read_VM_Output(bool useAsync)
    {
        using var unpack = new Unpack3();
        Call(unpack, "Init");
        var window = Get<byte[]>(unpack, "window");
        new byte[] { 1, 2, 3, 4 }.CopyTo(window, 0);
        Set(unpack, "unpPtr", 4);
        unpack.DestSize = 4;
        using var output = new MemoryStream();
        Set(unpack, "writeStream", output);
        var stack = Get<List<UnpackFilter>>(unpack, "prgStack");
        for (var i = 0; i < 2; i++)
        {
            var filter = new UnpackFilter { BlockLength = 4 };
            filter.Program.Type = VMStandardFilters.VMSF_DELTA;
            filter.Program.InitR[0] = 1;
            filter.Program.InitR[4] = 4;
            stack.Add(filter);
        }
        if (useAsync)
        {
            await (ValueTask)Call(unpack, "UnpWriteBufAsync", CancellationToken.None)!;
        }
        else
        {
            Call(unpack, "UnpWriteBuf");
        }
        Assert.Equal(new byte[] { 1, 4, 10, 20 }, output.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Rar3_Negative_Filter_Position_Is_A_Format_Error(int position)
    {
        using var unpack = new Unpack3();
        Assert.Throws<InvalidFormatException>(() =>
            Call(unpack, "AddVMCode", 0x80, Bits(DataBits(position)))
        );
    }

    [Fact]
    public void Rar_Ppmd_Resets_Reuse_Heap_And_Preserve_Logical_Size()
    {
        using var allocator = new SubAllocator();
        allocator.StartSubAllocator(4 << 20);
        var heap = allocator.Heap;
        heap[heap.Length - 1] = 0xA5;
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
        {
            var size = (i % 2 == 0 ? 1 : 4) << 20;
            allocator.StartSubAllocator(size);
            allocator.InitSubAllocator();
            Assert.Equal(size, allocator.GetAllocatedMemory());
            Assert.Same(heap, allocator.Heap);
        }
        Assert.Equal(0xA5, heap[heap.Length - 1]);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Rar_Ppmd_Stopped_Model_Does_Not_Decode_Returned_Heap()
    {
        using var model = new ModelPpm();
        using var source = new MemoryStream(new byte[32]);
        Assert.True(model.DecodeInit(source, 2, 1 << 20));
        Assert.False(model.DecodeInit(source, 1, 1 << 20));
        Assert.Null(Get<object?>(model, "_minContext"));
        Assert.Null(Get<object?>(model, "_maxContext"));
        Assert.Equal(-1, model.DecodeChar());
        Assert.Equal(-1, await model.DecodeCharAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar_Ppm_Byte_Read_Detects_EOF(bool useAsync)
    {
        using var unpack = new Unpack3();
        Set(unpack, "readStream", Stream.Null);
        unpack.InBuf.AsSpan().Fill(0xA5);
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await unpack.ReadCharAsync(CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() => unpack.ReadChar());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar5_Stored_Output_Is_Clamped(bool useAsync)
    {
        using var unpack = new Unpack5();
        var header = Header(50, 3, stored: true);
        using var input = new MemoryStream(new byte[64]);
        using var output = new MemoryStream();
        if (useAsync)
        {
            await unpack.DoUnpackAsync(header, input, output, CancellationToken.None);
        }
        else
        {
            unpack.DoUnpack(header, input, output);
        }
        Assert.Equal(3, output.Length);
        Assert.Equal(3, input.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar5_Filter_Output_Is_Clamped(bool useAsync)
    {
        using var unpack = new Unpack5();
        Set(unpack, "fileHeader", Header(50, 0));
        Call(unpack, "Init", 0x40000L, false);
        Set(unpack, "UnpPtr", (uint)64);
        Set(unpack, "DestUnpSize", 3L);
        using var output = new MemoryStream();
        Set(unpack, "writeStream", output);
        Get<List<SharpCompress.Compressors.Rar.UnpackV2017.UnpackFilter>>(unpack, "Filters")
            .Add(
                new SharpCompress.Compressors.Rar.UnpackV2017.UnpackFilter
                {
                    Type = 0,
                    BlockLength = 64,
                    Channels = 1,
                }
            );
        if (useAsync)
        {
            await (ValueTask)Call(unpack, "UnpWriteBufAsync", CancellationToken.None)!;
        }
        else
        {
            Call(unpack, "UnpWriteBuf");
        }
        Assert.Equal(3, output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar3_First_Solid_Entry_Has_An_Initialized_Window(bool useAsync)
    {
        using var unpack = new Unpack3();
        var header = Header(29, 1);
        Property(header, "IsSolid", true);
        if (useAsync)
        {
            await unpack.DoUnpackAsync(header, Stream.Null, Stream.Null, CancellationToken.None);
        }
        else
        {
            unpack.DoUnpack(header, Stream.Null, Stream.Null);
        }
        Assert.NotNull(Get<byte[]>(unpack, "window"));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    public void Rar5_Large_Dictionary_Has_Typed_Error(int exponent)
    {
        using var unpack = new Unpack5();
        var header = Header(50, 1);
        var size = 0x20000L << exponent;
        Property(header, "WindowSize", size);
        var error = Assert.Throws<NotSupportedException>(() =>
            unpack.DoUnpack(header, Stream.Null, Stream.Null)
        );
        Assert.Contains(size.ToString(), error.Message);
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(13, true)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    public async Task Rar5_Header_Dictionary_Shift_Is_64Bit(int exponent, bool useAsync)
    {
        var file = new List<byte> { 2, 2, 0, 0, 1, 0 };
        file.AddRange(VInt((ulong)((exponent << 10) | (3 << 7))));
        file.AddRange(new byte[] { 0, 1, (byte)'x' });
        var bytes = new List<byte> { 0x52, 0x61, 0x72, 0x21, 0x1A, 7, 1, 0 };
        bytes.AddRange(Rar5Header(new byte[] { 1, 0, 0 }));
        bytes.AddRange(Rar5Header(file.ToArray()));
        bytes.AddRange(Rar5Header(new byte[] { 5, 0, 0 }));
        using var source = new MemoryStream(bytes.ToArray());
        var factory = new RarHeaderFactory(StreamingMode.Seekable, ReaderOptions.ForExternalStream);
        FileHeader? header = null;
        if (useAsync)
        {
            await foreach (
                var item in factory
                    .ReadHeadersAsync(source)
                    .WithCancellation(CancellationToken.None)
            )
            {
                if (item is FileHeader parsed)
                {
                    header = parsed;
                }
            }
        }
        else
        {
            header = factory.ReadHeaders(source).OfType<FileHeader>().Single();
        }
        Assert.NotNull(header);
        Assert.Equal(0x20000L << exponent, header.WindowSize);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(50, false)]
    [InlineData(29, true)]
    [InlineData(50, true)]
    public async Task Rar_Truncated_Compressed_Data_Is_A_Format_Error(byte version, bool useAsync)
    {
        using var unpack = version == 29 ? (IDisposable)new Unpack3() : new Unpack5();
        // PPM reset header, memory size, then an incomplete range coder seed.
        using var input = new MemoryStream(
            version == 29 ? new byte[] { 0xA1, 0, 0 } : Array.Empty<byte>()
        );
        using var stream = new RarStream((IRarUnpack)unpack, Header(version, 16), input);
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await stream.CopyToAsync(Stream.Null, 1024, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() => stream.CopyTo(Stream.Null));
        }
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(29, false)]
    [InlineData(20, true)]
    [InlineData(29, true)]
    public async Task Rar_Table_Cannot_Repeat_Previous_Length_At_Zero(int version, bool useAsync)
    {
        using var unpack = new Unpack3();
        var count = version == 20 ? 19 : 20;
        var table =
            "00"
            + string.Concat(
                Enumerable.Range(0, count).Select(i => i == 0 || i == 16 ? "0001" : "0000")
            )
            + "100";
        Bits(table).ToArray().CopyTo(unpack.InBuf, 0);
        Set(unpack, "readTop", 128);
        var name = version == 20 ? "ReadTables20" : "ReadTables";
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await (ValueTask<bool>)Call(unpack, name + "Async", CancellationToken.None)!
            );
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() => Call(unpack, name));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar_Ppmd_Heap_Enforces_Memory_Cap(bool useAsync)
    {
        var previousPpmd = DictionarySizeLimits.Ppmd;
        try
        {
            DictionarySizeLimits.Ppmd = 1 << 20;
            using var unpack = new Unpack3();
            using var input = new MemoryStream(new byte[] { 0x21, 1, 0, 0, 0, 0 });
            Set(unpack, "readStream", input);
            using var model = new ModelPpm();
            if (useAsync)
            {
                await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                    await model.DecodeInitAsync(unpack, 0, CancellationToken.None)
                );
            }
            else
            {
                Assert.Throws<InvalidFormatException>(() => model.DecodeInit(unpack, 0));
            }
            Assert.Equal(0, model.SubAlloc.GetAllocatedMemory());
        }
        finally
        {
            DictionarySizeLimits.Ppmd = previousPpmd;
        }
    }

    [Theory]
    [InlineData("Rar.rar", false)]
    [InlineData("Rar5.rar", false)]
    [InlineData("Rar.rar", true)]
    [InlineData("Rar5.rar", true)]
    public async Task Rar_Archive_And_Reader_Enforce_Memory_Cap(string name, bool useAsync)
    {
        var previousRar4 = DictionarySizeLimits.Rar4;
        var previousRar5 = DictionarySizeLimits.Rar5;
        try
        {
            DictionarySizeLimits.Rar4 = 1;
            DictionarySizeLimits.Rar5 = 1;
            var options = new ReaderOptions { LeaveStreamOpen = true };
            using var source = File.OpenRead(Path.Combine(TEST_ARCHIVES_PATH, name));
            if (useAsync)
            {
                await using var archive = await RarArchive.OpenAsyncArchive(
                    source,
                    options,
                    CancellationToken.None
                );
                var entry = await archive.EntriesAsync.Where(e => !e.IsDirectory).FirstAsync();
                await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                    await entry.WriteToAsync(Stream.Null, cancellationToken: CancellationToken.None)
                );
            }
            else
            {
                using var reader = RarReader.OpenReader(source, options);
                while (reader.MoveToNextEntry() && reader.Entry.IsDirectory) { }
                Assert.Throws<InvalidFormatException>(() => reader.WriteEntryTo(Stream.Null));
            }
            Assert.True(source.CanRead);
        }
        finally
        {
            DictionarySizeLimits.Rar4 = previousRar4;
            DictionarySizeLimits.Rar5 = previousRar5;
        }
    }

    [Fact]
    public void Rar3_Ppm_Match_Wraps_At_Window_End()
    {
        using var unpack = new Unpack3();
        Call(unpack, "Init");
        var window = Get<byte[]>(unpack, "window");
        const int size = 0x400000;
        window[size - 281] = 0xA5;
        Set(unpack, "unpPtr", size - 280);
        Call(unpack, "CopyString", 287, 1);
        Assert.Equal(7, Get<int>(unpack, "unpPtr"));
        Assert.All(window.Take(7), value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void Rar5_Maximum_Increased_Match_Wraps_At_Window_End()
    {
        using var unpack = new Unpack5();
        Set(unpack, "fileHeader", Header(50, 0));
        Call(unpack, "Init", 0x40000L, false);
        var window = Get<byte[]>(unpack, "Window");
        window[0x40000 - 0x1003] = 0xA5;
        Set(unpack, "UnpPtr", (uint)(0x40000 - 0x1002));
        Call(unpack, "CopyString", (uint)0x1004, (uint)1);
        Assert.Equal((uint)2, Get<uint>(unpack, "UnpPtr"));
        Assert.Equal(0xA5, window[0]);
        Assert.Equal(0xA5, window[1]);
    }

    [Fact]
    public void Rar_Matches_Cannot_Read_Previous_NonSolid_Data()
    {
        using var oldUnpack = new Unpack3();
        Call(oldUnpack, "Init");
        Get<byte[]>(oldUnpack, "window").AsSpan().Fill(0xA5);
        Assert.Throws<InvalidFormatException>(() => Call(oldUnpack, "CopyString", 3, 1));
        using var newUnpack = new Unpack5();
        Set(newUnpack, "fileHeader", Header(50, 0));
        Call(newUnpack, "Init", 0x40000L, false);
        Get<byte[]>(newUnpack, "Window").AsSpan().Fill(0xA5);
        Assert.Throws<InvalidFormatException>(() =>
            Call(newUnpack, "CopyString", (uint)3, (uint)1)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lzw_Truncated_Clear_Padding_Is_A_Format_Error(bool useAsync)
    {
        // Two 9-bit little-endian codes: literal 65, CLEAR 256, no group padding.
        using var source = new MemoryStream(new byte[] { 0x1F, 0x9D, 0x90, 0x41, 0, 2 });
        using var stream = new LzwStream(source);
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await stream.CopyToAsync(Stream.Null, 1024, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() => stream.CopyTo(Stream.Null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lzw_Prefix_Cycle_Is_A_Format_Error(bool useAsync)
    {
        // Literal 65 followed by code 257, both 9-bit little-endian codes.
        using var source = new MemoryStream(new byte[] { 0x1F, 0x9D, 0x90, 0x41, 2, 2 });
        using var stream = new LzwStream(source);
        Assert.Equal(65, stream.ReadByte());
        Set(stream, "freeEnt", 258);
        Get<int[]>(stream, "tabPrefix")[257] = 257;
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
                await stream.CopyToAsync(Stream.Null, 1024, CancellationToken.None)
            );
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() => stream.CopyTo(Stream.Null));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BZip2_Run_Cannot_Exceed_Block(bool useAsync)
    {
        // One used byte, two identical Huffman groups, one selector. RUNB is
        // code 01. Seventeen repetitions expand beyond this 100 KiB block.
        var bits =
            "0"
            + new string('0', 24)
            + "1000000000000000"
            + "1000000000000000"
            + "010"
            + "000000000000001"
            + "0"
            + "00010000"
            + "00010000"
            + string.Concat(Enumerable.Repeat("01", 17))
            + "10";
        var data = new byte[]
        {
            0x42,
            0x5A,
            0x68,
            0x31,
            0x31,
            0x41,
            0x59,
            0x26,
            0x53,
            0x59,
            0,
            0,
            0,
            0,
        }
            .Concat(Bits(bits))
            .ToArray();
        using var source = new MemoryStream(data);
        if (useAsync)
        {
            await Assert.ThrowsAsync<InvalidFormatException>(async () =>
            {
                using var stream = await BZip2Stream.CreateAsync(
                    source,
                    CompressionMode.Decompress,
                    false,
                    cancellationToken: CancellationToken.None
                );
                await stream.CopyToAsync(Stream.Null, 1024, CancellationToken.None);
            });
        }
        else
        {
            Assert.Throws<InvalidFormatException>(() =>
            {
                using var stream = BZip2Stream.Create(source, CompressionMode.Decompress, false);
                stream.CopyTo(Stream.Null);
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rar_Old_Window_Flush_Clamps_Declared_Output(bool useAsync)
    {
        using var unpack = new Unpack3();
        Call(unpack, "Init");
        Set(unpack, "fileHeader", Header(20, 3));
        Set(unpack, "unpPtr", 64);
        using var output = new MemoryStream();
        Set(unpack, "writeStream", output);
        if (useAsync)
        {
            await (ValueTask)Call(unpack, "oldUnpWriteBufAsync", CancellationToken.None)!;
        }
        else
        {
            Call(unpack, "oldUnpWriteBuf");
        }
        Assert.Equal(3, output.Length);
    }

    private static FileHeader Header(byte version, long size, bool stored = false)
    {
        var header = new FileHeader();
        typeof(RarHeader)
            .GetField("_isRar5", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(header, version >= 50);
        Property(header, "CompressionAlgorithm", version);
        Property(header, "CompressionMethod", (byte)(stored ? 0 : 3));
        Property(header, "UncompressedSize", size);
        Property(header, "WindowSize", 0x40000L);
        return header;
    }

    private static string DataBits(int value) => "11" + Convert.ToString(value, 2).PadLeft(32, '0');

    private static byte[] Rar5Header(byte[] body)
    {
        var data = VInt((ulong)body.Length).Concat(body).ToArray();
        var crc = RarCRC.CheckCrc(0xFFFFFFFF, data, 0, data.Length) ^ 0xFFFFFFFF;
        return BitConverter.GetBytes(crc).Concat(data).ToArray();
    }

    private static byte[] VInt(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 127);
            value >>= 7;
            bytes.Add((byte)(next | (value == 0 ? 0 : 128)));
        } while (value != 0);
        return bytes.ToArray();
    }

    private static List<byte> Bits(string bits)
    {
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        var bytes = new List<byte>();
        for (var i = 0; i < bits.Length; i += 8)
        {
            bytes.Add(Convert.ToByte(bits.Substring(i, 8), 2));
        }
        return bytes;
    }

    private static T Get<T>(object target, string name) =>
        (T)
            target
                .GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(target)!;

    private static void Set(object target, string name, object value) =>
        target
            .GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static void Property(object target, string name, object value) =>
        target
            .GetType()
            .GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            )!
            .SetValue(target, value);

    private static object? Call(object target, string name, params object[] arguments)
    {
        try
        {
            var method = target
                .GetType()
                .GetMethod(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    arguments.Select(a => a.GetType()).ToArray(),
                    null
                )!;
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
