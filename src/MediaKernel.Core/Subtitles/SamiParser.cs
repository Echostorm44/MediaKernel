// MediaKernel - SAMI (Synchronized Accessible Media Interchange) Parser
// Microsoft subtitle format commonly used with Windows Media files (.smi, .sami)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaKernel.Core.Subtitles;

/// <summary>
/// Parser for SAMI (Synchronized Accessible Media Interchange) subtitle files.
/// SAMI is an HTML-like format with Start= timestamps in SYNC elements.
/// </summary>
public sealed partial class SamiParser
{
    // Regex patterns for parsing SAMI elements
    private static readonly Regex SyncStartRegex = CreateSyncStartRegex();
    private static readonly Regex ParagraphStartRegex = CreateParagraphStartRegex();
    private static readonly Regex ClassAttributeRegex = CreateClassAttributeRegex();
    private static readonly Regex HtmlTagRegex = CreateHtmlTagRegex();
    private static readonly Regex BrTagRegex = CreateBrTagRegex();
    private static readonly Regex WhitespaceRegex = CreateWhitespaceRegex();
    
    [GeneratedRegex(@"Start\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateSyncStartRegex();
    
    [GeneratedRegex(@"<P[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateParagraphStartRegex();
    
    [GeneratedRegex(@"Class\s*=\s*[""']?(\w+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateClassAttributeRegex();
    
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex CreateHtmlTagRegex();
    
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CreateBrTagRegex();
    
    [GeneratedRegex(@"[ \t]+", RegexOptions.Compiled)]
    private static partial Regex CreateWhitespaceRegex();
    
    /// <summary>
    /// Gets or sets the language class to filter. If null, all classes are included.
    /// </summary>
    public string? LanguageClass { get; set; }
    
    /// <summary>
    /// Parse a SAMI file from a stream.
    /// </summary>
    public List<SubtitleCue> Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ParseInternal(reader.ReadToEnd());
    }

    /// <summary>
    /// Parse SAMI content from a string.
    /// </summary>
    public List<SubtitleCue> Parse(string content)
    {
        return ParseInternal(content);
    }

    /// <summary>
    /// Parse SAMI content from a TextReader.
    /// </summary>
    public List<SubtitleCue> Parse(TextReader reader)
    {
        return ParseInternal(reader.ReadToEnd());
    }

    private List<SubtitleCue> ParseInternal(string content)
    {
        var cues = new List<SubtitleCue>();
        var entries = new List<SamiEntry>();
        
        // Find all SYNC elements
        int position = 0;
        while (position < content.Length)
        {
            // Find next <SYNC Start=
            int syncStart = content.IndexOf("<SYNC", position, StringComparison.OrdinalIgnoreCase);
            if (syncStart < 0)
                break;
            
            // Find the end of the SYNC tag
            int syncTagEnd = content.IndexOf('>', syncStart);
            if (syncTagEnd < 0)
                break;
            
            string syncTag = content.Substring(syncStart, syncTagEnd - syncStart + 1);
            
            // Extract Start= value
            var startMatch = SyncStartRegex.Match(syncTag);
            if (!startMatch.Success)
            {
                position = syncTagEnd + 1;
                continue;
            }
            
            long startMs = long.Parse(startMatch.Groups[1].Value);
            
            // Find next SYNC or end of body
            int nextSync = content.IndexOf("<SYNC", syncTagEnd + 1, StringComparison.OrdinalIgnoreCase);
            int bodyEnd = content.IndexOf("</BODY>", syncTagEnd + 1, StringComparison.OrdinalIgnoreCase);
            
            int textEnd;
            if (nextSync >= 0 && (bodyEnd < 0 || nextSync < bodyEnd))
                textEnd = nextSync;
            else if (bodyEnd >= 0)
                textEnd = bodyEnd;
            else
                textEnd = content.Length;
            
            // Extract text between SYNC tags
            string textBlock = content.Substring(syncTagEnd + 1, textEnd - syncTagEnd - 1);
            
            // Parse paragraphs in the text block
            var paragraphs = ParseParagraphs(textBlock);
            
            foreach (var paragraph in paragraphs)
            {
                if (LanguageClass == null || 
                    paragraph.LanguageClass == null || 
                    paragraph.LanguageClass.Equals(LanguageClass, StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(new SamiEntry(startMs, paragraph.Text, paragraph.LanguageClass));
                }
            }
            
            position = syncTagEnd + 1;
        }
        
        // Convert entries to cues with proper end times
        // Each cue ends when the next one starts, or has no end time for the last entry
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string text = CleanText(entry.Text);
            
            // Skip empty entries (used to clear subtitles)
            if (string.IsNullOrWhiteSpace(text))
                continue;
            
            // Find the end time (next entry with same language class, or next entry if filtering)
            long endMs = -1;
            for (int j = i + 1; j < entries.Count; j++)
            {
                if (LanguageClass == null || 
                    entries[j].LanguageClass == null ||
                    entries[j].LanguageClass.Equals(entry.LanguageClass, StringComparison.OrdinalIgnoreCase))
                {
                    endMs = entries[j].StartMs;
                    break;
                }
            }
            
            // Convert to microseconds
            long startUs = entry.StartMs * 1000;
            long endUs = endMs >= 0 ? endMs * 1000 : startUs + 5_000_000; // Default 5 second duration
            
            cues.Add(new SubtitleCue(
                startUs,
                endUs,
                text,
                SubtitleType.Text,
                null,
                SubtitlePosition.Default,
                null));
        }
        
        return cues;
    }
    
    private List<SamiParagraph> ParseParagraphs(string textBlock)
    {
        var paragraphs = new List<SamiParagraph>();
        
        // Find all <P> elements
        var matches = ParagraphStartRegex.Matches(textBlock);
        
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            int tagEnd = match.Index + match.Length;
            
            // Find end of this paragraph (next <P> or end of block)
            int nextP = i + 1 < matches.Count ? matches[i + 1].Index : textBlock.Length;
            
            string pContent = textBlock.Substring(tagEnd, nextP - tagEnd);
            
            // Extract language class from the <P> tag
            string? langClass = null;
            var classMatch = ClassAttributeRegex.Match(match.Value);
            if (classMatch.Success)
                langClass = classMatch.Groups[1].Value;
            
            paragraphs.Add(new SamiParagraph(pContent, langClass));
        }
        
        // If no <P> tags found, treat entire block as text
        if (paragraphs.Count == 0 && !string.IsNullOrWhiteSpace(textBlock))
        {
            paragraphs.Add(new SamiParagraph(textBlock, null));
        }
        
        return paragraphs;
    }
    
    private static string CleanText(string text)
    {
        // Replace <br> tags with newlines first
        text = BrTagRegex.Replace(text, "\n");
        
        // Remove HTML tags BEFORE decoding entities
        // (so &lt;Test&gt; doesn't become <Test> which then gets stripped)
        text = HtmlTagRegex.Replace(text, "");
        
        // Now decode HTML entities (after tag removal)
        text = System.Net.WebUtility.HtmlDecode(text);
        
        // Handle nbsp markers that survived decoding
        text = text.Replace('\u00A0', ' ');
        
        // Normalize whitespace
        text = WhitespaceRegex.Replace(text, " ");
        
        // Trim
        text = text.Trim();
        
        return text;
    }
    
    /// <summary>
    /// Check if content is likely a SAMI file.
    /// </summary>
    public static bool CanParse(ReadOnlySpan<char> content)
    {
        // Look for <SAMI> tag (case-insensitive)
        return content.Contains("<SAMI", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("<SYNC", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Check if a file has a SAMI extension.
    /// </summary>
    public static bool IsSamiExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".smi", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".sami", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Extract available language classes from SAMI content.
    /// </summary>
    public static List<string> ExtractLanguageClasses(string content)
    {
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        var matches = CreateClassAttributeRegex().Matches(content);
        foreach (Match match in matches)
        {
            if (match.Success)
                classes.Add(match.Groups[1].Value);
        }
        
        return new List<string>(classes);
    }
    
    private readonly record struct SamiEntry(long StartMs, string Text, string? LanguageClass);
    private readonly record struct SamiParagraph(string Text, string? LanguageClass);
}
