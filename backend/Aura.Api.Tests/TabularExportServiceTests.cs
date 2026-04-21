/* 文件：表格导出单元测试（TabularExportServiceTests.cs） | File: Tabular export service unit tests */
using System.IO.Compression;
using System.Text;
using Aura.Api.Export;
using Xunit;

namespace Aura.Api.Tests;

public sealed class TabularExportServiceTests
{
    [Fact]
    public async Task Xlsx写入应产出可解析的OpenXml工作簿()
    {
        var service = new TabularExportService();
        var path = Path.Combine(Path.GetTempPath(), $"aura-export-{Guid.NewGuid():N}.xlsx");
        try
        {
            await service.WriteXlsxAsync(path, "导出数据",
                [
                    ["列1", "列2"],
                    ["A", "B"]
                ]);

            Assert.True(File.Exists(path));
            using var archive = ZipFile.OpenRead(path);
            var workbook = archive.GetEntry("xl/workbook.xml");
            var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
            var styles = archive.GetEntry("xl/styles.xml");
            Assert.NotNull(workbook);
            Assert.NotNull(worksheet);
            Assert.NotNull(styles);

            using var reader = new StreamReader(worksheet!.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            Assert.Contains("inlineStr", xml, StringComparison.Ordinal);
            Assert.Contains("列1", xml, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

