using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotMarc.Dns;

namespace DotMarc.Bimi;

/// <summary>Turns an ordinary SVG logo into SVG Tiny PS, the profile mailboxes require for BIMI, as far as that can be done
/// safely: sets version and baseProfile, adds a title, removes scripts, animation, event handlers and editor metadata,
/// and pads a non-square logo to square. What can't be fixed (an embedded bitmap, links to other files) is reported.</summary>
public static class SvgTinyPs
{
    public sealed record Result(byte[]? Svg, IReadOnlyList<string> Fixed, IReadOnlyList<string> Problems);

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly string[] Removed = ["script", "animate", "animateMotion", "animateTransform", "animateColor", "set", "foreignObject", "metadata"];

    public static Result Prepare(byte[] input, string title)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(input), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return new(null, [], ["That isn't a valid SVG file."]);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg")
        {
            return new(null, [], ["That isn't an SVG file."]);
        }

        var fixes = new List<string>();
        document.DocumentType?.Remove();

        if ((string?)root.Attribute("version") != "1.2" || (string?)root.Attribute("baseProfile") != "tiny-ps")
        {
            root.SetAttributeValue("version", "1.2");
            root.SetAttributeValue("baseProfile", "tiny-ps");
            fixes.Add("marked it as SVG Tiny PS (version 1.2, baseProfile tiny-ps)");
        }

        var removed = root.Descendants().Where(element => Removed.Contains(element.Name.LocalName)).ToList();
        if (removed.Count > 0)
        {
            removed.ForEach(element => element.Remove());
            fixes.Add("removed scripts, animation and editor metadata");
        }

        var handlers = root.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)).ToList();
        var position = new[] { root.Attribute("x"), root.Attribute("y") }.OfType<XAttribute>().ToList();
        if (handlers.Count + position.Count > 0)
        {
            handlers.Concat(position).ToList().ForEach(attribute => attribute.Remove());
            fixes.Add("removed attributes BIMI doesn't allow");
        }

        if (!root.Elements().Any(element => element.Name.LocalName == "title" && !string.IsNullOrWhiteSpace(element.Value)))
        {
            root.Elements().Where(element => element.Name.LocalName == "title").Remove();
            root.AddFirst(new XElement(root.Name.Namespace == XNamespace.None ? Svg + "title" : root.Name.Namespace + "title", title));
            fixes.Add($"added the title \"{title}\"");
        }

        if (SquareViewBox(root) is { } square)
        {
            root.SetAttributeValue("viewBox", square);
            root.Attribute("width")?.Remove();
            root.Attribute("height")?.Remove();
            fixes.Add("padded it to a square, centred");
        }

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false }))
        {
            document.Save(writer);
        }

        var svg = output.ToArray();
        var problems = BimiChecker.LogoProblems(svg).Select(problem => char.ToUpperInvariant(problem[0]) + problem[1..] + ".").ToList();
        if (svg.Length > BimiChecker.MaxLogoBytes)
        {
            problems.Add($"It's {svg.Length / 1024} KB; BIMI logos can be 32 KB at most. Simplify it in the design tool, or export it without embedded fonts.");
        }

        return new(problems.Count == 0 ? svg : null, fixes, problems);
    }

    /// <summary>A square viewBox around a non-square one, keeping the logo centred. Null when it's square already, or the
    /// size can't be read.</summary>
    private static string? SquareViewBox(XElement root)
    {
        double[]? box = ((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } parts
            && parts.All(part => double.TryParse(part, CultureInfo.InvariantCulture, out _))
            ? parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray()
            : Size(root) is { } size ? [0, 0, size.Width, size.Height] : null;
        if (box is null || Math.Abs(box[2] - box[3]) <= 0.5)
        {
            return box is null || root.Attribute("viewBox") is not null ? null : Format(box);
        }

        var side = Math.Max(box[2], box[3]);
        return Format([box[0] - (side - box[2]) / 2, box[1] - (side - box[3]) / 2, side, side]);

        static string Format(double[] values) => string.Join(' ', values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    private static (double Width, double Height)? Size(XElement root) =>
        double.TryParse(((string?)root.Attribute("width"))?.Replace("px", ""), CultureInfo.InvariantCulture, out var width)
        && double.TryParse(((string?)root.Attribute("height"))?.Replace("px", ""), CultureInfo.InvariantCulture, out var height)
            ? (width, height)
            : null;
}
