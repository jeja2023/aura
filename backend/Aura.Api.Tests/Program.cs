using System.IO.Compression;
using System.Text;
using Aura.Api.Clustering;
using Aura.Api.Export;
using Aura.Api.Services.Hikvision;
using Microsoft.Extensions.Logging.Abstractions;

var failures = new List<string>();

Run("Feature DBSCAN clusters similar captures together", failures, TestFeatureClustering);
Run("Temporal fallback keeps per-device buckets", failures, TestTemporalFallback);
Run("Standard XLSX writer produces an OpenXML workbook", failures, TestXlsxWriter);
Run("AlertStream multipart extracts one XML part", failures, TestAlertStreamMultipartOneXml);

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAILED");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($" - {failure}");
    }

    return 1;
}

Console.WriteLine("PASSED");
return 0;

static void Run(string name, List<string> failures, Action test)
{
    try
    {
        test();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

static void TestFeatureClustering()
{
    var service = new FeatureClusteringService();
    var captures = new[]
    {
        new CaptureClusterPoint(
            new CaptureClusterSource(1, 1, 1, DateTimeOffset.Parse("2026-04-09T20:00:00+08:00"), "{}", "/tmp/1"),
            [1f, 0f, 0f]),
        new CaptureClusterPoint(
            new CaptureClusterSource(2, 2, 1, DateTimeOffset.Parse("2026-04-09T20:01:00+08:00"), "{}", "/tmp/2"),
            [0.98f, 0.02f, 0f]),
        new CaptureClusterPoint(
            new CaptureClusterSource(3, 3, 1, DateTimeOffset.Parse("2026-04-09T20:02:00+08:00"), "{}", "/tmp/3"),
            [0f, 1f, 0f]),
    };

    var result = service.ClusterByFeatures(captures, similarityThreshold: 0.9d, minPoints: 2);
    Assert(result.ClusterCount == 1, $"Expected 1 cluster but got {result.ClusterCount}.");
    Assert(result.NoiseCount == 1, $"Expected 1 noise point but got {result.NoiseCount}.");
    Assert(result.Groups[0].Members.Count == 2, $"Expected 2 members in cluster but got {result.Groups[0].Members.Count}.");
}

static void TestTemporalFallback()
{
    var service = new FeatureClusteringService();
    var captures = new[]
    {
        new CaptureClusterSource(10, 1, 1, DateTimeOffset.Parse("2026-04-09T20:00:00+08:00"), "{}", null),
        new CaptureClusterSource(11, 1, 1, DateTimeOffset.Parse("2026-04-09T20:10:00+08:00"), "{}", null),
        new CaptureClusterSource(12, 1, 1, DateTimeOffset.Parse("2026-04-09T21:00:00+08:00"), "{}", null),
    };

    var result = service.ClusterByTemporalWindow(captures, gapMinutes: 30);
    Assert(result.ClusterCount == 2, $"Expected 2 temporal buckets but got {result.ClusterCount}.");
}

static void TestAlertStreamMultipartOneXml()
{
    var boundary = "sampleBoundary123";
    var body = Encoding.UTF8.GetBytes("<e n=\"1\"/>");
    var headers = $"Content-Type: application/xml\r\nContent-Length: {body.Length}\r\n";
    var prefix = Encoding.UTF8.GetBytes("--" + boundary + "\r\n" + headers + "\r\n");
    var buffer = new List<byte>();
    buffer.AddRange(prefix);
    buffer.AddRange(body);

    var logger = NullLogger.Instance;
    var parsed = new List<string>();
    HikvisionAlertStreamMultipartParser.DrainBufferAsync(
        buffer,
        boundary,
        async (_, data) =>
        {
            parsed.Add(Encoding.UTF8.GetString(data));
            await Task.CompletedTask;
        },
        logger,
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(parsed.Count == 1, $"Expected 1 part, got {parsed.Count}");
    Assert(buffer.Count == 0, "Buffer should be drained.");
    Assert(parsed[0].Contains("<e", StringComparison.Ordinal), "XML body mismatch.");
}

static void TestXlsxWriter()
{
    var service = new TabularExportService();
    var path = Path.Combine(Path.GetTempPath(), $"aura-export-{Guid.NewGuid():N}.xlsx");
    try
    {
        service.WriteXlsxAsync(path, "导出数据", [
            ["列1", "列2"],
            ["A", "B"]
        ]).GetAwaiter().GetResult();

        Assert(File.Exists(path), "Expected xlsx file to exist.");
        using var archive = ZipFile.OpenRead(path);
        var workbook = archive.GetEntry("xl/workbook.xml");
        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
        var styles = archive.GetEntry("xl/styles.xml");
        Assert(workbook is not null, "Missing xl/workbook.xml.");
        Assert(worksheet is not null, "Missing xl/worksheets/sheet1.xml.");
        Assert(styles is not null, "Missing xl/styles.xml.");

        using var reader = new StreamReader(worksheet!.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        Assert(xml.Contains("inlineStr", StringComparison.Ordinal), "Worksheet should use inline strings.");
        Assert(xml.Contains("列1", StringComparison.Ordinal), "Worksheet should contain header text.");
    }
    finally
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
