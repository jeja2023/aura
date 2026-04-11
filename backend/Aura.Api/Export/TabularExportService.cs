using Aura.Api.Models;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Aura.Api.Export;

internal sealed class TabularExportService
{
    public async Task WriteCsvAsync(string path, IReadOnlyList<string[]> rows, CancellationToken cancellationToken = default)
    {
        var csvBody = string.Join(Environment.NewLine, rows.Select(r => string.Join(",", r.Select(ToCsvCell))));
        var csv = "\uFEFF" + csvBody;
        await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
    }

    public Task WriteXlsxAsync(
        string path,
        string sheetName,
        IReadOnlyList<string[]> rows,
        CancellationToken cancellationToken = default)
    {
        sheetName = SanitizeSheetName(sheetName);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        AddEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
        AddEntry(archive, "_rels/.rels", BuildRootRelationshipsXml());
        AddEntry(archive, "docProps/app.xml", BuildAppXml(sheetName));
        AddEntry(archive, "docProps/core.xml", BuildCoreXml());
        AddEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheetName));
        AddEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml());
        AddEntry(archive, "xl/styles.xml", BuildStylesXml());
        AddEntry(archive, "xl/worksheets/sheet1.xml", BuildWorksheetXml(rows));
        return Task.CompletedTask;
    }

    internal static string SanitizeSheetName(string? sheetName)
    {
        var raw = string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim();
        var invalid = new HashSet<char>(['\\', '/', '?', '*', '[', ']', ':']);
        var cleaned = new string(raw.Where(ch => !invalid.Contains(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "Sheet1";
        }

        cleaned = cleaned.Length > 31 ? cleaned[..31] : cleaned;
        return cleaned;
    }

    internal static string ToCsvCell(string? text)
    {
        var value = text ?? string.Empty;
        if (value.Contains('"'))
        {
            value = value.Replace("\"", "\"\"");
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value}\"";
        }

        return value;
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildContentTypesXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """;
    }

    private static string BuildRootRelationshipsXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
            </Relationships>
            """;
    }

    private static string BuildWorkbookRelationshipsXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """;
    }

    private static string BuildWorkbookXml(string sheetName)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """;
    }

    private static string BuildStylesXml()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2">
                <font>
                  <sz val="11"/>
                  <name val="Microsoft YaHei"/>
                </font>
                <font>
                  <b/>
                  <sz val="11"/>
                  <name val="Microsoft YaHei"/>
                </font>
              </fonts>
              <fills count="2">
                <fill><patternFill patternType="none"/></fill>
                <fill><patternFill patternType="gray125"/></fill>
              </fills>
              <borders count="1">
                <border>
                  <left/><right/><top/><bottom/><diagonal/>
                </border>
              </borders>
              <cellStyleXfs count="1">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
              </cellStyleXfs>
              <cellXfs count="2">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1">
                  <alignment vertical="center" wrapText="1"/>
                </xf>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyAlignment="1" applyFont="1">
                  <alignment horizontal="center" vertical="center" wrapText="1"/>
                </xf>
              </cellXfs>
              <cellStyles count="1">
                <cellStyle name="Normal" xfId="0" builtinId="0"/>
              </cellStyles>
            </styleSheet>
            """;
    }

    private static string BuildWorksheetXml(IReadOnlyList<string[]> rows)
    {
        var maxColumns = rows.Count == 0 ? 1 : rows.Max(x => x.Length);
        var dimension = $"{ColumnName(maxColumns)}{Math.Max(rows.Count, 1)}";
        var sheetData = new StringBuilder();

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var cells = rows[rowIndex];
            var cellXml = new StringBuilder();
            for (var colIndex = 0; colIndex < cells.Length; colIndex++)
            {
                var cellRef = $"{ColumnName(colIndex + 1)}{rowIndex + 1}";
                var styleIndex = rowIndex == 0 ? 1 : 0;
                cellXml.Append($"""<c r="{cellRef}" t="inlineStr" s="{styleIndex}"><is><t xml:space="preserve">{Escape(cells[colIndex])}</t></is></c>""");
            }

            sheetData.Append($"""<row r="{rowIndex + 1}">{cellXml}</row>""");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <dimension ref="A1:{dimension}"/>
              <sheetViews>
                <sheetView workbookViewId="0"/>
              </sheetViews>
              <sheetFormatPr defaultRowHeight="18"/>
              <sheetData>{sheetData}</sheetData>
            </worksheet>
            """;
    }

    private static string BuildAppXml(string sheetName)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
                        xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
              <Application>Aura.Api</Application>
              <HeadingPairs>
                <vt:vector size="2" baseType="variant">
                  <vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant>
                  <vt:variant><vt:i4>1</vt:i4></vt:variant>
                </vt:vector>
              </HeadingPairs>
              <TitlesOfParts>
                <vt:vector size="1" baseType="lpstr">
                  <vt:lpstr>{Escape(sheetName)}</vt:lpstr>
                </vt:vector>
              </TitlesOfParts>
            </Properties>
            """;
    }

    private static string BuildCoreXml()
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                               xmlns:dc="http://purl.org/dc/elements/1.1/"
                               xmlns:dcterms="http://purl.org/dc/terms/"
                               xmlns:dcmitype="http://purl.org/dc/dcmitype/"
                               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:creator>Aura.Api</dc:creator>
              <cp:lastModifiedBy>Aura.Api</cp:lastModifiedBy>
              <dcterms:created xsi:type="dcterms:W3CDTF">{now}</dcterms:created>
              <dcterms:modified xsi:type="dcterms:W3CDTF">{now}</dcterms:modified>
            </cp:coreProperties>
            """;
    }

    private static string ColumnName(int index)
    {
        if (index <= 0)
        {
            return "A";
        }

        var value = index;
        var name = new StringBuilder();
        while (value > 0)
        {
            value--;
            name.Insert(0, (char)('A' + (value % 26)));
            value /= 26;
        }

        return name.ToString();
    }

    private static string Escape(string? value)
    {
        return SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
    }
}

