using CloudSSD.Cache;
using CloudSSD.Core;
using CloudSSD.Diagnostics;
using CloudSSD.Mount;

namespace CloudSSD.Tests;

public class UnitsTests
{
    [Fact]
    public void Mbps_and_bytes_are_not_confused()
    {
        Assert.Equal(8_750_000, Bandwidth.MbpsToBytesPerSecond(70));
        Assert.Equal(70, Bandwidth.BytesPerSecondToMbps(8_750_000), 6);
        Assert.Equal(8.75, Bandwidth.BytesPerSecondToMBps(8_750_000), 6);
        // 1 GiB at 70 Mbps with 85% efficiency ≈ 144 s, never "instant".
        var t = Bandwidth.EstimateTransfer(ByteSize.GiB, 70);
        Assert.InRange(t.TotalSeconds, 140, 150);
    }

    [Theory]
    [InlineData("10G", 10L * 1024 * 1024 * 1024)]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("1.5GiB", 1610612736L)]
    [InlineData("4096", 4096L)]
    [InlineData("64k", 65536L)]
    public void Sizes_parse(string text, long expected) => Assert.Equal(expected, ByteSize.Parse(text));

    [Fact]
    public void Rclone_size_rendering_is_exact()
    {
        Assert.Equal("20Gi", ByteSize.ToRclone(20 * ByteSize.GiB));
        Assert.Equal("4Mi", ByteSize.ToRclone(4 * ByteSize.MiB));
        Assert.Equal("1000B", ByteSize.ToRclone(1000));
        Assert.Equal("14199018521B", ByteSize.ToRclone(14199018521));
        Assert.Equal("0", ByteSize.ToRclone(0));
    }

    [Fact]
    public void Block_math_covers_range()
    {
        Assert.Equal(new long[] { 0 }, BlockMath.BlocksFor(0, 1, ByteSize.MiB));
        Assert.Equal(new long[] { 0, 1 }, BlockMath.BlocksFor(ByteSize.MiB - 1, 2, ByteSize.MiB));
        Assert.Empty(BlockMath.BlocksFor(10, 0, ByteSize.MiB));
    }

    [Fact]
    public void Metric_history_ring_keeps_latest()
    {
        var h = new MetricHistory(3);
        foreach (var v in new double?[] { 1, 2, null, 4 }) h.Add(v);
        Assert.Equal(new double?[] { 2, null, 4 }, h.Snapshot());
        Assert.Equal(4, h.Latest);
        Assert.Equal(4, h.Max());
    }

    [Fact]
    public void Settings_round_trip_atomically()
    {
        var dir = Directory.CreateTempSubdirectory("cloudssd-cli-settings");
        var store = new JsonSettingsStore(Path.Combine(dir.FullName, "s.json"));
        var s = new AppSettings();
        s.Mounts.Add(new MountDefinition { RemoteName = "gdrive", RemotePath = "Games", DriveLetter = "X" });
        s.Cache.BudgetBytes = 30 * ByteSize.GiB;
        store.Save(s);
        store.Save(s);
        var back = store.Load();
        Assert.Equal(30 * ByteSize.GiB, back.Cache.BudgetBytes);
        Assert.Equal("gdrive:Games", back.Mounts[0].RemoteSpec);
        File.WriteAllText(store.Path, "{ broken");
        Assert.NotNull(store.Load());
        Assert.NotEmpty(Directory.GetFiles(dir.FullName, "s.json.corrupt-*"));
    }
}

public class RedactorTests
{
    [Theory]
    [InlineData("token = {\"access_token\":\"ya29.abc\",\"refresh_token\":\"1//0gsecretsecretsecretsecret\"}")]
    [InlineData("client_secret = GOCSPX-abcdefghijkl")]
    [InlineData("Authorization: Bearer ya29.a0AfH6SMBx")]
    [InlineData("RCLONE_RC_PASS=hunter2")]
    [InlineData("rclone mount --rc-pass hunter2 X:")]
    [InlineData("https://oauth2.googleapis.com/token?client_secret=xyz123&code=4/abc")]
    public void Secrets_are_removed(string input)
    {
        var r = Redactor.Redact(input);
        foreach (var secret in new[] { "ya29.abc", "1//0gsecret", "GOCSPX-abcdefghijkl", "ya29.a0AfH6SMBx", "hunter2", "xyz123", "4/abc" })
            Assert.DoesNotContain(secret, r);
        Assert.Contains("REDACTED", r);
    }

