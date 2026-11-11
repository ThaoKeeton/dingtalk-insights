using System.Text.Json;

namespace Workbench.App;

internal sealed record RunOptions(bool SmokeTest, string UserRoot, string? ReportPath)
{
    public string? RuntimeProbe { get; init; }
    public bool IsIsolated => SmokeTest || RuntimeProbe is not null;
    public static readonly string[] RuntimeProbePhases = ["management", "restart-seed", "restart-verify", "restart-disabled", "update-verify", "rollback-verify", "remove-verify"];
    public static readonly string[] SampleIds = ["com.projectworkbench.summary", "com.projectworkbench.test-cases"];
    private readonly List<object> _startupStages = [];
    public IReadOnlyList<object> StartupStages => _startupStages.ToArray();

    public void TraceStartup(string stage)
    {
        if (ReportPath is null) return;
        _startupStages.Add(new { stage, threadId = Environment.CurrentManagedThreadId, utc = DateTimeOffset.UtcNow });
        WriteReport(new { succeeded = false, stage, userRoot = UserRoot, startupStages = StartupStages });
    }

    public static RunOptions Parse(string[] args)
    {
        var smoke = args.Contains("--smoke-test", StringComparer.Ordinal);
        string? report = null;
        string? probe = null;
        string? probeRoot = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--smoke-report" && i + 1 < args.Length) report = Path.GetFullPath(args[++i]);
            else if (args[i] == "--runtime-probe" && i + 1 < args.Length) probe = args[++i];
            else if (args[i] == "--probe-root" && i + 1 < args.Length) probeRoot = Path.GetFullPath(args[++i]);
        }
        if (probe is not null && (!RuntimeProbePhases.Contains(probe, StringComparer.Ordinal) || smoke))
            throw new ArgumentException("Unknown or conflicting isolated runtime probe mode.");
        if (probe is null && probeRoot is not null) throw new ArgumentException("A probe root requires an isolated runtime probe.");
        if (probe is not null)
        {
            var parent = Path.Combine(Path.GetTempPath(), "ProjectWorkbench-runtime-probe");
            probeRoot ??= Path.Combine(parent, Guid.NewGuid().ToString("N"));
            if (!string.Equals(Path.GetDirectoryName(probeRoot), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(probeRoot), "N", out _))
                throw new ArgumentException("Runtime probes require an isolated GUID directory under the dedicated temporary probe root.");
        }
        var root = probeRoot ?? (smoke
            ? Path.Combine(Path.GetTempPath(), "ProjectWorkbench-smoke", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectWorkbench"));
        return new(smoke, root, smoke || probe is not null ? report ?? Path.Combine(root, "smoke-result.json") : null) { RuntimeProbe = probe };
    }

    public void WriteReport(object result)
    {
        if (ReportPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        File.WriteAllText(ReportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public void WriteFailure(string stage, Exception error, string? nativeMessage = null) => WriteReport(new
    {
        succeeded = false,
        stage,
        userRoot = UserRoot,
        error = error.ToString(),
        nativeMessage,
        errorHResult = $"0x{unchecked((uint)error.HResult):X8}",
        startupStages = StartupStages,
        completedUtc = DateTimeOffset.UtcNow
    });
}
