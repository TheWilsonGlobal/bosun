using System.Text.RegularExpressions;

namespace Bosun.Application.Safety;

/// <summary>One instruction-like phrase found in document text.</summary>
public sealed record InjectionHit(string Pattern, string Match);

/// <summary>
/// Deterministic scanner for instruction-like phrases in DOCUMENT text (plan s7.6 / s6.3). It never decides anything on its own:
/// a hit only sets the suspected_injection flag, which lowers the confidence of the row and makes a human look at it. The model
/// is told that the document is data; this is the second, independent line of defence that does not depend on the model
/// behaving. Patterns are deliberately specific to instruction phrasing, so ordinary I/O-list text ("set point", "high alarm",
/// "ignore if redundant") does not trigger.
/// </summary>
public static class InjectionScanner
{
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("ignore_instructions", Make(@"\b(ignore|disregard|forget|override|bypass)\b[^.\n]{0,30}\b(previous|prior|above|earlier|preceding|all|any|your|these|the)\b[^.\n]{0,20}\b(instructions?|prompts?|rules?|directions?|guidelines?|messages?|constraints?)\b")),
        ("new_instructions", Make(@"\b(new|updated|revised|additional|real)\s+(instructions?|system\s+prompt|directive)s?\b")),
        ("role_override", Make(@"\b(you\s+are\s+now|from\s+now\s+on\s+you|act\s+as\s+(an?\s+)?(admin|root|system|assistant|developer)|pretend\s+(to\s+be|you\s+are)|your\s+(new\s+)?role\s+is)\b")),
        ("system_marker", Make(@"(\bsystem\s*(prompt|message|override|instruction)s?\b|^\s*system\s*:|</?\s*(system|assistant|user|instructions?)\s*>|\[/?(INST|SYS)\]|<<<|>>>|END_DOC_DATA|DOC_DATA)")),
        ("zero_alarms", Make(@"\b(set|change|make|put|reset)\b[^.\n]{0,25}\b(alarms?|limits?|thresholds?|setpoints?|delays?)\b[^.\n]{0,25}\b(to|=)\s*(0\b|zero|none|nothing|disabled|off)")),
        ("disable_alarms", Make(@"\b(disable|deactivate|turn\s+off|remove|delete|clear|wipe)\b[^.\n]{0,25}\b(all|every|each|the)\b[^.\n]{0,15}\b(alarms?|limits?|tags?|data|rows?|configuration)\b")),
        ("apply_or_approve", Make(@"\b(approve|apply|commit|execute|activate)\b[^.\n]{0,30}\b(change\s*sets?|changes?|draft|extraction|configuration|this\s+list|all\s+rows)\b|\bauto[- ]?(approve|apply|accept)\b")),
        ("skip_review", Make(@"\b(skip|without|bypass|no\s+need\s+for|do\s+not\s+(require|ask\s+for|wait\s+for))\b[^.\n]{0,20}\b(validation|review|approval|verification|checks?|human)\b")),
        ("suppress_flag", Make(@"\bdo\s+not\s+(flag|report|mention|warn|tell|reveal|show)\b|\b(don't|dont)\s+(flag|report|mention|warn|tell)\b|\bwithout\s+(flagging|telling|warning)\b")),
        ("add_rows", Make(@"\b(add|create|insert|append|include)\b[^.\n]{0,20}\b(a\s+|an\s+|the\s+)?(new\s+)?(extra\s+|additional\s+)?(tags?|rows?|entries|entry)\b[^.\n]{0,40}\b(named|called|with\s+name)\s+[A-Z0-9]")),
        ("output_override", Make(@"\b(respond|reply|answer|output|return)\s+(only\s+)?(with|in)\b[^.\n]{0,30}\b(json|yes|ok|approved|true)\b|\binstead\s+of\s+(extracting|the\s+table)\b")),
        ("model_address", Make(@"\b(dear|hey|attention|note\s+to)\s+(ai|llm|model|assistant|gemini|chatgpt|claude|language\s+model)\b|\bas\s+an?\s+(ai|llm|language\s+model)\b|\b(ai|llm|assistant)[, ]+(please|you\s+must|you\s+should)\b")),
    ];

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));

    /// <summary>Instruction-like phrases in <paramref name="text"/>; empty for ordinary document text.</summary>
    public static IReadOnlyList<InjectionHit> Scan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var hits = new List<InjectionHit>();
        foreach (var (name, pattern) in Patterns)
        {
            try
            {
                var match = pattern.Match(text);
                if (match.Success)
                {
                    hits.Add(new InjectionHit(name, Clip(match.Value.Trim())));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Hostile text built to stall the scanner is itself suspicious.
                hits.Add(new InjectionHit(name + "_timeout", Clip(text)));
            }
        }

        return hits;
    }

    public static bool IsSuspicious(string? text) => Scan(text).Count > 0;

    private static string Clip(string text) => text.Length <= 120 ? text : text[..119] + "…";
}
