using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace Synapse;

/// Turns documents into plain text for the model: code and text files, PDF, Word, PowerPoint, Excel, RTF and HTML.
public static class TextExtract
{
    public static string Read(string path)
    {
        if (Directory.Exists(path)) return $"{path} is a folder. Use list_files.";
        if (!File.Exists(path)) return $"File not found: {path}";
        try
        {
            return Util.Ext(path) switch
            {
                "pdf" => PdfText(File.ReadAllBytes(path)),
                "docx" or "docm" or "dotx" => Docx(path),
                "pptx" or "pptm" => Pptx(path),
                "xlsx" or "xlsm" => Xlsx(path),
                "odt" or "odp" or "ods" => OpenDocument(path),
                "rtf" => Rtf(File.ReadAllText(path)),
                "html" or "htm" => Tools.HtmlToText(File.ReadAllText(path)),
                _ => PlainText(path),
            };
        }
        catch (Exception e)
        {
            return $"Couldn't read {Path.GetFileName(path)}: {e.Message}";
        }
    }

    static string PlainText(string path)
    {
        var info = new FileInfo(path);
        var bytes = info.Length > 20_000_000 ? ReadPrefix(path, 20_000_000) : File.ReadAllBytes(path);
        if (LooksBinary(bytes)) return $"Binary file ({Util.FormatBytes(info.Length)}), can't show as text.";
        // UTF-16 files (common on Windows, e.g. PowerShell output) start with a BOM.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var text = new UTF8Encoding(false, false).GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    static byte[] ReadPrefix(string path, int n)
    {
        using var f = File.OpenRead(path);
        var buf = new byte[n];
        var read = f.Read(buf, 0, n);
        return buf[..read];
    }

    public static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return false;
        var n = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < n; i++) if (bytes[i] == 0) return true;
        return false;
    }

    public static string PdfText(byte[] data)
    {
        try
        {
            using var doc = PdfDocument.Open(data);
            var sb = new StringBuilder();
            foreach (var page in doc.GetPages())
            {
                var text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
                sb.Append($"--- Page {page.Number} ---\n").Append(text.Trim()).Append("\n\n");
                if (sb.Length > 400_000) break;
            }
            var s = sb.ToString().Trim();
            return s.Length == 0 ? "This PDF has no text layer (it may be scanned images)." : s;
        }
        catch (Exception e) { return "Couldn't read PDF: " + e.Message; }
    }

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    static XDocument? Entry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name);
        if (e == null) return null;
        using var s = e.Open();
        return XDocument.Load(s);
    }

    static string Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var doc = Entry(zip, "word/document.xml") ?? throw new InvalidDataException("Not a Word document");
        var sb = new StringBuilder();
        foreach (var p in doc.Descendants(W + "p"))
        {
            var line = new StringBuilder();
            foreach (var node in p.Descendants())
            {
                if (node.Name == W + "t") line.Append(node.Value);
                else if (node.Name == W + "tab") line.Append('\t');
                else if (node.Name == W + "br") line.Append('\n');
            }
            var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            var isList = p.Element(W + "pPr")?.Element(W + "numPr") != null;
            var prefix = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var lvl)
                ? new string('#', Math.Clamp(lvl, 1, 6)) + " " : isList ? "• " : "";
            sb.Append(prefix).Append(line).Append('\n');
        }
        return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
    }

    static string Pptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
            .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).ToList();
        var sb = new StringBuilder();
        var n = 0;
        foreach (var s in slides)
        {
            n++;
            var doc = Entry(zip, s.FullName);
            if (doc == null) continue;
            sb.Append($"--- Slide {n} ---\n");
            foreach (var p in doc.Descendants(A + "p"))
            {
                var t = string.Concat(p.Descendants(A + "t").Select(x => x.Value));
                if (t.Length > 0) sb.Append(t).Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    static string Xlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = Entry(zip, "xl/sharedStrings.xml")?.Descendants(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() ?? [];
        var workbook = Entry(zip, "xl/workbook.xml");
        var names = workbook?.Descendants(S + "sheet").Select(x => x.Attribute("name")?.Value ?? "Sheet").ToList() ?? [];
        var sheets = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$"))
            .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            var doc = Entry(zip, sheets[i].FullName);
            if (doc == null) continue;
            sb.Append($"--- Sheet: {(i < names.Count ? names[i] : $"Sheet{i + 1}")} ---\n");
            var rows = 0;
            foreach (var row in doc.Descendants(S + "row"))
            {
                var cells = row.Elements(S + "c").Select(c =>
                {
                    var v = c.Element(S + "v")?.Value ?? string.Concat(c.Descendants(S + "t").Select(t => t.Value));
                    if (c.Attribute("t")?.Value == "s" && int.TryParse(v, out var idx) && idx < shared.Count) v = shared[idx];
                    return v.Replace('\t', ' ').Replace('\n', ' ');
                });
                sb.Append(string.Join('\t', cells)).Append('\n');
                if (++rows >= 2000) { sb.Append("…(more rows)\n"); break; }
            }
            sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    static string OpenDocument(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var doc = Entry(zip, "content.xml") ?? throw new InvalidDataException("Not an OpenDocument file");
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var paras = doc.Descendants().Where(e => e.Name == text + "p" || e.Name == text + "h").Select(e => e.Value);
        return string.Join("\n", paras).Trim();
    }

    /// Good-enough RTF to text: drops control words and groups like fonts and colors.
    public static string Rtf(string rtf)
    {
        var sb = new StringBuilder();
        var skipDepth = new Stack<bool>();
        var skipping = false;
        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];
            if (c == '{') { skipDepth.Push(skipping); continue; }
            if (c == '}') { skipping = skipDepth.Count > 0 && skipDepth.Pop(); continue; }
            if (c == '\\')
            {
                if (i + 1 >= rtf.Length) break;
                var n = rtf[i + 1];
                if (n is '\\' or '{' or '}') { if (!skipping) sb.Append(n); i++; continue; }
                if (n == '\'' && i + 3 < rtf.Length)
                {
                    if (!skipping && int.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var code)) sb.Append((char)code);
                    i += 3;
                    continue;
                }
                if (n == '*') { skipping = true; i++; continue; }
                var j = i + 1;
                while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                var word = rtf[(i + 1)..j];
                while (j < rtf.Length && (char.IsDigit(rtf[j]) || rtf[j] == '-')) j++;
                if (j < rtf.Length && rtf[j] == ' ') j++;
                if (word is "fonttbl" or "colortbl" or "stylesheet" or "info" or "pict" or "header" or "footer") skipping = true;
                if (!skipping && word is "par" or "line") sb.Append('\n');
                if (!skipping && word == "tab") sb.Append('\t');
                i = j - 1;
                continue;
            }
            if (!skipping && c != '\r' && c != '\n') sb.Append(c);
        }
        return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
    }
}
