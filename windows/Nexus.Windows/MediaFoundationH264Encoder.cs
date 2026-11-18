using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Windows;

/// <summary>
/// H.264 encoding with the encoder built into Windows (Media Foundation, CLSID_CMSH264EncoderMFT,
/// software, synchronous). No extra libraries. COM methods are called through their vtable slots;
/// slot numbers come from the Windows SDK headers (mfobjects.h, mftransform.h, strmif.h) and include
/// the three IUnknown methods. All calls must happen on one thread (the caller's), which this class
/// initialises for COM.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MediaFoundationH264Encoder : IDisposable
{
    // GUIDs from mfapi.h / codecapi.h / wmcodecdsp.h (checked against the SDK headers).
    private static readonly Guid ClsidH264Encoder = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IidTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid FormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    private const int NeedMoreInput = unchecked((int)0xc00d6d72);
    private const int StreamChange = unchecked((int)0xc00d6d61);
    private const int NotAccepting = unchecked((int)0xc00d36b5);
    private const uint ProvidesSamples = 0x100;

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private nint _transform;
    private nint _codecApi;
    private nint _outputSample;
    private nint _outputBuffer;
    private bool _providesSamples;
    private byte[] _sequenceHeader = [];
    private long _frameIndex;
    private bool _comInitialized;
    private bool _mfStarted;

    public MediaFoundationH264Encoder(int width, int height, int fps, int bitrateKbps)
    {
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("Width and height must be even.");
        }
        _width = width;
        _height = height;
        _fps = fps;
        try
        {
            _comInitialized = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */) >= 0;
            Check(MFStartup(0x00020070, 0), "MFStartup");
            _mfStarted = true;
            var clsid = ClsidH264Encoder;
            var iid = IidTransform;
            nint transform;
            Check(CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, &transform), "кодировщик H.264 не найден в Windows");
            _transform = transform;

            // Low-latency settings before the media types: the encoder decides how many frames to hold back
            // when the types are set (found on the owner's PC: frames waited inside the encoder, a long delay).
            var codecIid = IidCodecApi;
            nint codecApi;
            if (QueryInterface(_transform, &codecIid, &codecApi) >= 0)
            {
                _codecApi = codecApi;
                ApplyLowLatency();
            }

            // Output first (the encoder requires it), then input.
            var output = NewMediaType();
            try
            {
                SetGuid(output, MajorType, MediaTypeVideo);
                SetGuid(output, SubType, FormatH264);
                SetUInt32(output, AvgBitrate, (uint)(bitrateKbps * 1000));
                SetUInt64(output, FrameSize, ((ulong)(uint)width << 32) | (uint)height);
                SetUInt64(output, FrameRate, ((ulong)(uint)fps << 32) | 1);
                SetUInt64(output, PixelAspect, (1UL << 32) | 1);
                SetUInt32(output, InterlaceMode, 2 /* progressive */);
                SetUInt32(output, Profile, 66 /* baseline: no B-frames, lowest latency */);
                Check(Call<uint, nint, uint>(_transform, 16, 0, output, 0), "SetOutputType");
            }
            finally
            {
                Release(output);
            }
            var input = NewMediaType();
            try
            {
                SetGuid(input, MajorType, MediaTypeVideo);
                SetGuid(input, SubType, FormatNv12);
                SetUInt64(input, FrameSize, ((ulong)(uint)width << 32) | (uint)height);
                SetUInt64(input, FrameRate, ((ulong)(uint)fps << 32) | 1);
                SetUInt64(input, PixelAspect, (1UL << 32) | 1);
                SetUInt32(input, InterlaceMode, 2);
                Check(Call<uint, nint, uint>(_transform, 15, 0, input, 0), "SetInputType");
            }
            finally
            {
                Release(input);
            }

            // Again after the types: some Windows versions accept these settings only then.
            if (_codecApi != 0)
            {
                ApplyLowLatency();
            }

            ReadSequenceHeader();
            MftOutputStreamInfo info;
            Check(Call<uint, nint>(_transform, 7, 0, (nint)(&info)), "GetOutputStreamInfo");
            _providesSamples = (info.Flags & ProvidesSamples) != 0;
            if (!_providesSamples)
            {
                var size = (uint)Math.Max(info.Size, width * height * 2);
                nint buffer, sample;
                Check(MFCreateMemoryBuffer(size, &buffer), "MFCreateMemoryBuffer");
                _outputBuffer = buffer;
                Check(MFCreateSample(&sample), "MFCreateSample");
                _outputSample = sample;
                Check(Call<nint>(_outputSample, 42, _outputBuffer), "AddBuffer");
            }
            Call<uint, nint>(_transform, 23, 0x10000000 /* BEGIN_STREAMING */, 0);
            Call<uint, nint>(_transform, 23, 0x10000003 /* START_OF_STREAM */, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>False if the encoder ignores "force keyframe"; then the caller recreates it to get one.</summary>
    public bool CanForceKeyframe => _codecApi != 0;

    /// <summary>Encodes one NV12 frame. Returns zero or more access units (Annex-B), each with its keyframe flag.</summary>
    public IReadOnlyList<(byte[] Data, bool Key)> Encode(ReadOnlySpan<byte> nv12, bool forceKeyframe)
    {
        if (forceKeyframe && _codecApi != 0)
        {
            SetCodecValue(ForceKeyFrame, Variant.UInt32(1));
        }
        var results = new List<(byte[], bool)>();
        nint buffer = 0, sample = 0;
        try
        {
            Check(MFCreateMemoryBuffer((uint)nv12.Length, &buffer), "MFCreateMemoryBuffer");
            byte* data;
            uint max, current;
            Check(Call<nint, nint, nint>(buffer, 3, (nint)(&data), (nint)(&max), (nint)(&current)), "Lock");
            nv12.CopyTo(new Span<byte>(data, nv12.Length));
            Call(buffer, 4);
            Check(Call<uint>(buffer, 6, (uint)nv12.Length), "SetCurrentLength");
            Check(MFCreateSample(&sample), "MFCreateSample");
            Check(Call<nint>(sample, 42, buffer), "AddBuffer");
            var duration = 10_000_000L / _fps;
            Check(Call<long>(sample, 36, _frameIndex * duration), "SetSampleTime");
            Check(Call<long>(sample, 38, duration), "SetSampleDuration");
            _frameIndex++;

            var hr = Call<uint, nint, uint>(_transform, 24, 0, sample, 0);
            if (hr == NotAccepting)
            {
                Drain(results);
                hr = Call<uint, nint, uint>(_transform, 24, 0, sample, 0);
            }
            Check(hr, "ProcessInput");
            Drain(results);
        }
        finally
        {
            Release(sample);
            Release(buffer);
        }
        return results;
    }

    private void Drain(List<(byte[], bool)> results)
    {
        while (true)
        {
            var output = new MftOutputDataBuffer { StreamId = 0, Sample = _providesSamples ? 0 : _outputSample };
            if (!_providesSamples)
            {
                Call<uint>(_outputBuffer, 6, 0u); // reuse the buffer
            }
            uint status;
            var hr = Call<uint, uint, nint, nint>(_transform, 25, 0, 1, (nint)(&output), (nint)(&status));
            if (output.Events != 0)
            {
                Release(output.Events);
            }
            if (hr == NeedMoreInput)
            {
                return;
            }
            if (hr == StreamChange)
            {
                // The encoder changed its output format: take what it offers and continue.
                nint type;
                Check(Call<uint, uint, nint>(_transform, 14, 0, 0, (nint)(&type)), "GetOutputAvailableType");
                try
                {
                    Check(Call<uint, nint, uint>(_transform, 16, 0, type, 0), "SetOutputType");
                }
                finally
                {
                    Release(type);
                }
                ReadSequenceHeader();
                continue;
            }
            Check(hr, "ProcessOutput");
            var produced = output.Sample;
            try
            {
                nint contiguous;
                Check(Call<nint>(produced, 41, (nint)(&contiguous)), "ConvertToContiguousBuffer");
                try
                {
                    byte* data;
                    uint max, length;
                    Check(Call<nint, nint, nint>(contiguous, 3, (nint)(&data), (nint)(&max), (nint)(&length)), "Lock");
                    var bytes = new ReadOnlySpan<byte>(data, (int)length).ToArray();
                    Call(contiguous, 4);
                    if (bytes.Length > 0)
                    {
                        var key = H264.ContainsNal(bytes, 5);
                        if (key && !H264.ContainsNal(bytes, 7) && _sequenceHeader.Length > 0)
                        {
                            bytes = [.. _sequenceHeader, .. bytes]; // decoders need SPS/PPS before every IDR
                        }
                        results.Add((bytes, key));
                    }
                }
                finally
                {
                    Release(contiguous);
                }
            }
            finally
            {
                if (_providesSamples)
                {
                    Release(produced);
                }
            }
        }
    }

    private void ReadSequenceHeader()
    {
        nint type;
        if (Call<uint, nint>(_transform, 18, 0, (nint)(&type)) < 0)
        {
            return;
        }
        try
        {
            var key = SequenceHeader;
            uint size;
            if (Call<nint, nint>(type, 14, (nint)(&key), (nint)(&size)) < 0 || size == 0)
            {
                return;
            }
            var blob = new byte[size];
            fixed (byte* p = blob)
            {
                uint written;
                if (Call<nint, nint, uint, nint>(type, 15, (nint)(&key), (nint)p, size, (nint)(&written)) >= 0)
                {
                    _sequenceHeader = H264.StartsWithStartCode(blob) ? blob[..(int)written] : [];
                }
            }
        }
        finally
        {
            Release(type);
        }
    }

    /// <summary>No frames held back: low-latency mode, no B-frames, a keyframe every 2 s. Optional on old Windows.</summary>
    private void ApplyLowLatency()
    {
        SetCodecValue(LowLatencyMode, Variant.Bool(true));
        SetCodecValue(BPictureCount, Variant.UInt32(0));
        SetCodecValue(GopSize, Variant.UInt32((uint)(_fps * 2)));
    }

    private void SetCodecValue(Guid api, Variant value)
    {
        Call<nint, nint>(_codecApi, 9, (nint)(&api), (nint)(&value));
    }

    public void Dispose()
    {
        Release(_outputSample);
        Release(_outputBuffer);
        Release(_codecApi);
        Release(_transform);
        _outputSample = _outputBuffer = _codecApi = _transform = 0;
        if (_mfStarted)
        {
            _ = MFShutdown(); // nothing to do if it fails while closing
            _mfStarted = false;
        }
        if (_comInitialized)
        {
            CoUninitialize();
            _comInitialized = false;
        }
    }

    // MARK: vtable helpers

    private static nint NewMediaType()
    {
        nint type;
        Check(MFCreateMediaType(&type), "MFCreateMediaType");
        return type;
    }

    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        Check(Call<nint, nint>(attributes, 24, (nint)(&key), (nint)(&value)), "SetGUID");

    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        Check(Call<nint, uint>(attributes, 21, (nint)(&key), value), "SetUINT32");

    private static void SetUInt64(nint attributes, Guid key, ulong value) =>
        Check(Call<nint, ulong>(attributes, 22, (nint)(&key), value), "SetUINT64");

    private static int QueryInterface(nint obj, Guid* iid, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, iid, result);

    private static void Release(nint obj)
    {
        if (obj != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
        }
    }

    private static void* Slot(nint obj, int index) => (*(void***)obj)[index];

    private static int Call(nint obj, int slot) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, slot))(obj);

    private static int Call<T1>(nint obj, int slot, T1 a)
        where T1 : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T1, int>)Slot(obj, slot))(obj, a);

    private static int Call<T1, T2>(nint obj, int slot, T1 a, T2 b)
        where T1 : unmanaged
        where T2 : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T1, T2, int>)Slot(obj, slot))(obj, a, b);

    private static int Call<T1, T2, T3>(nint obj, int slot, T1 a, T2 b, T3 c)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T1, T2, T3, int>)Slot(obj, slot))(obj, a, b, c);

    private static int Call<T1, T2, T3, T4>(nint obj, int slot, T1 a, T2 b, T3 c, T4 d)
        where T1 : unmanaged
        where T2 : unmanaged
        where T3 : unmanaged
        where T4 : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T1, T2, T3, T4, int>)Slot(obj, slot))(obj, a, b, c, d);

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"Media Foundation: {what} (0x{hr:X8}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public nint Sample;
        public uint Status;
        public nint Events;
    }

    /// <summary>VARIANT (24 bytes on x64): VT_UI4 or VT_BOOL only.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public uint UInt;

        [FieldOffset(8)]
        public short BoolValue;

        public static Variant UInt32(uint value) => new() { Type = 19, UInt = value };

        public static Variant Bool(bool value) => new() { Type = 11, BoolValue = (short)(value ? -1 : 0) };
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(nint* type);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateSample(nint* sample);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMemoryBuffer(uint maxLength, nint* buffer);
}

/// <summary>Annex-B helpers (start codes 00 00 01 / 00 00 00 01).</summary>
public static class H264
{
    public static bool StartsWithStartCode(ReadOnlySpan<byte> data) =>
        (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1)
        || (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1);

    /// <summary>NAL unit types present in an Annex-B stream (5 = IDR slice, 7 = SPS, 8 = PPS).</summary>
    public static IEnumerable<int> NalTypes(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                yield return data[i + 3] & 0x1F;
                i += 2;
            }
        }
    }

    public static bool ContainsNal(byte[] data, int type) => NalTypes(data).Contains(type);
}
