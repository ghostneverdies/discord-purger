using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordPurger.Core;

public static partial class TokenScanner
{
    private const string TokenPattern = @"[\w-]{24,32}\.[\w-]{6}\.[\w-]{27,64}";
    private const string MfaPattern = @"mfa\.[\w-]{84}";
    private const string EncryptedPrefix = "dQw4w9WgXcQ:";
    private const int ChunkSize = 1024 * 1024;
    private const int CarryBytes = 128;
    private const long MaxRegionBytes = 256L * 1024 * 1024;

    private static readonly byte[] AuthHeaderPrefix = "Authorization"u8.ToArray();

    private static readonly byte[] V8TokenKeyPrefix = [
        0x6D, 0x00, 0x00, 0x00, 0x05, 0x74, 0x6F, 0x6B,
        0x65, 0x6E, 0x6D, 0x00, 0x00, 0x00, 0x03
    ];

    private static readonly HashSet<uint> ReadableProtects =
        new() { 0x02, 0x04, 0x08, 0x20, 0x40, 0x80 };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualQueryEx(nint process, nint baseAddress, out MEMORY_BASIC_INFORMATION info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer, nuint size, out nuint bytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_IMAGE = 0x1000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint Reserved1;
        public nint PebBaseAddress;
        public nint Reserved2_1;
        public nint Reserved2_2;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    public static StorageScanResult ScanStorage()
    {
        var tokens = new List<string>();
        var filesScanned = 0;

        var roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var dirs = new[]
        {
            Path.Combine(roam, "discord", "Local Storage", "leveldb"),
            Path.Combine(roam, "discordcanary", "Local Storage", "leveldb"),
            Path.Combine(roam, "discordptb", "Local Storage", "leveldb"),
        };

        var stateFiles = new[]
        {
            Path.Combine(roam, "discord", "Local State"),
            Path.Combine(roam, "discordcanary", "Local State"),
            Path.Combine(roam, "discordptb", "Local State"),
        };

        for (var i = 0; i < dirs.Length; i++)
        {
            filesScanned += ScanLevelDbDir(dirs[i], tokens);
            tokens.AddRange(DecryptEncryptedTokens(dirs[i], stateFiles[i]));
        }

        var browserRoots = new[]
        {
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
        };

        foreach (var root in browserRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var profile in Directory.GetDirectories(root))
                {
                    var ldb = Path.Combine(profile, "Local Storage", "leveldb");
                    if (Directory.Exists(ldb))
                        filesScanned += ScanLevelDbDir(ldb, tokens);
                }
            }
            catch { }
        }

