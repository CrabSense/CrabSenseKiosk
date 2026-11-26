using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace CrabSenseKiosk.Services.WaterBench;

internal static class WireProtocol
{
    internal const int MaxLine = 16384;
    internal const int MaxFrame = 55 * 1024;
    internal static bool HardwareId(string? id) => id is not null && Regex.IsMatch(id, @"\A[0-9a-f]{12}\z", RegexOptions.CultureInvariant);
    internal static bool BatchId(string? id) => id is not null && Regex.IsMatch(id, @"\A[0-9a-f]{16}\z", RegexOptions.CultureInvariant);
    internal static bool Inventory(JsonElement e, bool master, string expected)
    {
        var role = master ? "master" : "slave";
        var board = master ? "EITEK-WERS3-8IO-4AI" : "ESP32-S3-N16R8";
        if (e.GetProperty("schemaVersion").GetInt32() != 1 || e.GetProperty("revision").GetInt32() != 2 ||
            e.GetProperty("role").GetString() != role || e.GetProperty("board").GetString() != board ||
            e.GetProperty("hardwareId").GetString() != expected || !HardwareId(expected)) return false;
        var ports = e.GetProperty("ports").EnumerateArray().ToArray();
        if (ports.Length != (master ? 21 : 11) ||
            ports.Select(p => p.GetProperty("portId").GetString()).Distinct().Count() != ports.Length) return false;
        var reserve = master ? "reserve-vial-drain" : "reserve-dosing";
        return ports.All(p => p.GetProperty("discovery").GetString() == "declared" && !p.GetProperty("remoteControl").GetBoolean())
            && ports.Any(p => p.GetProperty("portId").GetString() == reserve && !p.GetProperty("assigned").GetBoolean());
    }
    internal static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("JSON object required.");
        return doc.RootElement.Clone();
    }
    internal static uint UInt(string s) => uint.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }
    internal static void ValidateJpeg(byte[] bytes, int width, int height)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8 ||
            bytes[^2] != 0xff || bytes[^1] != 0xd9) throw new FormatException("JPEG envelope.");
        var info = Image.Identify(bytes);
        if (info.Width != width || info.Height != height || (long)width * height > 1_000_000)
            throw new FormatException("JPEG dimensions.");
        if (Image.DetectFormat(bytes) is not JpegFormat) throw new FormatException("Not JPEG.");
        using var image = Image.Load(bytes);
        if (image.Width != width || image.Height != height) throw new FormatException("JPEG decode dimensions.");
    }
}
internal sealed record ReceivedFrame(uint CaptureId,uint Sequence,int Width,int Height,uint Crc,byte[] Bytes,DateTimeOffset ReceivedAt);
internal sealed class CameraReceiver
{
    private uint _id, _sequence, _crc;
    private int _width, _height, _size;
    private MemoryStream? _buffer;
    private long _started;
    internal void Reset() { _buffer?.Dispose(); _buffer = null; }
    internal ReceivedFrame? Feed(string line, DateTimeOffset utc, long nowMs)
    {
        if (_buffer is not null && nowMs - _started > 30000) Reset();
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3 || p[0] != "CAM") return null;
        try
        {
            uint id = WireProtocol.UInt(p[2]);
            if (id == 0 || id > 65536) throw new FormatException("Capture ID.");
            switch (p[1])
            {
                case "ERROR": Reset(); return null;
                case "BEGIN":
                    if (p.Length != 8 || _buffer is not null) throw new FormatException("Unexpected header.");
                    _id = id; _sequence = WireProtocol.UInt(p[3]);
                    _width = checked((int)WireProtocol.UInt(p[4])); _height = checked((int)WireProtocol.UInt(p[5]));
                    _size = checked((int)WireProtocol.UInt(p[6]));
                    if (_width <= 0 || _height <= 0 || (long)_width * _height > 1_000_000 ||
                        _size < 4 || _size > WireProtocol.MaxFrame || p[7].Length != 8) throw new FormatException("Frame limits.");
                    _crc = uint.Parse(p[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    _buffer = new MemoryStream(_size); _started = nowMs; return null;
                case "DATA":
                    if (_buffer is null || id != _id || p.Length != 5 || WireProtocol.UInt(p[3]) != _buffer.Length ||
                        p[4].Length is < 2 or > 192 || p[4].Length % 2 != 0) throw new FormatException("Chunk offset/size.");
                    var chunk = Convert.FromHexString(p[4]);
                    if (_buffer.Length + chunk.Length > _size) throw new FormatException("Frame overflow.");
                    _buffer.Write(chunk); return null;
                case "END":
                    if (_buffer is null || id != _id || p.Length != 3 || _buffer.Length != _size) throw new FormatException("Incomplete frame.");
                    var bytes = _buffer.ToArray();
                    if (WireProtocol.Crc32(bytes) != _crc) throw new FormatException("CRC mismatch.");
                    WireProtocol.ValidateJpeg(bytes, _width, _height);
                    var result = new ReceivedFrame(id,_sequence,_width,_height,_crc,bytes,utc);
                    Reset(); return result;
                default: return null;
            }
        }
        catch { Reset(); throw; }
    }
}
