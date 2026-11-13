using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CadMcp.Core;

public sealed record ComputerUseTarget(string ExecutablePath, string DisplayName, string PublisherName, string ProductName, string BinaryName, string Sha256)
{
    public string Identity => PublisherName + "\n" + ProductName + "\n" + BinaryName;
    public static ComputerUseTarget Inspect(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new CadFault("COMPUTER_USE_WINDOWS_REQUIRED", "AutoCAD application access requires Windows");
        path = Path.GetFullPath(path);
        if (!Path.GetFileName(path).Equals("acad.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new CadFault("COMPUTER_USE_TARGET_INVALID", "Connect to the AutoCAD whose interface you want to allow");
        string before = IsolatedCSharpJob.HashFile(path);
        if (!WindowsSignature.Trusted(path)) throw new CadFault("COMPUTER_USE_SIGNATURE_UNVERIFIED", "Windows could not verify the AutoCAD executable signature; access was not granted");
        using var certificate = X509Certificate.CreateFromSignedFile(path);
        var version = FileVersionInfo.GetVersionInfo(path);
        string publisher = certificate.Subject, product = version.ProductName ?? "";
        if (!publisher.Contains("Autodesk", StringComparison.OrdinalIgnoreCase) || !product.Contains("AutoCAD", StringComparison.OrdinalIgnoreCase) ||
            before != IsolatedCSharpJob.HashFile(path))
            throw new CadFault("COMPUTER_USE_TARGET_INVALID", "Expected an unchanged, signed Autodesk AutoCAD executable");
        return new(path, version.FileDescription ?? product, publisher, product, Path.GetFileName(path), before);
    }
}

public sealed record ComputerUsePermission(ComputerUseTarget Target, bool Allowed, string Revision);
/// <summary>User-owned access, never restored from a transcript or granted by an agent tool.</summary>
public sealed class ComputerUseAccessStore(string? path = null)
{
    public static string DefaultPath => Environment.GetEnvironmentVariable("CAD_MCP_COMPUTER_USE_ACCESS_FILE") ?? Wire.DataDirectory("computer-use-access.json");
    private readonly string path = path ?? DefaultPath;
    public IReadOnlyList<ComputerUsePermission> Read()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return [];
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return JsonSerializer.Deserialize<ComputerUsePermission[]>(reader.ReadToEnd(), Wire.Json)?.Where(p => Valid(p.Target) && !string.IsNullOrEmpty(p.Revision)).Take(16).ToArray() ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException) { return []; }
    }
    public ComputerUsePermission? Permission(ComputerUseTarget target) => Read().FirstOrDefault(p =>
        p.Target.ExecutablePath.Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase) && p.Target.Identity == target.Identity && p.Target.Sha256 == target.Sha256);
    public void Set(ComputerUseTarget target, bool allowed)
    {
        if (!Valid(target)) throw new InvalidDataException("Incomplete Computer Use application identity");
        var values = Read().Where(p => !p.Target.ExecutablePath.Equals(target.ExecutablePath, StringComparison.OrdinalIgnoreCase)).TakeLast(15)
            .Append(new ComputerUsePermission(target, allowed, Guid.NewGuid().ToString("N"))).ToArray();
        Save(values);
    }
    public void RevokeAll() => Save(Read().Select(p => p with { Allowed = false, Revision = Guid.NewGuid().ToString("N") }).ToArray());
    private void Save(ComputerUsePermission[] values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(values, Wire.Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static bool Valid(ComputerUseTarget? target) => target is { ExecutablePath.Length: > 0, Sha256.Length: 64, BinaryName: not null } && Path.IsPathFullyQualified(target.ExecutablePath) &&
        target.BinaryName.Equals("acad.exe", StringComparison.OrdinalIgnoreCase) && target.Sha256.All(Portable.AsciiHexDigit) &&
        new[] { target.PublisherName, target.ProductName, target.BinaryName }.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 2048 && !s.Any(c => char.IsControl(c) || c is '*' or '?'));
}

internal static class WindowsSignature
{
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoData { public uint Size; public IntPtr Path, File, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint Flags, Context; public IntPtr Signature;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    internal static bool Trusted(string path)
    {
        IntPtr name = Marshal.StringToCoTaskMemUni(path), file = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = file, StateAction = 1, Flags = 0x1000 | 0x10 };
        try
        {
            Marshal.StructureToPtr(new FileInfoData { Size = (uint)Marshal.SizeOf<FileInfoData>(), Path = name }, file, false);
            // No UI and no network revocation fetch. Windows still validates the file signature and trust chain.
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(file); Marshal.FreeCoTaskMem(name);
        }
    }
}