    [Fact]
    public void Ordinary_text_is_untouched()
    {
        const string s = "Mount gdrive: X: --vfs-cache-mode full --buffer-size 0";
        Assert.Equal(s, Redactor.Redact(s));
    }

    [Fact]
    public void Dpapi_secrets_round_trip_and_fail_closed()
    {
        var p = SecretStore.Protect("s3cr3t");
        Assert.DoesNotContain("s3cr3t", p);
        Assert.Equal("s3cr3t", SecretStore.Unprotect(p));
        Assert.Null(SecretStore.Unprotect("bm90LWEtYmxvYg=="));
        Assert.Null(SecretStore.Unprotect("%%%"));
    }
}

public class RcloneLogParserTests
{
    // Lines captured from rclone v1.75.2 (-vv, --log-format date,time,microseconds) during the probe session.
    private static readonly string[] ColdThenWarm =
    [
        "2026/10/10 21:05:08.486320 DEBUG : /big.bin: Read: ofst=0, fh=0x0",
        "2026/10/10 21:05:08.486320 DEBUG : big.bin(0x3d31c648cc40): _readAt: size=65536, off=0",
        "2026/10/10 21:05:08.486851 DEBUG : big.bin: vfs cache: truncate to size=67108864",
        "2026/10/10 21:05:08.487403 DEBUG : vfs cache: looking for range={Pos:0 Size:65536} in [] - present false",
        "2026/10/10 21:05:08.565839 DEBUG : big.bin(0x3d31c648cc40): >_readAt: n=65536, err=<nil>",
        "2026/10/10 21:05:08.565839 DEBUG : /big.bin: >Read: n=65536",
        "2026/10/10 21:05:09.000100 DEBUG : Game Data/level 1 (final).pak(0x2532e4015380): _readAt: size=4096, off=9437184",
        "2026/10/10 21:05:09.000150 DEBUG : vfs cache: looking for range={Pos:9437184 Size:4096} in [{Pos:9437184 Size:131072}] - present true",
        "2026/10/10 21:05:09.000300 DEBUG : Game Data/level 1 (final).pak(0x2532e4015380): >_readAt: n=4096, err=<nil>",
    ];

    [Fact]
    public void Reconstructs_reads_with_hit_state_and_wait_time()
    {
        var p = new RcloneLogParser("m1");
        var events = new List<AccessEvent>();
        var lookups = new List<(string, long, bool)>();
        p.Read += events.Add;
        p.Lookup += (path, off, present, _) => lookups.Add((path, off, present));
        foreach (var l in ColdThenWarm) p.ParseLine(l);

        Assert.Equal(2, events.Count);
        var cold = events[0];
        Assert.Equal("big.bin", cold.Path);
        Assert.Equal(0, cold.Offset);
        Assert.Equal(65536, cold.Length);
        Assert.False(cold.CacheHit);
        Assert.Equal(79.519, cold.WaitMs!.Value, 2);
        var warm = events[1];
        Assert.Equal("Game Data/level 1 (final).pak", warm.Path);
        Assert.Equal(9437184, warm.Offset);
        Assert.True(warm.CacheHit);
        Assert.Equal(0.2, warm.WaitMs!.Value, 3);
        Assert.Equal(ReadSource.Application, warm.Source);
        Assert.Equal([("big.bin", 0L, false), ("Game Data/level 1 (final).pak", 9437184L, true)], lookups);
    }

