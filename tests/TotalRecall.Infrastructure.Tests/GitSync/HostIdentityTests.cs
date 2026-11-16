using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class HostIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-host-" + Guid.NewGuid().ToString("N"));

    public HostIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static MachineIdResult Found(string id) => new(MachineIdKind.Found, id);

    [Fact]
    public void Clean_TrimsAndRefusesEmptyLongAndControlValues()
    {
        Assert.Equal("abc", MachineIdReader.Clean("  abc\n"));
        Assert.Null(MachineIdReader.Clean(""));
        Assert.Null(MachineIdReader.Clean("   "));
        Assert.Null(MachineIdReader.Clean(null));
        Assert.Null(MachineIdReader.Clean(new string('a', 129)));
        Assert.Equal(new string('a', 128), MachineIdReader.Clean(new string('a', 128)));
        Assert.Null(MachineIdReader.Clean("ab\u0001c"));
    }

    [Fact]
    public void ParseIoreg_ReadsThePlatformUuid()
    {
        const string output = """
            +-o Root  <class IORegistryEntry>
              +-o MacBookPro  <class IOPlatformExpertDevice>
                {
                  "IOPlatformSerialNumber" = "C02XXXXXXXXX"
                  "IOPlatformUUID" = "AAAA1111-BBBB-2222-CCCC-333344445555"
                  "manufacturer" = <"Apple Inc.">
                }
            """;
        Assert.Equal("AAAA1111-BBBB-2222-CCCC-333344445555", MachineIdReader.ParseIoreg(output));
        Assert.Null(MachineIdReader.ParseIoreg("nothing here"));
        Assert.Null(MachineIdReader.ParseIoreg("\"IOPlatformUUID\" = \"\""));
    }

    [Fact]
    public void FromFileText_FoundAbsentAndFailed()
    {
        Assert.Equal(new MachineIdResult(MachineIdKind.Found, "m1"), MachineIdReader.FromFileText("m1\n", true, false));
        Assert.Equal(MachineIdKind.Absent, MachineIdReader.FromFileText(null, false, false).Kind);
        Assert.Equal(MachineIdKind.Absent, MachineIdReader.FromFileText("  \n", true, false).Kind);
        Assert.Equal(MachineIdKind.Failed, MachineIdReader.FromFileText(null, true, true).Kind);
    }

    [Fact]
    public void LinuxCombination_FirstFoundWins_AbsentFallsThrough_FailedFailsTheRead()
    {
        (bool, string?, bool) Missing() => (false, null, false);

        var firstWins = MachineIdReader.Read(path => path == "/etc/machine-id" ? (true, "one", false) : (true, "two", false));
        Assert.Equal(new MachineIdResult(MachineIdKind.Found, "one"), firstWins);

        var fallsThrough = MachineIdReader.Read(path => path == "/etc/machine-id" ? Missing() : (true, "two", false));
        Assert.Equal(new MachineIdResult(MachineIdKind.Found, "two"), fallsThrough);

        var blankFallsThrough = MachineIdReader.Read(path => path == "/etc/machine-id" ? (true, " ", false) : (true, "two", false));
        Assert.Equal("two", blankFallsThrough.Id);

        var failed = MachineIdReader.Read(path => path == "/etc/machine-id" ? (true, null, true) : (true, "two", false));
        Assert.Equal(MachineIdKind.Failed, failed.Kind);

        var none = MachineIdReader.Read(_ => Missing());
        Assert.Equal(MachineIdKind.Absent, none.Kind);
    }

    [Fact]
    public void AFoundId_GivesTheCoreFingerprintOfIdAndCanonicalPath()
    {
        var db = Path.Combine(_root, "tr.db");

        var host = new HostIdentity(() => Found("m"), db, caseInsensitive: false);

        Assert.Equal(SyncIdentity.hostFingerprint("m", host.CanonicalPath!), host.Fingerprint);
    }

    [Fact]
    public void AnAbsentId_UsesTheEmptyMachineId()
    {
        var db = Path.Combine(_root, "tr.db");

        var host = new HostIdentity(() => new MachineIdResult(MachineIdKind.Absent, null), db, caseInsensitive: false);

        Assert.Equal(SyncIdentity.hostFingerprint("", host.CanonicalPath!), host.Fingerprint);
    }

    [Fact]
    public void AFailedSource_GivesNoFingerprint()
    {
        var host = new HostIdentity(() => new MachineIdResult(MachineIdKind.Failed, null), Path.Combine(_root, "tr.db"), false);

        Assert.Null(host.Fingerprint);
        Assert.NotNull(host.CanonicalPath);
    }

    [Fact]
    public void TheFingerprintIsComputedOnce()
    {
        var reads = 0;
        var host = new HostIdentity(() => { reads++; return Found("m"); }, Path.Combine(_root, "tr.db"), false);

        _ = host.Fingerprint;
        _ = host.Fingerprint;
        _ = host.CanonicalPath;

        Assert.Equal(1, reads);
    }

    [Fact]
    public void ATrailingSeparatorIsRemoved_AndCaseFoldingFollowsTheFlag()
    {
        var dir = Path.Combine(_root, "MixedCase");
        Directory.CreateDirectory(dir);

        var kept = HostIdentity.CanonicalPathOf(dir + Path.DirectorySeparatorChar, caseInsensitive: false);
        var folded = HostIdentity.CanonicalPathOf(dir + Path.DirectorySeparatorChar, caseInsensitive: true);

        Assert.EndsWith("MixedCase", kept);
        Assert.EndsWith("mixedcase", folded);
        Assert.Equal(kept!.ToLowerInvariant(), folded);
    }

    [Fact]
    public void AnExtendedLengthPrefixIsStripped()
    {
        Assert.Equal(@"C:\a\b", HostIdentity.StripExtendedPrefix(@"\\?\C:\a\b"));
        Assert.Equal(@"\\server\share\x", HostIdentity.StripExtendedPrefix(@"\\?\UNC\server\share\x"));
        Assert.Equal(@"C:\a", HostIdentity.StripExtendedPrefix(@"C:\a"));
    }

    [ShFact]
    public void ADatabaseReachedThroughASymlinkedDirectory_GivesTheSameFingerprint()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, real);

        var viaReal = new HostIdentity(() => Found("m"), Path.Combine(real, "tr.db"), false);
        var viaLink = new HostIdentity(() => Found("m"), Path.Combine(link, "tr.db"), false);

        Assert.Equal(viaReal.CanonicalPath, viaLink.CanonicalPath);
        Assert.Equal(viaReal.Fingerprint, viaLink.Fingerprint);
    }

    [ShFact]
    public void ALinkInTheMiddleOfAnExistingPath_AndALinkToALink_AreResolved()
    {
        var real = Path.Combine(_root, "real", "deep");
        Directory.CreateDirectory(real);
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateSymbolicLink(first, Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(second, first);

        var canonical = HostIdentity.CanonicalPathOf(Path.Combine(second, "deep", "tr.db"), false);

        Assert.Equal(HostIdentity.CanonicalPathOf(Path.Combine(real, "tr.db"), false), canonical);
    }

    [ShFact]
    public void ASymlinkLoop_MakesTheFingerprintUnavailable()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        var host = new HostIdentity(() => Found("m"), Path.Combine(a, "tr.db"), false);

        Assert.Null(host.CanonicalPath);
        Assert.Null(host.Fingerprint);
    }
}
