using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sowser.Models;

namespace Sowser.Services
{
    public static class BookmarkHtmlIO
    {
        private static readonly Regex AnchorRx = new(
            @"<A\s+[^>]*HREF\s*=\s*""([^""]+)""[^>]*>([^<]*)</A>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<Bookmark> ImportNetscapeHtml(string path)
        {
            string html = File.ReadAllText(path);
            var list = new List<Bookmark>();
            foreach (Match m in AnchorRx.Matches(html))
            {
                string url = m.Groups[1].Value.Trim();
                string title = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                if (string.IsNullOrEmpty(url)) continue;
                list.Add(new Bookmark { Title = string.IsNullOrEmpty(title) ? url : title, Url = url });
            }
            return list;
        }

        public static void ExportNetscapeHtml(string path, IEnumerable<Bookmark> bookmarks, string title = "Sowser Bookmarks")
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
            sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
            sb.AppendLine($"<TITLE>{System.Net.WebUtility.HtmlEncode(title)}</TITLE>");
            sb.AppendLine("<H1>Bookmarks</H1><DL><p>");
            foreach (var b in bookmarks)
            {
                string u = System.Net.WebUtility.HtmlEncode(b.Url);
                string t = System.Net.WebUtility.HtmlEncode(string.IsNullOrEmpty(b.Title) ? b.Url : b.Title);
                sb.AppendLine($"    <DT><A HREF=\"{u}\">{t}</A>");
            }
            sb.AppendLine("</DL><p>");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }
}