    [Fact]
    public void Errors_eof_and_attribution_are_handled()
    {
        var registry = new ReadAttributionRegistry();
        var clock = new ManualClock(new DateTime(2026, 10, 10, 21, 0, 0));
        var p = new RcloneLogParser("m", registry.IsPrefetch);
        var events = new List<AccessEvent>();
        var provider = new List<ProviderEvent>();
        p.Read += events.Add;
        p.Provider += provider.Add;
        using (registry.Register("a.pak", 1048576, 1048576, clock)) { }
        p.ParseLine("2026/10/10 21:00:00.050000 DEBUG : a.pak(0x1): _readAt: size=1048576, off=1048576");
        p.ParseLine("2026/10/10 21:00:00.090000 DEBUG : a.pak(0x1): >_readAt: n=1048576, err=<nil>");
        p.ParseLine("2026/10/10 21:00:05.000000 DEBUG : a.pak(0x2): _readAt: size=4096, off=1048576");
        p.ParseLine("2026/10/10 21:00:05.100000 DEBUG : a.pak(0x2): >_readAt: n=0, err=vfs reader: failed to write to cache file: Open failed: Get \"http://x/a.pak\": EOF");
        p.ParseLine("2026/10/10 21:00:06.000000 DEBUG : a.pak(0x3): _readAt: size=4096, off=99999999");
        p.ParseLine("2026/10/10 21:00:06.000100 DEBUG : a.pak(0x3): >_readAt: n=0, err=EOF");
        p.ParseLine("2026/10/10 21:00:07.000000 ERROR : a.pak: vfs cache: failed to download: googleapi: Error 403: Rate Limit Exceeded, rateLimitExceeded");
        p.ParseLine("2026/10/10 21:00:08.000000 ERROR : gdrive: Failed to get token: oauth2: \"invalid_grant\" \"Token has been expired or revoked.\"");
        p.ParseLine("2026/10/10 21:00:09.000000 INFO  : vfs cache: cleaned: objects 3 (was 5) in use 1, to upload 0, uploading 0, total size 1.2Gi (was 2Gi)");

        Assert.Equal(ReadSource.Prefetch, events[0].Source);
        Assert.Equal(ReadSource.Application, events[1].Source); // started after the prefetch window closed
        Assert.NotNull(events[1].Error);
        Assert.Equal(0, events[2].Length);
        Assert.Contains(provider, e => e.Kind == ProviderEventKind.RateLimited);
        Assert.Contains(provider, e => e.Kind == ProviderEventKind.AuthError);
        Assert.Contains(provider, e => e.Kind == ProviderEventKind.CacheCleaned);
    }

    [Fact]
    public void Garbage_lines_are_ignored()
    {
        var p = new RcloneLogParser("m");
        var n = 0;
        p.Read += _ => n++;
        foreach (var l in new[] { "", "panic: something", "goroutine 1 [running]:", "2026/10/10 bad line", "\t/usr/local/go/src/runtime/x.go:1" })
            p.ParseLine(l);
        Assert.Equal(0, n);
    }
}

public class GoogleClientFileTests
{
    [Fact]
    public void Reads_desktop_client_json_and_rejects_other_files()
    {
        var dir = Directory.CreateTempSubdirectory("cloudssd-cli-key").FullName;
        var good = Path.Combine(dir, "client_secret_123-abc.apps.googleusercontent.com.json");
        File.WriteAllText(good, """{"installed":{"client_id":"123-abc.apps.googleusercontent.com","project_id":"cloudssd","client_secret":"GOCSPX-test","redirect_uris":["http://localhost"]}}""");
        var bad = Path.Combine(dir, "client_secret_bad.json");
        File.WriteAllText(bad, """{"installed":{"client_id":"not-google"}}""");
        var k = GoogleClientFile.TryLoad(good)!;
        Assert.Equal("123-abc.apps.googleusercontent.com", k.ClientId);
        Assert.Equal("GOCSPX-test", k.ClientSecret);
        Assert.Equal("https://console.cloud.google.com/auth/audience?project=cloudssd", k.TestUsersUrl);
        Assert.Null(GoogleClientFile.TryLoad(bad));
        Assert.Null(GoogleClientFile.TryLoad(Path.Combine(dir, "missing.json")));
    }
}

public class GoogleSetupErrorTests
{
    [Fact]
    public void Drive_api_disabled_error_becomes_a_one_click_fix()
    {
        const string err = "rclone rc operations/about failed (500): couldn't find root directory ID: googleapi: Error 403: Google Drive API has not been used in project 123456789012 before or it is disabled. ... \"reason\": \"SERVICE_DISABLED\" ..., accessNotConfigured";
        var x = GoogleSetupException.From(err)!;
        Assert.Equal("https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=123456789012", x.FixUrl);
        Assert.Contains("Enable", x.FixLabel);
        Assert.Null(GoogleSetupException.From("some other error"));
    }
}

