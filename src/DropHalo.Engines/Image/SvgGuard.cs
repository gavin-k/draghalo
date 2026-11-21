using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DropHalo.Engines.Image;

/// <summary>
/// Refuses an SVG that points outside itself before a renderer reads it: an image, a stylesheet, a
/// <c>&lt;use&gt;</c> or an entity can name a local file or a web address, and rendering it would read the
/// file or go online. Only references into the SVG itself (<c>#id</c>) and inline raster <c>data:</c> are left;
/// a link on <c>&lt;a&gt;</c> is fine, nothing follows it while drawing.
/// </summary>
internal static partial class SvgGuard
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 64L * 1024 * 1024,
    };

    /// <summary>Decodes UTF-8 / UTF-16 / UTF-32 SVG bytes (with or without BOM) and checks for external references.</summary>
    public static string? FindExternal(byte[] bytes)
    {
        _ = PrepareSafeUtf8(bytes, out var external);
        return external;
    }

    /// <summary>
    /// Validates <paramref name="bytes"/> and returns clean UTF-8 SVG bytes with any DTD stripped so the renderer
    /// parses the exact same byte representation without fetching external DTDs. Returns null when <paramref name="external"/> is found.
    /// </summary>
    public static byte[]? PrepareSafeUtf8(byte[] bytes, out string? external)
    {
        var decoded = DecodeSvg(bytes);
        external = CheckAndStripDoctype(decoded, out var sanitized);
        if (external is not null)
        {
            return null;
        }

        if (XmlDeclEncoding().IsMatch(sanitized))
        {
            sanitized = XmlDeclEncoding().Replace(sanitized, "encoding=\"utf-8\"", 1);
        }

        return Encoding.UTF8.GetBytes(sanitized);
    }

    /// <summary>What the SVG points at outside itself, or null when it points nowhere.</summary>
    public static string? FindExternal(string svg) => CheckAndStripDoctype(svg, out _);

    private static string? CheckAndStripDoctype(string svg, out string sanitized)
    {
        var cleaned = svg.Contains('\0', StringComparison.Ordinal) ? svg.Replace("\0", "", StringComparison.Ordinal) : svg;
        sanitized = cleaned;

        if (Entity().Match(cleaned) is { Success: true } entity)
        {
            return entity.Groups["what"].Value.Trim();
        }

        if (DoctypeInternalSubset().Match(cleaned) is { Success: true } subset)
        {
            return subset.Groups["what"].Value.Trim();
        }

        if (cleaned.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match doctype in DoctypeAny().Matches(cleaned))
            {
                if (!StandardSvgDoctype().IsMatch(doctype.Value))
                {
                    return doctype.Value.Trim();
                }
            }

            cleaned = DoctypeAny().Replace(cleaned, "");
            sanitized = cleaned;
        }

        var styleDepth = 0;
        using var reader = XmlReader.Create(new StringReader(cleaned), XmlSettings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.ProcessingInstruction when reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase):
                    if (PiHref().Match(reader.Value) is { Success: true } pi)
                    {
                        if (OutsideRef(pi.Groups["what"].Value) is { } piRef)
                        {
                            return piRef;
                        }

                        break;
                    }

                    return $"<?{reader.Name} {reader.Value.Trim()}?>";

                case XmlNodeType.Element:
                    var localElement = reader.LocalName;
                    var isAnchor = localElement.Equals("a", StringComparison.OrdinalIgnoreCase);
                    var entersStyle = localElement.Equals("style", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement;
                    var animatesHref = (localElement.Equals("set", StringComparison.OrdinalIgnoreCase) || localElement.Equals("animate", StringComparison.OrdinalIgnoreCase))
                        && IsHrefTarget(reader.GetAttribute("attributeName"));
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            var attr = reader.LocalName;
                            var value = reader.Value;
                            if ((!isAnchor && attr.Equals("href", StringComparison.OrdinalIgnoreCase))
                                || attr.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                if (OutsideRef(value) is { } ext)
                                {
                                    return ext;
                                }
                            }
                            else if (animatesHref)
                            {
                                if ((attr.Equals("to", StringComparison.OrdinalIgnoreCase) || attr.Equals("from", StringComparison.OrdinalIgnoreCase))
                                    && OutsideRef(value) is { } animExt)
                                {
                                    return animExt;
                                }

                                if (attr.Equals("values", StringComparison.OrdinalIgnoreCase))
                                {
                                    foreach (var item in value.Split(';'))
                                    {
                                        if (OutsideRef(item) is { } valExt)
                                        {
                                            return valExt;
                                        }
                                    }
                                }
                            }

                            if (FindCssExternal(value) is { } cssAttr)
                            {
                                return cssAttr;
                            }
                        }

                        reader.MoveToElement();
                    }

                    if (entersStyle)
                    {
                        styleDepth++;
                    }

                    break;

                case XmlNodeType.EndElement when styleDepth > 0 && reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase):
                    styleDepth--;
                    break;

                case XmlNodeType.Text or XmlNodeType.CDATA when styleDepth > 0:
                    if (FindCssExternal(reader.Value) is { } cssBody)
                    {
                        return cssBody;
                    }

                    break;
            }
        }

        return null;
    }

    private static bool IsHrefTarget(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return false;
        }

        var trimmed = attributeName.AsSpan().Trim();
        var colon = trimmed.IndexOf(':');
        if (colon >= 0)
        {
            trimmed = trimmed[(colon + 1)..];
        }

        return trimmed.Equals("href", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("src", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeSvg(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes is [0xFF, 0xFE, 0x00, 0x00, ..])
        {
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes is [0x00, 0x00, 0xFE, 0xFF, ..])
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes, 4, bytes.Length - 4);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 4 && bytes[0] != 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0)
        {
            return Encoding.UTF32.GetString(bytes);
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] != 0)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] != 0 && bytes[1] == 0)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0 && bytes[1] != 0)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? OutsideRef(string raw)
    {
        var trimmed = TrimControlsAndWhitespace(raw);
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return null;
        }

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = trimmed.IndexOf(',');
            if (comma < 0)
            {
                return trimmed;
            }

            var meta = UnescapePercent(trimmed[5..comma]);
            if (meta.Contains("svg", StringComparison.OrdinalIgnoreCase)
                || meta.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || meta.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return null;
        }

        return trimmed;
    }

    private static string UnescapePercent(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string TrimControlsAndWhitespace(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && (value[start] <= ' ' || char.IsWhiteSpace(value[start])))
        {
            start++;
        }

        while (end > start && (value[end - 1] <= ' ' || char.IsWhiteSpace(value[end - 1])))
        {
            end--;
        }

        return value[start..end];
    }

    private static string? FindCssExternal(string css)
    {
        if (!css.Contains("url", StringComparison.OrdinalIgnoreCase)
            && !css.Contains("import", StringComparison.OrdinalIgnoreCase)
            && !css.Contains("image-set", StringComparison.OrdinalIgnoreCase)
            && !css.Contains('\\', StringComparison.Ordinal)
            && !css.Contains("/*", StringComparison.Ordinal))
        {
            return null;
        }

        var i = 0;
        var inImageSet = 0;
        while (i < css.Length)
        {
            SkipCssTrivia(css, ref i);
            if (i >= css.Length)
            {
                break;
            }

            var c = css[i];
            if (c is '"' or '\'')
            {
                var str = ReadCssString(css, ref i);
                if (inImageSet > 0 && OutsideRef(str) is { } imgSetTarget)
                {
                    return imgSetTarget;
                }

                continue;
            }

            if (c == '@')
            {
                var start = i++;
                var atName = ReadCssIdent(css, ref i);
                if (atName.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    var end = css.IndexOf(';', i);
                    var raw = end >= 0 ? css[start..end] : css[start..];
                    return "@import " + UnescapeCssSpan(raw[(start == i - 1 ? 1 : i - start)..]).Trim();
                }

                continue;
            }

            if (c == '(')
            {
                if (inImageSet > 0)
                {
                    inImageSet++;
                }

                i++;
                continue;
            }

            if (c == ')')
            {
                if (inImageSet > 0)
                {
                    inImageSet--;
                }

                i++;
                continue;
            }

            if (IsCssIdentChar(c) || c == '\\')
            {
                var ident = ReadCssIdent(css, ref i);
                SkipCssTrivia(css, ref i);
                if (i < css.Length && css[i] == '(')
                {
                    if (ident.Equals("url", StringComparison.OrdinalIgnoreCase) || ident.Equals("src", StringComparison.OrdinalIgnoreCase))
                    {
                        i++; // '('
                        SkipCssTrivia(css, ref i);
                        string urlValue;
                        if (i < css.Length && css[i] is '"' or '\'')
                        {
                            urlValue = ReadCssString(css, ref i);
                            SkipCssTrivia(css, ref i);
                            if (i < css.Length && css[i] == ')')
                            {
                                i++;
                            }
                        }
                        else
                        {
                            urlValue = ReadCssUnquotedUrl(css, ref i);
                        }

                        if (OutsideRef(urlValue) is { } target)
                        {
                            return target;
                        }

                        continue;
                    }

                    if (ident.EndsWith("image-set", StringComparison.OrdinalIgnoreCase))
                    {
                        inImageSet++;
                        i++;
                        continue;
                    }
                }

                continue;
            }

            i++;
        }

        return null;
    }

    private static bool IsCssIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c is '-' or '_' || c >= 0x80;

    private static void SkipCssTrivia(string css, ref int i)
    {
        while (i < css.Length)
        {
            if (css[i] <= ' ' || char.IsWhiteSpace(css[i]))
            {
                i++;
                continue;
            }

            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close >= 0 ? close + 2 : css.Length;
                continue;
            }

            break;
        }
    }

    private static string ReadCssString(string css, ref int i)
    {
        var quote = css[i++];
        var sb = new StringBuilder();
        while (i < css.Length)
        {
            var c = css[i++];
            if (c == quote)
            {
                break;
            }

            if (c == '\\')
            {
                if (i >= css.Length)
                {
                    break;
                }

                if (css[i] is '\r' or '\n' or '\f')
                {
                    if (css[i] == '\r' && i + 1 < css.Length && css[i + 1] == '\n')
                    {
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }

                    continue;
                }

                sb.Append(ReadCssEscape(css, ref i));
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string ReadCssIdent(string css, ref int i)
    {
        var sb = new StringBuilder();
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '\\')
            {
                i++;
                if (i < css.Length)
                {
                    sb.Append(ReadCssEscape(css, ref i));
                }

                continue;
            }

            if (IsCssIdentChar(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            break;
        }

        return sb.ToString();
    }

    private static string ReadCssUnquotedUrl(string css, ref int i)
    {
        var sb = new StringBuilder();
        while (i < css.Length)
        {
            if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close >= 0 ? close + 2 : css.Length;
                continue;
            }

            var c = css[i];
            if (c == ')')
            {
                i++;
                break;
            }

            if (c <= ' ' || char.IsWhiteSpace(c))
            {
                SkipCssTrivia(css, ref i);
                if (i < css.Length && css[i] == ')')
                {
                    i++;
                }

                break;
            }

            if (c == '\\')
            {
                i++;
                if (i < css.Length)
                {
                    sb.Append(ReadCssEscape(css, ref i));
                }

                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string ReadCssEscape(string css, ref int i)
    {
        var hexStart = i;
        while (i < css.Length && i - hexStart < 6 && Uri.IsHexDigit(css[i]))
        {
            i++;
        }

        if (i > hexStart)
        {
            var span = css.AsSpan(hexStart, i - hexStart);
            if (i < css.Length && css[i] is ' ' or '\t' or '\n' or '\f' or '\r')
            {
                if (css[i] == '\r' && i + 1 < css.Length && css[i + 1] == '\n')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }
            }

            if (int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                && code is > 0 and <= 0x10FFFF && !(code is >= 0xD800 and <= 0xDFFF))
            {
                return char.ConvertFromUtf32(code);
            }

            return "\uFFFD";
        }

        return css[i++].ToString();
    }

    private static string UnescapeCssSpan(string raw)
    {
        if (!raw.Contains('\\', StringComparison.Ordinal))
        {
            return raw;
        }

        var sb = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                sb.Append(ReadCssEscape(raw, ref i));
            }
            else
            {
                sb.Append(raw[i++]);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex("""(?<what><!ENTITY\b[^>]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Entity();

    [GeneratedRegex("""(?<what><!DOCTYPE\b[^>\[]*\[[^\]]*\]?\s*>?)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DoctypeInternalSubset();

    [GeneratedRegex("""<!DOCTYPE\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DoctypeAny();

    [GeneratedRegex("""^<!DOCTYPE\s+svg(?:\s+(?:PUBLIC\s+(["'])-\/\/W3C\/\/DTD\s+SVG\b[^"']*\1|SYSTEM)\s+(["'])https?:\/\/www\.w3\.org\/(?:Graphics\/SVG\/|TR\/[^"']*\/DTD\/svg)[^"']*\.dtd\2)?\s*>$""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StandardSvgDoctype();

    [GeneratedRegex("""(?<=<\?xml\b[^?]*?)\bencoding\s*=\s*(["'])[^"']*\1""", RegexOptions.IgnoreCase)]
    private static partial Regex XmlDeclEncoding();

    [GeneratedRegex("""\bhref\s*=\s*(["'])(?<what>.*?)\1""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PiHref();
}
