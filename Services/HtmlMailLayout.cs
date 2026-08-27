using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HtmlAgilityPack;

namespace MailPulse.Services
{
    // Localizes an HTML document with an XLIFF-style model: the LLM only ever sees plain text
    // interleaved with opaque inline placeholders (⟦N⟧...⟦/N⟧). One unit is an inline/text
    // run between block boundaries, including runs around nested blocks. Every inline
    // element becomes a placeholder and the whole block is sent as a single template string,
    // so the model has full sentence context (no fragmented per-text-node requests).
    // On completion the returned template is parsed, validated (placeholders complete,
    // well-nested, in order) and mapped 1:1 back onto the fragments; markup never travels.
    public sealed class HtmlMailLayout
    {
        private const string Open = "\u27E6";   // ⟦
        private const string Close = "\u27E7";  // ⟧

        private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "p","div","li","ul","ol","table","tbody","thead","tfoot","tr","td","th","caption",
            "h1","h2","h3","h4","h5","h6","pre","blockquote","section","article","header",
            "footer","nav","aside","figure","figcaption","dl","dt","dd","address","form",
            "fieldset","main","details","summary"
        };

        // Subtrees that must never be sent to the LLM: document scaffolding and CSS/script.
        private static readonly HashSet<string> SkipTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "head","style","script","title","meta","link","noscript","template"
        };

        // Attribute values that carry user-visible text and are safe to translate.
        private static readonly string[] TranslatableAttrs =
            { "alt", "title", "placeholder", "aria-label", "aria-description" };

        internal sealed class AttributeUnit
        {
            internal readonly HtmlNode Element;
            internal readonly string Name;
            internal readonly string Value;
            internal string Translated;
            internal AttributeUnit(HtmlNode element, string name, string value)
            { Element = element; Name = name; Value = value; }
        }

        internal sealed class Fragment
        {
            internal readonly List<HtmlTextNode> Nodes;
            internal readonly string Source;
            internal string Translation;
            internal HtmlNode Span;
            internal Fragment(List<HtmlTextNode> nodes, string source) { Nodes = nodes; Source = source; }
        }

        internal sealed class Unit
        {
            internal readonly HtmlNode Block;
            internal readonly List<HtmlNode> Roots;
            internal readonly List<Fragment> Fragments = new List<Fragment>();
            internal readonly List<HtmlNode> Placeholders = new List<HtmlNode>();
            internal string Template;
            internal string Translation;
            internal bool Done => Translation != null;
            internal Unit(HtmlNode block, IEnumerable<HtmlNode> roots)
            { Block = block; Roots = roots.ToList(); }
        }

        internal readonly HtmlDocument Document;
        internal readonly List<Unit> Units = new List<Unit>();
        internal readonly List<AttributeUnit> Attributes = new List<AttributeUnit>();
        public readonly List<string> Texts = new List<string>();
        public readonly int TotalUnits;
        internal bool AttributesDone;
        public string TranslatedSubject { get; set; }
        private bool _spansWritten;

        private HtmlMailLayout(HtmlDocument document, List<Unit> units, List<string> texts)
        {
            Document = document;
            Units = units;
            Texts = texts;
            TotalUnits = units.Count;
        }

        public int CompletedUnits { get { lock (this) return Units.Count(u => u.Done); } }
        public bool HasAttributes => Attributes.Count > 0;
        public int AttributeCount => Attributes.Count;
        // Text units plus one batched attribute job.
        public int TotalJobs => Units.Count + (HasAttributes ? 1 : 0);
        public int CompletedJobs { get { lock (this) return Units.Count(u => u.Done) + (AttributesDone ? 1 : 0); } }

        public static HtmlMailLayout Parse(string bodyHtml)
        {
            if (string.IsNullOrWhiteSpace(bodyHtml))
                throw new InvalidOperationException("这封邮件没有可翻译的 HTML 正文。");
            var document = new HtmlDocument { OptionFixNestedTags = true, OptionOutputAsXml = false };
            document.LoadHtml(bodyHtml);
            var units = new List<Unit>();
            var texts = new List<string>();
            CollectUnits(document.DocumentNode, units, texts);
            var layout = new HtmlMailLayout(document, units, texts);
            CollectAttributes(document, layout);
            if (layout.TotalJobs == 0)
                throw new InvalidOperationException("这封邮件没有可翻译的文本内容。");
            layout.EnsureSpans();
            return layout;
        }

        // Split at block boundaries, but retain the inline/text runs before and after them.
        // Roots are references to the original nodes: no wrapper/reparenting changes layout.
        private static void CollectUnits(HtmlNode container, List<Unit> units, List<string> texts)
        {
            if (IsNoTranslateOrSkipped(container)) return;
            var roots = new List<HtmlNode>();
            foreach (var child in container.ChildNodes)
            {
                bool boundary = child.NodeType == HtmlNodeType.Element &&
                    (BlockTags.Contains(child.Name) || child.Descendants().Any(n =>
                        n.NodeType == HtmlNodeType.Element && BlockTags.Contains(n.Name)));
                if (boundary)
                {
                    AddUnit(container, roots, units, texts);
                    roots.Clear();
                    CollectUnits(child, units, texts);
                }
                else roots.Add(child);
            }
            AddUnit(container, roots, units, texts);
        }

        private static void AddUnit(HtmlNode container, List<HtmlNode> roots, List<Unit> units, List<string> texts)
        {
            if (roots.Count == 0) return;
            var unit = new Unit(container, roots);
            var sb = new StringBuilder();
            BuildTemplate(unit.Roots, unit, sb);
            if (!unit.Fragments.Any(f => RegexHasText(f.Source))) return;
            unit.Template = sb.ToString();
            units.Add(unit);
            texts.Add(unit.Template);
        }

        // Alt/title/placeholder/aria-* attribute values are user-visible text and are translated
        // as units too (batched by the service), unless the element is marked translate="no".
        private static void CollectAttributes(HtmlDocument document, HtmlMailLayout layout)
        {
            foreach (var element in AllElementsInDocumentOrder(document.DocumentNode))
            {
                if (element.NodeType != HtmlNodeType.Element) continue;
                if (IsNoTranslateOrSkipped(element)) continue;
                foreach (string name in TranslatableAttrs)
                {
                    if (!element.Attributes.Contains(name)) continue;
                    string value = element.GetAttributeValue(name, "");
                    string clean = CleanAttr(value);
                    if (!RegexHasText(clean)) continue;
                    layout.Attributes.Add(new AttributeUnit(element, name, clean));
                }
            }
        }

        private static bool IsNoTranslateOrSkipped(HtmlNode element)
        {
            var node = element;
            while (node != null)
            {
                if (node.NodeType != HtmlNodeType.Element) { node = node.ParentNode; continue; }
                if (SkipTags.Contains(node.Name)) return true;
                if (string.Equals(node.GetAttributeValue("translate", ""), "no", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (node.Attributes.Contains("data-no-translate")) return true;
                node = node.ParentNode;
            }
            return false;
        }

        private static string CleanAttr(string value)
        {
            string raw = System.Net.WebUtility.HtmlDecode(value ?? "");
            return System.Text.RegularExpressions.Regex.Replace(raw, "[\\s\\u00A0\\u2000-\\u200A\\u202F\\u205F]+", " ").Trim();
        }

        // Builds the block's template: text runs become fragments, every inline element becomes
        // a placeholder (⟦id⟧...⟦/id⟧) and its own content is recursed, so nested markup yields
        // nested placeholders. Placeholder ids are assigned in document (DFS) order.
        private static void BuildTemplate(IEnumerable<HtmlNode> children, Unit unit, StringBuilder sb)
        {
            var run = new List<HtmlTextNode>();
            foreach (var child in children)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    run.Add((HtmlTextNode)child);
                }
                else if (child.NodeType == HtmlNodeType.Element)
                {
                    FlushRun(run, unit, sb);
                    if (IsNoTranslateOrSkipped(child)) continue;
                    int id = unit.Placeholders.Count;
                    unit.Placeholders.Add(child);
                    sb.Append(Open).Append(id).Append(Close);
                    BuildTemplate(child.ChildNodes, unit, sb);
                    sb.Append(Open).Append('/').Append(id).Append(Close);
                }
            }
            FlushRun(run, unit, sb);
        }

        private static void FlushRun(List<HtmlTextNode> run, Unit unit, StringBuilder sb)
        {
            if (run.Count == 0) return;
            string source = CleanText(run);
            if (source.Length == 0) { run.Clear(); return; }
            // Copy: the shared run list is cleared below, and each fragment must keep its own nodes.
            unit.Fragments.Add(new Fragment(new List<HtmlTextNode>(run), source));
            sb.Append(source);
            run.Clear();
        }

        // HtmlAgilityPack keeps named entities (&nbsp; &amp; &lt; ...) as literal text in text
        // nodes. Decode them so the LLM sees real characters; whitespace (incl. NBSP) collapses
        // to a single regular space. Original spacing survives where it is structural.
        internal static string CleanText(List<HtmlTextNode> nodes)
        {
            var builder = new StringBuilder();
            foreach (var node in nodes) builder.Append(node.Text);
            string raw = System.Net.WebUtility.HtmlDecode(builder.ToString());
            return System.Text.RegularExpressions.Regex.Replace(raw, "[\\s\\u00A0\\u2000-\\u200A\\u202F\\u205F]+", " ");
        }

        private static bool RegexHasText(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                System.Text.RegularExpressions.Regex.IsMatch(text, "[\\p{L}\\p{N}]");
        }

        private static IEnumerable<HtmlNode> AllElementsInDocumentOrder(HtmlNode root)
        {
            var stack = new Stack<HtmlNode>();
            var children = root.ChildNodes.ToArray();
            for (int i = children.Length - 1; i >= 0; i--) stack.Push(children[i]);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.NodeType != HtmlNodeType.Element) continue;
                yield return node;
                var kids = node.ChildNodes.ToArray();
                for (int i = kids.Length - 1; i >= 0; i--) stack.Push(kids[i]);
            }
        }

        // Wraps every fragment in a marker span once so Build() snapshots and the UI's mpApply
        // in-place patching address the same nodes.
        private void EnsureSpans()
        {
            if (_spansWritten) return;
            _spansWritten = true;
            for (int i = 0; i < Units.Count; i++)
            {
                var unit = Units[i];
                for (int k = 0; k < unit.Fragments.Count; k++)
                {
                    var fragment = unit.Fragments[k];
                    var span = HtmlNode.CreateNode("<span data-mp=\"" + i + "\" data-frag=\"" + k + "\"></span>");
                    span.InnerHtml = System.Net.WebUtility.HtmlEncode(fragment.Source);
                    var first = fragment.Nodes[0];
                    first.ParentNode.InsertBefore(span, first);
                    int rootIndex = unit.Roots.IndexOf(first);
                    if (rootIndex >= 0)
                    {
                        foreach (var node in fragment.Nodes) unit.Roots.Remove(node);
                        unit.Roots.Insert(rootIndex, span);
                    }
                    foreach (var node in fragment.Nodes) node.Remove();
                    fragment.Span = span;
                }
            }
            for (int i = 0; i < Attributes.Count; i++)
                Attributes[i].Element.SetAttributeValue("data-mp-attr-" + i, "");
        }

        internal string Build()
        {
            lock (this)
            {
                EnsureSpans();
                for (int i = 0; i < Units.Count; i++)
                    foreach (var fragment in Units[i].Fragments)
                        if (fragment.Translation != null)
                            fragment.Span.InnerHtml = System.Net.WebUtility.HtmlEncode(fragment.Translation);
                foreach (var attribute in Attributes)
                    if (attribute.Translated != null)
                        attribute.Element.SetAttributeValue(attribute.Name, System.Net.WebUtility.HtmlEncode(attribute.Translated));
                return Document.DocumentNode.OuterHtml;
            }
        }

        // Applies a translated template to a unit. Throws when placeholders are missing,
        // reordered, duplicated or improperly nested so the caller can degrade gracefully.
        internal void ApplyTranslation(Unit unit, string translated)
        {
            var tree = ParseTemplate(translated, unit.Placeholders.Count);
            int fragIndex = 0, phIndex = 0;
            Walk(unit.Roots, tree, unit, ref fragIndex, ref phIndex);
            if (fragIndex != unit.Fragments.Count || phIndex != unit.Placeholders.Count)
                throw new InvalidOperationException("占位符结构不完整。");
            unit.Translation = translated;
        }

        // Degradation path: strip all placeholders and pour the whole text into the first
        // fragment, emptying the rest. Never throws.
        internal void Fallback(Unit unit, string translated)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(translated ?? "",
                Open + "/?\\d+" + Close, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) text = unit.Fragments[0].Source;
            for (int k = 0; k < unit.Fragments.Count; k++)
                unit.Fragments[k].Translation = k == 0 ? text : "";
            unit.Translation = text;
        }

        private sealed class TPart
        {
            internal string Text;
            internal int? Id;
            internal TNode Inner;
        }
        private sealed class TNode
        {
            internal readonly List<TPart> Parts = new List<TPart>();
        }

        private static TNode ParseTemplate(string translated, int expectedPlaceholders)
        {
            var root = new TNode();
            var stack = new Stack<TNode>();
            stack.Push(root);
            var seen = new HashSet<int>();
            int pos = 0;
            string text = translated ?? "";
            while (pos < text.Length)
            {
                int openIdx = text.IndexOf(Open, pos, StringComparison.Ordinal);
                if (openIdx < 0)
                {
                    if (pos < text.Length) stack.Peek().Parts.Add(new TPart { Text = text.Substring(pos) });
                    break;
                }
                if (openIdx > pos) stack.Peek().Parts.Add(new TPart { Text = text.Substring(pos, openIdx - pos) });
                int closeIdx = text.IndexOf(Close, openIdx, StringComparison.Ordinal);
                if (closeIdx < 0) throw new InvalidOperationException("占位符缺少闭合标记。");
                string token = text.Substring(openIdx + Open.Length, closeIdx - openIdx - Open.Length);
                if (token.Length > 0 && token[0] == '/')
                {
                    int id = int.Parse(token.Substring(1));
                    if (!seen.Contains(id)) throw new InvalidOperationException("占位符缺失或顺序错乱。");
                    if (stack.Count <= 1) throw new InvalidOperationException("占位符嵌套不合法。");
                    seen.Remove(id);
                    stack.Pop();
                }
                else
                {
                    int id = int.Parse(token);
                    if (id < 0 || id >= expectedPlaceholders) throw new InvalidOperationException("占位符编号非法。");
                    if (seen.Contains(id)) throw new InvalidOperationException("占位符重复。");
                    seen.Add(id);
                    var node = new TNode();
                    stack.Peek().Parts.Add(new TPart { Id = id, Inner = node });
                    stack.Push(node);
                }
                pos = closeIdx + Close.Length;
            }
            if (stack.Count != 1 || seen.Count != 0)
                throw new InvalidOperationException("占位符结构不完整。");
            return root;
        }

        private static void Walk(IEnumerable<HtmlNode> children, TNode tnode, Unit unit, ref int fragIndex, ref int phIndex)
        {
            int pi = 0;
            foreach (var child in children)
            {
                if (child.NodeType != HtmlNodeType.Element) continue;
                if (IsNoTranslateOrSkipped(child)) continue;
                if (child.Attributes.Contains("data-frag"))
                {
                    // Fragment marker span: consumes the next plain-text part.
                    if (pi >= tnode.Parts.Count || tnode.Parts[pi].Id != null)
                        throw new InvalidOperationException("占位符顺序错乱。");
                    unit.Fragments[fragIndex].Translation = tnode.Parts[pi].Text;
                    fragIndex++;
                    pi++;
                }
                else
                {
                    // Inline placeholder element: consumes the next placeholder part and recurses.
                    int id = phIndex;
                    phIndex++;
                    if (pi >= tnode.Parts.Count || tnode.Parts[pi].Id != id)
                        throw new InvalidOperationException("占位符顺序错乱。");
                    var inner = tnode.Parts[pi].Inner;
                    pi++;
                    Walk(child.ChildNodes, inner, unit, ref fragIndex, ref phIndex);
                }
            }
            if (pi != tnode.Parts.Count)
                throw new InvalidOperationException("占位符结构不完整。");
        }
    }
}