        return new StorageScanResult(Deduplicate(tokens), filesScanned);
    }

    public static MemoryScanResult ScanMemory(Action<string>? onPhase = null, CancellationToken ct = default)
    {
        var pids = RunningDiscordPids();
        if (pids.Length == 0)
            return new MemoryScanResult([], 0);

        onPhase?.Invoke("Scanning memory…");
        var tokens = new List<string>();
        var processCount = 0;

        for (var i = 0; i < pids.Length; i++)
        {
            if (ct.IsCancellationRequested) break;
            processCount++;
            onPhase?.Invoke($"Scanning memory… process {i + 1} of {pids.Length}");
            ScanProcessMemory(pids[i], tokens, ct);
        }

        var result = Deduplicate(tokens);
        if (result.Count > 0)
            return new MemoryScanResult(result, processCount);

        onPhase?.Invoke("Scanning memory… full fallback");
        tokens.Clear();
        processCount = 0;

        for (var i = 0; i < pids.Length; i++)
        {
            if (ct.IsCancellationRequested) break;
            processCount++;
            onPhase?.Invoke($"Scanning memory… process {i + 1} of {pids.Length}");
            ScanProcessMemoryFull(pids[i], tokens, ct);
        }

        return new MemoryScanResult(Deduplicate(tokens), processCount);
    }

    public static bool IsDiscordRunning()
        => RunningDiscordPids().Length > 0;

    private static void ScanProcessMemoryFull(int pid, List<string> tokens, CancellationToken ct)
    {
        var hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (hProcess == 0) return;

        try
        {
            nint address = 0;
            MEMORY_BASIC_INFORMATION mbi;

            while (!ct.IsCancellationRequested &&
                   VirtualQueryEx(hProcess, address, out mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
            {
                if (mbi.State == MEM_COMMIT
                    && ReadableProtects.Contains(mbi.Protect)
                    && (mbi.Type & MEM_IMAGE) == 0
                    && mbi.RegionSize > 0
                    && (long)mbi.RegionSize <= MaxRegionBytes)
                {
                    ScanRegion(hProcess, mbi.BaseAddress, (long)mbi.RegionSize, tokens, ct);
                }

                var next = mbi.BaseAddress + (nint)mbi.RegionSize;
                if (next <= address) break;
                address = next;
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static void ScanRegion(nint hProcess, nint baseAddress, long size, List<string> tokens, CancellationToken ct)
    {
        var readBuf = new byte[ChunkSize];
        long pos = 0;
        var carry = ReadOnlySpan<byte>.Empty;

        while (pos < size && !ct.IsCancellationRequested)
        {
            var toRead = (int)Math.Min(size - pos, ChunkSize);
            if (!ReadProcessMemory(hProcess, baseAddress + (nint)pos, readBuf, (nuint)toRead, out var bytesRead) || bytesRead == 0)
                break;

            var chunk = readBuf.AsSpan(0, (int)bytesRead);
            ScanChunk(chunk, carry, tokens);

            if (bytesRead >= CarryBytes)
                carry = readBuf.AsSpan((int)bytesRead - CarryBytes, CarryBytes).ToArray();
            else
                carry = readBuf.AsSpan(0, (int)bytesRead).ToArray();

            pos += (int)bytesRead;
        }
    }

    private static void ScanProcessMemory(int pid, List<string> tokens, CancellationToken ct)
    {
        var hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (hProcess == 0) return;

        try
        {
            nint address = 0;
            MEMORY_BASIC_INFORMATION mbi;

            while (!ct.IsCancellationRequested &&
                   VirtualQueryEx(hProcess, address, out mbi, (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()))
            {
                if (mbi.State == MEM_COMMIT
                    && ReadableProtects.Contains(mbi.Protect)
                    && (mbi.Type & MEM_IMAGE) == 0
                    && mbi.RegionSize > 0
                    && (long)mbi.RegionSize <= MaxRegionBytes)
                {
                    ScanRegionFast(hProcess, mbi.BaseAddress, (long)mbi.RegionSize, tokens, ct);
                }

                var next = mbi.BaseAddress + (nint)mbi.RegionSize;
                if (next <= address) break;
                address = next;
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static void ScanRegionFast(nint hProcess, nint baseAddress, long size, List<string> tokens, CancellationToken ct)
    {
        var readBuf = new byte[ChunkSize];
        long pos = 0;
        var carry = ReadOnlySpan<byte>.Empty;

        while (pos < size && !ct.IsCancellationRequested)
        {
            var toRead = (int)Math.Min(size - pos, ChunkSize);
            if (!ReadProcessMemory(hProcess, baseAddress + (nint)pos, readBuf, (nuint)toRead, out var bytesRead) || bytesRead == 0)
                break;

            var chunk = readBuf.AsSpan(0, (int)bytesRead);
            ScanChunkFast(chunk, carry, tokens);

            if (bytesRead >= CarryBytes)
                carry = readBuf.AsSpan((int)bytesRead - CarryBytes, CarryBytes).ToArray();
            else
                carry = readBuf.AsSpan(0, (int)bytesRead).ToArray();

            pos += (int)bytesRead;
        }
    }

    private static void ScanChunkFast(ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> carry, List<string> tokens)
    {
        if (chunk.IndexOf(AuthHeaderPrefix) < 0 && chunk.IndexOf(V8TokenKeyPrefix) < 0) return;

        var combined = new byte[carry.Length + chunk.Length];
        carry.CopyTo(combined);
        chunk.CopyTo(combined.AsSpan(carry.Length));

        var utf8 = Encoding.UTF8.GetString(combined);
        AddMatches(utf8, tokens);
        AddMfaMatches(utf8, tokens);
    }

    private static void ScanChunk(ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> carry, List<string> tokens)
    {
        Span<byte> combined;
        if (chunk.IndexOf((byte)'.') < 0) return;

        combined = new byte[carry.Length + chunk.Length];
        carry.CopyTo(combined);
        chunk.CopyTo(combined[carry.Length..]);

        var utf8 = Encoding.UTF8.GetString(combined);
        AddMatches(utf8, tokens);
        AddMfaMatches(utf8, tokens);

        var utf16 = Encoding.Unicode.GetString(combined);
        AddMatches(utf16, tokens);
        AddMfaMatches(utf16, tokens);
    }

    private static int ScanLevelDbDir(string dir, List<string> tokens)
    {
        if (!Directory.Exists(dir)) return 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = file;
                if (!name.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    continue;

                count++;
                ScanFile(file, tokens);
            }
        }
        catch { }
        return count;
    }

    private static void ScanFile(string path, List<string> tokens)
    {
        try
        {
            var temp = Path.GetTempFileName();
            File.Copy(path, temp, true);
            try
            {
                var text = File.ReadAllText(temp);
                AddMatches(text, tokens);
                AddMfaMatches(text, tokens);
            }
            finally
            {
                File.Delete(temp);
            }
        }
        catch { }
    }

    private static List<string> DecryptEncryptedTokens(string ldbDir, string stateFile)
    {
        var result = new List<string>();
        if (!Directory.Exists(ldbDir)) return result;

        var masterKey = LoadMasterKey(stateFile);
        if (masterKey == null) return result;

        foreach (var file in Directory.GetFiles(ldbDir))
        {
            if (!file.EndsWith(".ldb", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var temp = Path.GetTempFileName();
                try
                {
                    File.Copy(file, temp, true);
                    var text = File.ReadAllText(temp);
                    var idx = 0;
                    while ((idx = text.IndexOf(EncryptedPrefix, idx, StringComparison.Ordinal)) >= 0)
                    {
                        var from = idx + EncryptedPrefix.Length;
                        var to = from;
                        while (to < text.Length && (char.IsAsciiLetterOrDigit(text[to]) || text[to] is '+' or '/' or '='))
                            to++;
                        var token = to - from > 8 ? DecryptToken(text.Substring(from, to - from), masterKey) : null;
                        if (token != null)
                            result.Add(token);
                        idx = to;
                    }
                }
                finally
                {
                    File.Delete(temp);
                }
            }
            catch { }
        }
        return result;
    }

    private static byte[]? LoadMasterKey(string stateFile)
    {
        if (!File.Exists(stateFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encKey))
                return null;
            var b64 = encKey.GetString();
            if (string.IsNullOrEmpty(b64)) return null;
            var raw = Convert.FromBase64String(b64);
            if (raw.Length <= 5) return null;
            return ProtectedData.Unprotect(raw.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
        }
        catch { return null; }
    }

    private static string? DecryptToken(string b64, byte[] masterKey)
    {
        try
        {
            var blob = Convert.FromBase64String(b64);
            if (blob.Length < 3 + 12 + 16 + 1) return null;
            var nonce = blob.AsSpan(3, 12);
            var payload = blob.AsSpan(15);
            var tag = payload[^16..];
            var cipher = payload[..^16];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(masterKey, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            var token = Encoding.UTF8.GetString(plain).TrimEnd('\0');
            return token.Length is >= 50 and <= 120 ? token : null;
        }
        catch { return null; }
    }

    private static void AddMatches(string text, List<string> tokens)
    {
        foreach (Match m in TokenRegex().Matches(text))
        {
            var val = m.Value;
            if (val.Length is >= 50 and <= 120)
                tokens.Add(val);
        }
    }

    private static void AddMfaMatches(string text, List<string> tokens)
    {
        foreach (Match m in MfaRegex().Matches(text))
            tokens.Add(m.Value);
    }

    private static int[] RunningDiscordPids()
    {
        try
        {
            return Process.GetProcesses()
                .Where(p => p.ProcessName.Equals("discord", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .Distinct()
                .Where(TokenBearingProcess)
                .ToArray();
        }
        catch { return []; }
    }

    private static bool TokenBearingProcess(int pid)
    {
        var cmdLine = ReadCommandLine(pid);
        if (cmdLine == null) return true;
        return cmdLine.Contains("--type=renderer", StringComparison.OrdinalIgnoreCase)
            || !cmdLine.Contains("--type=", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadCommandLine(int pid)
    {
        var hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (hProcess == 0) return null;

        try
        {
            var status = NtQueryInformationProcess(hProcess, 0, out var pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status < 0 || pbi.PebBaseAddress == 0) return null;

            var peb = pbi.PebBaseAddress;
            var paramsOffset = nint.Size == 8 ? 0x20 : 0x10;
            if (!TryReadPointer(hProcess, peb + paramsOffset, out var procParams) || procParams == 0)
                return null;

            var cmdOffset = nint.Size == 8 ? 0x70 : 0x40;
            var uniSize = nint.Size == 8 ? 16 : 8;
            var uniBytes = new byte[uniSize];
            if (!ReadProcessMemory(hProcess, procParams + cmdOffset, uniBytes, (nuint)uniSize, out var uniRead)
                || uniRead != (nuint)uniSize)
                return null;

            var length = BitConverter.ToUInt16(uniBytes, 0);
            if (length == 0) return string.Empty;

            var bufferStart = nint.Size == 8 ? BitConverter.ToInt64(uniBytes, 8) : BitConverter.ToInt32(uniBytes, 4);
            if (bufferStart == 0) return string.Empty;

            var cmdBytes = new byte[length];
            if (!ReadProcessMemory(hProcess, (nint)bufferStart, cmdBytes, (nuint)length, out var cmdRead)
                || cmdRead != (nuint)length)
                return string.Empty;

            return Encoding.Unicode.GetString(cmdBytes);
        }
        catch { return null; }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static bool TryReadPointer(nint hProcess, nint address, out nint value)
    {
        var buf = new byte[nint.Size];
        if (!ReadProcessMemory(hProcess, address, buf, (nuint)nint.Size, out var read) || read != (nuint)nint.Size)
        {
            value = 0;
            return false;
        }
        value = nint.Size == 8 ? (nint)BitConverter.ToInt64(buf, 0) : (nint)BitConverter.ToInt32(buf, 0);
        return true;
    }

    private static List<string> Deduplicate(List<string> tokens)
    {
        return tokens
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(TokenPattern, RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(MfaPattern, RegexOptions.Compiled)]
    private static partial Regex MfaRegex();
}