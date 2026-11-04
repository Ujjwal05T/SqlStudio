using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using SqlStudio.Views;

namespace SqlStudio.Services;

public enum ExportFormat { Csv, Excel, Json }

/// <summary>Writes a result grid to a file. Excel files are built by hand (a zip of XML), so no extra package is needed.</summary>
public static class ResultExporter
{
    /// <summary>Excel's own limits: 1,048,576 rows including the header, 16,384 columns, 32,767 characters per cell.</summary>
    public const int ExcelMaxDataRows = 1_048_575;
    public const int ExcelMaxColumns = 16_384;
    const int ExcelMaxCellChars = 32_767;
    const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Excel => "xlsx",
        _ => "json",
    };

    public static void Write(ResultSet set, ExportFormat format, Stream output)
    {
        switch (format)
        {
            case ExportFormat.Csv: WriteCsv(set, output); break;
            case ExportFormat.Excel: WriteExcel(set, output); break;
            default: WriteJson(set, output); break;
        }
    }

    // ---- CSV ----

    public static void WriteCsv(ResultSet set, Stream output)
    {
        // The BOM makes Excel read non-English text as UTF-8 instead of the local code page.
        using var writer = new StreamWriter(output, new UTF8Encoding(true), 65536, leaveOpen: true) { NewLine = "\r\n" };
        writer.WriteLine(string.Join(',', set.Columns.Select(c => CsvField(c.Name))));
        foreach (var row in set.Rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0)
                    writer.Write(',');
                // NULL is an empty field, so it is not mistaken for the text "NULL".
                if (row[i] is not null)
                    writer.Write(CsvField(CellFormatter.Format(row[i], set.Columns[i].TypeName)));
            }
            writer.WriteLine();
        }
    }

    static string CsvField(string text) =>
        text.AsSpan().IndexOfAny(",\"\r\n") >= 0 || text.StartsWith(' ') || text.EndsWith(' ')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;

    // ---- JSON ----

    public static void WriteJson(ResultSet set, Stream output)
    {
        var names = UniqueNames(set.Columns);
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (var row in set.Rows)
        {
            json.WriteStartObject();
            for (var i = 0; i < row.Length; i++)
            {
                json.WritePropertyName(names[i]);
                WriteJsonValue(json, row[i], set.Columns[i].TypeName);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    static void WriteJsonValue(Utf8JsonWriter json, object? value, string typeName)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case bool b: json.WriteBooleanValue(b); break;
            case byte or short or int or long: json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case decimal d: json.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): json.WriteNumberValue(f); break;
            case double d when double.IsFinite(d): json.WriteNumberValue(d); break;
            default: json.WriteStringValue(CellFormatter.Format(value, typeName)); break;
        }
    }

    /// <summary>JSON objects and spreadsheets need a name per column; blank or repeated names get a suffix.</summary>
    public static string[] UniqueNames(IReadOnlyList<ResultColumn> columns)
    {
        var names = new string[columns.Count];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            var name = string.IsNullOrWhiteSpace(columns[i].Name) ? $"Column{i + 1}" : columns[i].Name;
            var candidate = name;
            for (var n = 2; !seen.Add(candidate); n++)
                candidate = $"{name}_{n}";
            names[i] = candidate;
        }
        return names;
    }

    // ---- Excel (.xlsx) ----

    public static void WriteExcel(ResultSet set, Stream output)
    {
        if (set.Columns.Count > ExcelMaxColumns)
            throw new InvalidOperationException($"Excel allows {ExcelMaxColumns:N0} columns; this result has {set.Columns.Count:N0}. Export it as CSV or JSON instead.");
        if (set.Rows.Count > ExcelMaxDataRows)
            throw new InvalidOperationException($"Excel allows {ExcelMaxDataRows:N0} rows; this result has {set.Rows.Count:N0}. Export it as CSV or JSON instead.");

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        AddText(zip, "[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>");
        AddText(zip, "_rels/.rels",
            $"<Relationships xmlns=\"{PackageRelNs}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        AddText(zip, "xl/workbook.xml",
            $"<workbook xmlns=\"{MainNs}\" xmlns:r=\"{RelNs}\"><sheets><sheet name=\"Results\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        AddText(zip, "xl/_rels/workbook.xml.rels",
            $"<Relationships xmlns=\"{PackageRelNs}\">" +
            $"<Relationship Id=\"rId1\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/></Relationships>");
        AddText(zip, "xl/styles.xml",
            $"<styleSheet xmlns=\"{MainNs}\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
        xml.WriteStartDocument(true);
        xml.WriteStartElement("worksheet", MainNs);
        // Keep the header visible while scrolling.
        xml.WriteRaw("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        xml.WriteStartElement("sheetData");

        var names = UniqueNames(set.Columns);
        xml.WriteStartElement("row");
        xml.WriteAttributeString("r", "1");
        for (var c = 0; c < names.Length; c++)
            WriteText(xml, Reference(c, 1), names[c], style: 1);
        xml.WriteEndElement();

        var rowNumber = 1;
        foreach (var row in set.Rows)
        {
            rowNumber++;
            xml.WriteStartElement("row");
            xml.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
            for (var c = 0; c < row.Length; c++)
                WriteCell(xml, Reference(c, rowNumber), row[c], set.Columns[c].TypeName);
            xml.WriteEndElement();
        }

        xml.WriteEndElement(); // sheetData
        xml.WriteEndElement(); // worksheet
        xml.WriteEndDocument();
    }

    static void AddText(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + content);
    }

    static void WriteCell(XmlWriter xml, string reference, object? value, string typeName)
    {
        switch (value)
        {
            case null:
                return; // an empty cell is how a spreadsheet shows NULL
            case bool b:
                xml.WriteStartElement("c");
                xml.WriteAttributeString("r", reference);
                xml.WriteAttributeString("t", "b");
                xml.WriteElementString("v", b ? "1" : "0");
                xml.WriteEndElement();
                return;
            case byte or short or int:
                WriteNumber(xml, reference, Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            case long l when Math.Abs(l) <= 9_007_199_254_740_992: // beyond this a double loses digits
                WriteNumber(xml, reference, l.ToString(CultureInfo.InvariantCulture));
                return;
            case decimal d when SignificantDigits(d) <= 15:
                WriteNumber(xml, reference, d.ToString(CultureInfo.InvariantCulture));
                return;
            case float f when float.IsFinite(f):
                WriteNumber(xml, reference, f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case double d when double.IsFinite(d):
                WriteNumber(xml, reference, d.ToString("R", CultureInfo.InvariantCulture));
                return;
            default:
                // Text keeps big numbers, dates, GUIDs and binary exactly as the grid shows them.
                WriteText(xml, reference, CellFormatter.Format(value, typeName), style: 0);
                return;
        }
    }

    static int SignificantDigits(decimal d) => d.ToString(CultureInfo.InvariantCulture).Count(char.IsDigit);

    static void WriteNumber(XmlWriter xml, string reference, string number)
    {
        xml.WriteStartElement("c");
        xml.WriteAttributeString("r", reference);
        xml.WriteElementString("v", number);
        xml.WriteEndElement();
    }

    static void WriteText(XmlWriter xml, string reference, string text, int style)
    {
        xml.WriteStartElement("c");
        xml.WriteAttributeString("r", reference);
        xml.WriteAttributeString("t", "inlineStr");
        if (style != 0)
            xml.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
        xml.WriteStartElement("is");
        xml.WriteStartElement("t");
        xml.WriteAttributeString("xml", "space", null, "preserve");
        xml.WriteString(CleanForXml(text.Length > ExcelMaxCellChars ? text[..ExcelMaxCellChars] : text));
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    /// <summary>XML cannot hold most control characters; they are replaced instead of failing the whole export.</summary>
    static string CleanForXml(string text)
    {
        if (text.All(XmlConvert.IsXmlChar))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(text[i]).Append(text[i + 1]);
                i++;
            }
            else
            {
                sb.Append(XmlConvert.IsXmlChar(text[i]) ? text[i] : '�');
            }
        }
        return sb.ToString();
    }

    /// <summary>Column index and row number as an A1 reference: (0,1) is A1, (26,5) is AA5.</summary>
    public static string Reference(int column, int row)
    {
        var letters = new StringBuilder();
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
            letters.Insert(0, (char)('A' + (n - 1) % 26));
        return letters.ToString() + row.ToString(CultureInfo.InvariantCulture);
    }
}
