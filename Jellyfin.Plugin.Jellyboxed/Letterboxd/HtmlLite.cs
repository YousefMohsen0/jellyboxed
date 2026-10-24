using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Jellyfin.Plugin.Jellyboxed.Letterboxd;

/// <summary>A node in the tiny, dependency-free HTML tree used to read Letterboxd pages.</summary>
public sealed class HtmlNode
{
    private static readonly char[] ClassSeparators = { ' ', '\t', '\r', '\n', '\f' };

    public HtmlNode(string tag)
    {
        Tag = tag;
    }

    /// <summary>Gets the lower-case tag name, "#document", or "#text".</summary>
    public string Tag { get; }

    public string Text { get; set; } = string.Empty;

    public HtmlNode? Parent { get; set; }

    public List<HtmlNode> Children { get; } = new();

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) ? v : null;

    public bool HasClass(string cls)
    {
        var value = Attr("class");
        return value is not null && value.Split(ClassSeparators, StringSplitOptions.RemoveEmptyEntries).Contains(cls, StringComparer.Ordinal);
    }

    /// <summary>Gets all class names of the element.</summary>
    public IEnumerable<string> Classes =>
        (Attr("class") ?? string.Empty).Split(ClassSeparators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Gets the concatenated text of this node and its descendants.</summary>
    public string InnerText
    {
        get
        {
            if (Tag == "#text")
            {
                return Text;
            }

            var sb = new StringBuilder();
            Append(this, sb);
            return sb.ToString();
        }
    }

    public IEnumerable<HtmlNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    /// <summary>Removes this node from its parent.</summary>
    public void Remove() => Parent?.Children.Remove(this);

    public HtmlNode? SelectOne(string selector) => Select(selector).FirstOrDefault();

    /// <summary>
    /// Finds descendants matching a small CSS subset: <c>tag</c>, <c>.class</c>, <c>#id</c>, <c>[attr]</c>,
    /// <c>[attr=value]</c>, compounds of those, the descendant combinator (space), <c>&gt;</c> and comma lists.
    /// </summary>
    public IEnumerable<HtmlNode> Select(string selector)
    {
        var groups = selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Selector.Parse)
            .ToList();

        foreach (var node in Descendants())
        {
            if (node.Tag == "#text")
            {
                continue;
            }

            if (groups.Any(g => g.Matches(node, this)))
            {
                yield return node;
            }
        }
    }

    private static void Append(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.Children)
        {
            if (child.Tag == "#text")
            {
                sb.Append(child.Text);
            }
            else
            {
                Append(child, sb);
            }
        }
    }

    private sealed class Selector
    {
        private readonly List<(Compound Compound, bool DirectChild)> _parts = new();

        public static Selector Parse(string text)
        {
            var sel = new Selector();
            var directChild = false;
            foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (raw == ">")
                {
                    directChild = true;
                    continue;
                }

                sel._parts.Add((Compound.Parse(raw), directChild));
                directChild = false;
            }

            return sel;
        }

        public bool Matches(HtmlNode node, HtmlNode scope)
        {
            if (_parts.Count == 0)
            {
                return false;
            }

            return MatchFrom(node, _parts.Count - 1, scope);
        }

        private bool MatchFrom(HtmlNode node, int index, HtmlNode scope)
        {
            if (!_parts[index].Compound.Matches(node))
            {
                return false;
            }

            if (index == 0)
            {
                return true;
            }

            var direct = _parts[index].DirectChild;
            var ancestor = node.Parent;
            while (ancestor is not null && ancestor != scope.Parent)
            {
                if (ancestor.Tag != "#document" && MatchFrom(ancestor, index - 1, scope))
                {
                    return true;
                }

                if (direct)
                {
                    return false;
                }

                ancestor = ancestor.Parent;
            }

            return false;
        }
    }

    private sealed class Compound
    {
        private string? _tag;
        private string? _id;
        private readonly List<string> _classes = new();
        private readonly List<(string Name, string? Value)> _attrs = new();

        public static Compound Parse(string text)
        {
            var c = new Compound();
            var i = 0;
            var start = i;
            while (i < text.Length && text[i] != '.' && text[i] != '#' && text[i] != '[')
            {
                i++;
            }

            if (i > start)
            {
                var tag = text[start..i];
                c._tag = tag == "*" ? null : tag.ToLowerInvariant();
            }

            while (i < text.Length)
            {
                var kind = text[i++];
                if (kind == '[')
                {
                    var end = text.IndexOf(']', i);
                    if (end < 0)
                    {
                        end = text.Length;
                    }

                    var inner = text[i..end];
                    i = Math.Min(end + 1, text.Length);
                    var eq = inner.IndexOf('=', StringComparison.Ordinal);
                    if (eq < 0)
                    {
                        c._attrs.Add((inner.Trim(), null));
                    }
                    else
                    {
                        c._attrs.Add((inner[..eq].Trim(), inner[(eq + 1)..].Trim().Trim('"', '\'')));
                    }
                }
                else
                {
                    var s = i;
                    while (i < text.Length && text[i] != '.' && text[i] != '#' && text[i] != '[')
                    {
                        i++;
                    }

                    var name = text[s..i];
                    if (kind == '.')
                    {
                        c._classes.Add(name);
                    }
                    else
                    {
                        c._id = name;
                    }
                }
            }

            return c;
        }

        public bool Matches(HtmlNode node)
        {
            if (_tag is not null && !string.Equals(node.Tag, _tag, StringComparison.Ordinal))
            {
                return false;
            }

            if (_id is not null && !string.Equals(node.Attr("id"), _id, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var cls in _classes)
            {
                if (!node.HasClass(cls))
                {
                    return false;
                }
            }

            foreach (var (name, value) in _attrs)
            {
                var actual = node.Attr(name);
                if (actual is null || (value is not null && !string.Equals(actual, value, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>A forgiving HTML parser: good enough for scraping, never throws on bad markup.</summary>
public static class HtmlParser
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextTags = new(StringComparer.Ordinal) { "script", "style", "textarea", "title" };

    public static HtmlNode Parse(string html)
    {
        var root = new HtmlNode("#document");
        var current = root;
        var i = 0;
        var n = html.Length;

        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AddText(current, html[i..]);
                break;
            }

            if (lt > i)
            {
                AddText(current, html[i..lt]);
            }

            i = lt;

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            if (i + 1 < n && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                var end = html.IndexOf('>', i);
                i = end < 0 ? n : end + 1;
                continue;
            }

            if (i + 1 < n && html[i + 1] == '/')
            {
                var end = html.IndexOf('>', i);
                var close = end < 0 ? html[(i + 2)..] : html[(i + 2)..end];
                i = end < 0 ? n : end + 1;
                var name = close.Trim().ToLowerInvariant();
                var node = current;
                while (node is not null && node.Tag != name && node.Tag != "#document")
                {
                    node = node.Parent;
                }

                if (node is not null && node.Tag == name && node.Parent is not null)
                {
                    current = node.Parent;
                }

                continue;
            }

            if (i + 1 >= n || !char.IsLetter(html[i + 1]))
            {
                AddText(current, "<");
                i++;
                continue;
            }

            // Opening tag.
            var j = i + 1;
            while (j < n && !char.IsWhiteSpace(html[j]) && html[j] != '>' && html[j] != '/')
            {
                j++;
            }

            var tag = html[(i + 1)..j].ToLowerInvariant();
            var element = new HtmlNode(tag) { Parent = current };
            var selfClosing = false;

            while (j < n)
            {
                while (j < n && char.IsWhiteSpace(html[j]))
                {
                    j++;
                }

                if (j >= n)
                {
                    break;
                }

                if (html[j] == '>')
                {
                    j++;
                    break;
                }

                if (html[j] == '/')
                {
                    selfClosing = true;
                    j++;
                    continue;
                }

                var ns = j;
                while (j < n && !char.IsWhiteSpace(html[j]) && html[j] != '=' && html[j] != '>' && html[j] != '/')
                {
                    j++;
                }

                var attrName = html[ns..j].ToLowerInvariant();
                var value = string.Empty;
                while (j < n && char.IsWhiteSpace(html[j]))
                {
                    j++;
                }

                if (j < n && html[j] == '=')
                {
                    j++;
                    while (j < n && char.IsWhiteSpace(html[j]))
                    {
                        j++;
                    }

                    if (j < n && (html[j] == '"' || html[j] == '\''))
                    {
                        var quote = html[j++];
                        var qe = html.IndexOf(quote, j);
                        if (qe < 0)
                        {
                            qe = n;
                        }

                        value = html[j..qe];
                        j = Math.Min(qe + 1, n);
                    }
                    else
                    {
                        var vs = j;
                        while (j < n && !char.IsWhiteSpace(html[j]) && html[j] != '>')
                        {
                            j++;
                        }

                        value = html[vs..j];
                    }
                }

                if (attrName.Length > 0 && !element.Attributes.ContainsKey(attrName))
                {
                    element.Attributes[attrName] = WebUtility.HtmlDecode(value);
                }
            }

            current.Children.Add(element);
            i = j;

            if (RawTextTags.Contains(tag) && !selfClosing)
            {
                var closeIdx = html.IndexOf("</" + tag, i, StringComparison.OrdinalIgnoreCase);
                var rawEnd = closeIdx < 0 ? n : closeIdx;
                if (rawEnd > i)
                {
                    element.Children.Add(new HtmlNode("#text") { Text = html[i..rawEnd], Parent = element });
                }

                var gt = closeIdx < 0 ? -1 : html.IndexOf('>', closeIdx);
                i = gt < 0 ? n : gt + 1;
                continue;
            }

            if (!selfClosing && !VoidTags.Contains(tag))
            {
                current = element;
            }
        }

        return root;
    }

    private static void AddText(HtmlNode parent, string raw)
    {
        if (raw.Length == 0)
        {
            return;
        }

        parent.Children.Add(new HtmlNode("#text") { Text = WebUtility.HtmlDecode(raw), Parent = parent });
    }
}
