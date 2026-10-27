using System.Runtime.InteropServices;

namespace SvgViewer;

internal static class FileCatalog
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".svg" or ".eps";

    public static List<string> Scan(string path) => Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
        .Where(IsSupported)
        .OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(StrCmpLogicalW)).ToList();

    public static byte[] ReadSvg(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .svg 文件。");
        // Parsing validates the file, but the original bytes preserve its declared encoding.
        using var input = File.OpenRead(path);
        if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("文件超过 64 MB，暂不支持打开。");
        using (var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024,
            CloseInput = false
        }))
        {
            reader.MoveToContent();
            if (reader.LocalName != "svg" || reader.NamespaceURI != "http://www.w3.org/2000/svg")
                throw new InvalidDataException("这个文件不是有效的 SVG 图片。");
            while (reader.Read()) { }
        }
        input.Position = 0;
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
