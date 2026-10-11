using System.Buffers.Binary;
using BenchForge.Core.Model;

namespace BenchForge.Core.Crash;

/// <summary>Reads the stop code and parameters out of a Windows kernel crash dump (C:\Windows\Minidump\*.dmp, MEMORY.DMP).</summary>
public static class MinidumpReader
{
    public const string DefaultFolder = @"C:\Windows\Minidump";

    public static MinidumpInfo Read(string path)
    {
        var info = new MinidumpInfo { Path = path };
        try
        {
            var fi = new FileInfo(path);
            info.SizeBytes = fi.Length;
            info.Created = fi.LastWriteTime;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[0x1000];
            int n = fs.Read(header, 0, header.Length);
            ParseHeader(header.AsSpan(0, n), info);
        }
        catch (Exception ex)
        {
            info.Valid = false;
            info.Error = ex.Message;
        }
        return info;
    }

    public static void ParseHeader(ReadOnlySpan<byte> h, MinidumpInfo info)
    {
        if (h.Length < 0x60) { info.Error = "File too small"; return; }
        var sig = System.Text.Encoding.ASCII.GetString(h[..8]);
        if (sig == "PAGEDU64")
        {
            info.Is64Bit = true;
            info.Processors = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[0x34..]);
            info.BugCheckCode = BinaryPrimitives.ReadUInt32LittleEndian(h[0x38..]);
            for (int i = 0; i < 4; i++) info.Parameters[i] = BinaryPrimitives.ReadUInt64LittleEndian(h[(0x40 + i * 8)..]);
            info.Valid = true;
        }
        else if (sig == "PAGEDUMP")
        {
            info.Processors = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[0x24..]);
            info.BugCheckCode = BinaryPrimitives.ReadUInt32LittleEndian(h[0x28..]);
            for (int i = 0; i < 4; i++) info.Parameters[i] = BinaryPrimitives.ReadUInt32LittleEndian(h[(0x2C + i * 4)..]);
            info.Valid = true;
        }
        else if (sig.StartsWith("MDMP"))
        {
            info.Error = "User-mode (application) dump, not a blue screen dump";
        }
        else
        {
            info.Error = "Not a Windows kernel dump";
        }
    }

    public static List<MinidumpInfo> ListDumps(string? folder = null, DateTime? since = null)
    {
        var list = new List<MinidumpInfo>();
        folder ??= DefaultFolder;
        try
        {
            if (Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder, "*.dmp"))
                {
                    if (since != null && File.GetLastWriteTime(f) < since) continue;
                    list.Add(Read(f));
                }
        }
        catch { }
        return list.OrderByDescending(d => d.Created).ToList();
    }
}