public class MountPlanningTests
{
    private static RcloneInfo FakeRclone(params string[] missing)
    {
        var flags = new[]
        {
            "vfs-cache-mode", "cache-dir", "vfs-cache-max-size", "vfs-cache-min-free-space", "vfs-cache-max-age", "vfs-cache-poll-interval",
            "buffer-size", "vfs-read-ahead", "vfs-read-chunk-size", "vfs-read-chunk-streams", "vfs-write-back", "dir-cache-time", "file-perms",
            "dir-perms", "volname", "read-only", "bwlimit", "tpslimit", "rc", "rc-addr", "log-file", "log-format", "log-file-max-size",
            "log-file-max-backups", "http-url", "option", "transfers"
        }.Except(missing).ToHashSet();
        return new RcloneInfo("rclone.exe", "rclone v1.75.2", new Version(1, 75, 2), flags);
    }

    private static MountPlanInput Input(MountDefinition? d = null) => new(
        d ?? new MountDefinition { Id = "a", RemoteName = "gdrive", RemotePath = "Games", DisplayName = "My Drive" },
        'X', new CacheSettings(), new NetworkSettings(), @"C:\cache\m-a", @"C:\logs\r.log", 20 * ByteSize.GiB, true, 5572, "drive");

    [Fact]
    public void Builds_full_cache_mount_with_safe_defaults()
    {
        var plan = MountFlagBuilder.Build(Input(), FakeRclone());
        var a = string.Join(' ', plan.Arguments);
        Assert.StartsWith("mount gdrive:Games X:", a);
        Assert.Contains("--vfs-cache-mode full", a);
        Assert.Contains("--vfs-cache-max-size 20Gi", a);
        Assert.Contains("--buffer-size 0", a);
        Assert.Contains("--file-perms 0777", a);
        Assert.Contains("--transfers 8", a);
        if (OperatingSystem.IsWindows()) Assert.Contains("--option ThreadCount=64", a);
        Assert.Contains("--rc-addr 127.0.0.1:5572", a);
        Assert.Contains("--tpslimit 10", a);
        Assert.Contains("-vv", a);
        Assert.DoesNotContain("rc-pass", a);
        Assert.DoesNotContain("rc-user", a);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Unsupported_flags_become_warnings_and_dangerous_extras_are_blocked()
    {
        var d = new MountDefinition { Id = "a", RemoteName = "gdrive" };
        d.ExtraFlags["password-command"] = "calc.exe";
        d.ExtraFlags["rc-no-auth"] = "";
        d.ExtraFlags["rc-addr"] = "0.0.0.0:5572";
        d.ExtraFlags["http-url"] = "http://127.0.0.1:1/";
        var plan = MountFlagBuilder.Build(Input(d), FakeRclone("vfs-cache-min-free-space"));
        var a = string.Join(' ', plan.Arguments);
        Assert.DoesNotContain("calc.exe", a);
        Assert.DoesNotContain("0.0.0.0", a);
        Assert.DoesNotContain("rc-no-auth", a);
        Assert.Contains("--http-url http://127.0.0.1:1/", a);
        Assert.Contains(plan.Warnings, w => w.Contains("vfs-cache-min-free-space"));
        Assert.Equal(3, plan.Warnings.Count(w => w.Contains("not allowed")));
    }

    [Fact]
    public void Drive_letter_choice_respects_usage()
    {
        var used = new HashSet<char> { 'C', 'D', 'E', 'F', 'G', 'H', 'I' };
        Assert.Equal('X', DriveLetterAllocator.Choose(null, used));
        Assert.Equal('M', DriveLetterAllocator.Choose("m", used));
        Assert.Equal('X', DriveLetterAllocator.Choose("G", used));
        Assert.Equal('G', DriveLetterAllocator.Choose(null, new HashSet<char> { 'C' }));
        Assert.Null(DriveLetterAllocator.Choose(null, "CDEFGHIJKLMNOPQRSTUVWXYZ".ToHashSet()));
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(3600, "1h")]
    [InlineData(720 * 3600, "720h")]
    [InlineData(90, "90s")]
    public void Durations_render_for_rclone(int seconds, string expected) => Assert.Equal(expected, MountFlagBuilder.Dur(TimeSpan.FromSeconds(seconds)));
}

public class CacheTests
{
    [Fact]
    public void Inspector_reads_rclone_metadata_and_sparse_sizes()
    {
        var dir = Directory.CreateTempSubdirectory("cloudssd-cli-cache").FullName;
        var fs = "：http{abc}";
        Directory.CreateDirectory(Path.Combine(dir, "vfsMeta", fs, "Game Data"));
        Directory.CreateDirectory(Path.Combine(dir, "vfs", fs, "Game Data"));
        File.WriteAllText(Path.Combine(dir, "vfsMeta", fs, "Game Data", "a.pak"), """
            {"ModTime":"2026-10-10T21:07:33+05:30","ATime":"2026-10-10T21:07:33+05:30","Size":67108864,
             "Rs":[{"Pos":0,"Size":98304},{"Pos":10485760,"Size":98304}],"Fingerprint":"67108864,x","Dirty":false}
            """);
        File.WriteAllText(Path.Combine(dir, "vfsMeta", fs, "save.dat"), """{"Size":10,"Rs":null,"Dirty":true}""");
        File.WriteAllBytes(Path.Combine(dir, "vfs", fs, "Game Data", "a.pak"), new byte[4096]);

        var files = CacheInspector.Enumerate(dir).ToList();
        Assert.Equal(2, files.Count);
        var a = files.Single(f => f.RemotePath == "Game Data/a.pak");
        Assert.Equal(67108864, a.LogicalSize);
        Assert.Equal(196608, a.CachedBytes);
        Assert.True(a.Covers(10485760 + 4096, 4096));
        Assert.False(a.Covers(10485760 + 90000, 20000));
        Assert.True(a.PhysicalBytes >= 4096);
        Assert.NotNull(CacheInspector.Find(dir, "Game Data/a.pak"));
        var usage = CacheInspector.Measure(dir);
        Assert.Equal(1, usage.DirtyFiles);
        Assert.Equal("Games/a.pak", CacheInspector.StripRoot("Games/a.pak", "Other"));
        Assert.Equal("a.pak", CacheInspector.StripRoot("Games/a.pak", "Games"));
    }

    [Fact]
    public void Budget_is_reduced_to_protect_free_space()
    {
        // 4 GiB free (like this machine's C:), 8 GiB reserve, user asked for 20 GiB.
        var d = DiskSafetyGuard.Decide(20 * ByteSize.GiB, 8 * ByteSize.GiB, 4 * ByteSize.GiB, 0);
        Assert.True(d.EffectiveMaxBytes <= 0 || d.EffectiveMaxBytes < ByteSize.GiB);
        Assert.Equal(DiskPressure.Low, d.Pressure);
        Assert.NotEmpty(d.Warnings);

        var ok = DiskSafetyGuard.Decide(20 * ByteSize.GiB, 8 * ByteSize.GiB, 200 * ByteSize.GiB, 5 * ByteSize.GiB);
        Assert.Equal(20 * ByteSize.GiB, ok.EffectiveMaxBytes);
        Assert.False(ok.BudgetReduced);
        Assert.True(DiskSafetyGuard.MayPrefetch(ByteSize.GiB, 200 * ByteSize.GiB, 8 * ByteSize.GiB));
        Assert.False(DiskSafetyGuard.MayPrefetch(ByteSize.GiB, 8 * ByteSize.GiB, 8 * ByteSize.GiB));
    }

    [Fact]
    public void Cache_clear_refuses_unmarked_or_foreign_folders_and_dirty_data()
    {
        var root = Directory.CreateTempSubdirectory("cloudssd-cli-root").FullName;
        var outside = Directory.CreateTempSubdirectory("cloudssd-cli-outside").FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "user data");
        Assert.False(CacheMaintenance.ClearMountCache(outside, root).Cleared);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));

        var unmarked = Path.Combine(root, "m-x");
        Directory.CreateDirectory(Path.Combine(unmarked, "vfs"));
        Assert.False(CacheMaintenance.ClearMountCache(unmarked, root).Cleared);

        var marked = Path.Combine(root, "m-y");
        RcloneMountService.PrepareCacheDir(marked);
        Directory.CreateDirectory(Path.Combine(marked, "vfsMeta", "fs"));
        File.WriteAllText(Path.Combine(marked, "vfsMeta", "fs", "save.dat"), """{"Size":1,"Dirty":true}""");
        var refused = CacheMaintenance.ClearMountCache(marked, root);
        Assert.False(refused.Cleared);
        Assert.Equal(1, refused.DirtyFilesBlocking);
        var forced = CacheMaintenance.ClearMountCache(marked, root, discardPendingUploads: true);
        Assert.True(forced.Cleared);
        Assert.False(Directory.Exists(Path.Combine(marked, "vfsMeta")));
        Assert.True(File.Exists(Path.Combine(marked, CacheMaintenance.Marker)));
    }
}
