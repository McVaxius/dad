using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Resources;
using System.Text.RegularExpressions;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using dad.Models;

namespace dad.Windows;

internal sealed class UiText : IDisposable
{
    [ThreadStatic] private static UiText? current;
    internal static UiText Current => current ?? throw new InvalidOperationException("Enter the DAD UI frame before drawing.");
    internal static readonly (string Code,string Name)[] Languages=[("en","English"),("de","Deutsch"),("fr","Français"),
        ("es","Español"),("it","Italiano"),("ru","Русский"),("ja","日本語"),("ko","한국어"),("zh-Hans","简体中文"),
        ("vi","Tiếng Việt"),("pt-BR","Português (Brasil)"),("id","Bahasa Indonesia"),("pl","Polski"),("tr","Türkçe"),("hi","हिन्दी")];
    internal static IEnumerable<string> CjkLanguages(string selected) => new[]{"ja","ko","zh-Hans"}.OrderBy(code=>code==selected?0:1);
    private readonly ResourceManager manager;
    private readonly ResourceManager englishManager;
    private readonly Dictionary<string,string> labels;
    private readonly string[] concatenatedPrefixes;
    internal ResourceSet Resources { get; }
    internal IReadOnlyList<string> RequiredText { get; }
    internal CultureInfo Culture { get; }
    internal string Language { get; }
    private readonly Func<UiFontRole,IDisposable> pushFont;
    private readonly (Lazy<Regex> Pattern, string PatternText, string Key, string Prefix, int ArgumentCount)[] messageTemplates;
    private readonly (Lazy<Regex> Pattern, string Key, string Prefix, string RequiredLiteral, int ArgumentCount)[] levelingTemplates;
    internal UiText(string language, Func<UiFontRole,IDisposable> pushFont)
    {
        Language=Languages.Any(l=>l.Code==language)?language:"en";
        Culture=CultureInfo.GetCultureInfo(Language);
        manager=new ResourceManager("dad.Localization.Strings_"+Language.Replace('-','_'),typeof(UiText).Assembly);
        Resources=manager.GetResourceSet(CultureInfo.InvariantCulture,true,false) ?? throw new MissingManifestResourceException(Language);
        englishManager=new ResourceManager("dad.Localization.Strings_en",typeof(UiText).Assembly);
        var english=englishManager.GetResourceSet(CultureInfo.InvariantCulture,true,false) ?? throw new MissingManifestResourceException("en");
        RequiredText=Values(Resources).Concat(Values(english)).Concat(Languages.Where(l => l.Code != "hi").Select(l=>l.Name)).Append("\u2661").Distinct().ToArray();
        labels=english.Cast<DictionaryEntry>().ToDictionary(entry=>(string)entry.Value!,entry=>(string)entry.Key,StringComparer.Ordinal);
        if (labels.Count!=Resources.Cast<DictionaryEntry>().Count() || labels.Values.Any(key=>string.IsNullOrEmpty(Resources.GetString(key,false))))
            throw new MissingManifestResourceException("Incomplete DAD UI translations for "+Language);
        concatenatedPrefixes=labels.Keys.Where(key=>key.EndsWith(' ') || key.EndsWith('.')).OrderByDescending(key=>key.Length).ToArray();
        this.pushFont=pushFont;
        // Service messages remain English in logs; only their UI copies are localized.
        var parameter = new Regex(@"\{(\d+)(?::([^}]+))?\}");
        messageTemplates = labels.Keys.Where(key => parameter.IsMatch(key) && key != "{0} {1}: {2}")
            .OrderBy(key => key.StartsWith('{')).ThenByDescending(key=>key.Length).Select(key =>
            {
                var pattern = "^";
                var offset = 0;
                var holes = parameter.Matches(key);
                foreach (Match hole in holes)
                {
                    pattern += Regex.Escape(key[offset..hole.Index]) + $"(?<arg{hole.Groups[1].Value}>.*?)";
                    offset = hole.Index + hole.Length;
                }
                pattern += Regex.Escape(key[offset..]) + "$";
                // Construct and retain a matcher only when its existing prefix gate is reached.
                return (new Lazy<Regex>(() => new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(20))), pattern, key, key[..holes[0].Index],
                    holes.Cast<Match>().Max(hole => int.Parse(hole.Groups[1].Value, CultureInfo.InvariantCulture)) + 1);
            }).ToArray();
        // Only the compiler/scheduler display path can interpret single-space
        // aggregates. The raw character envelope must never be a global matcher.
        levelingTemplates = messageTemplates.Where(template =>
            int.TryParse(labels[template.Key].Replace("text_", ""), out var index) &&
            index is >= 2701 and <= 2736 && index != 2721)
            .Select(template => (new Lazy<Regex>(() => new Regex((template.Key.StartsWith("{0}", StringComparison.Ordinal)
                ? template.PatternText[..^1].Replace("(?<arg0>.*?)", @"(?<arg0>\S+?)")
                : template.PatternText[..^1]) + "(?=$| )",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20))), template.Key, template.Prefix,
                parameter.Replace(template.Key, "\0").Split('\0').OrderByDescending(part => part.Length).First(), template.ArgumentCount))
            .Concat(labels.Where(entry => entry.Key.EndsWith('.') && !parameter.IsMatch(entry.Key) &&
                int.TryParse(entry.Value.Replace("text_", ""), out var index) && index is >= 2701 and <= 2736)
                .Select(entry => (new Lazy<Regex>(() => new Regex("^" + Regex.Escape(entry.Key) + "(?=$| )",
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20))), entry.Key, entry.Key, entry.Key, 0)))
            .ToArray();
    }
    internal static string T(string english)
    {

        if (Current.labels.TryGetValue(english,out var resourceKey)) return Current.Resources.GetString(resourceKey,false)!;
        foreach (var template in Current.messageTemplates)
        {
            if (!english.StartsWith(template.Prefix,StringComparison.Ordinal)) continue;
            var match = template.Pattern.Value.Match(english);
            if (!match.Success) continue;
            // Service values are already formatted; keep empty text, names and IDs exact.
            var args = Enumerable.Range(0, template.ArgumentCount)
                .Select(index =>
                {
                    var value = match.Groups[$"arg{index}"].Value;
                    return (object)TranslateArgument(template.Key, index, value);
                }).ToArray();
            return string.Format(Current.Culture, Current.Resources.GetString(Current.labels[template.Key], false)!, args);
        }
        foreach (var prefix in Current.concatenatedPrefixes)
            if (english.Length>prefix.Length && english.StartsWith(prefix,StringComparison.Ordinal))
                return Current.Resources.GetString(Current.labels[prefix],false)!+" "+T(english[prefix.Length..]);
        if (english.Contains(" || ",StringComparison.Ordinal)) return string.Join(" || ",english.Split(" || ",StringSplitOptions.None).Select(T));
        if (english.Contains(" | ",StringComparison.Ordinal)) return string.Join(" | ",english.Split(" | ",StringSplitOptions.None).Select(T));
        return english; // External names, command tokens and raw runtime data retain their original values.
    }
    private static string TranslateArgument(string template, int index, string value)
    {
        if ((template == "Leveling child {0}: {1} at party minimum level {2}; {3}" && index == 3) ||
            (template == "Leveling Mode '{0}' blocked before child {1}: {2}" && index == 2) ||
            (template == "Leveling Mode child {0}: {1}" && index == 1) ||
            (template == "Leveling Mode dry run compiled one immutable child successfully: {0}" && index == 0))
            return LevelingDetail(value);
        if (template is "Readiness: {0}" or "Static: {0}" or "Schedule: {0}" or "Module: {0}" or "Slot: {0}" && index == 0)
            return string.Join(" || ", value.Split(" || ", StringSplitOptions.None).Select(T));
        return IsAuthoredArgument(template, index) ? T(value) : value;
    }
    internal static string LevelingSummary(DadLevelingCompilation compilation)
        => compilation.Status == DadLevelingCompilationStatus.Blocked && compilation.Blockers.Count > 0
            ? string.Join(" ", compilation.Blockers.Distinct(StringComparer.OrdinalIgnoreCase).Select(LevelingDetail))
            : T(compilation.Summary);
    private static string LevelingDetail(string english)
    {
        if (english.StartsWith("Leveling child ", StringComparison.Ordinal)) return T(english);
        var translated = new List<string>();
        var remaining = english;
        while (remaining.Length > 0)
        {
            var ledger = Regex.Match(remaining,
                @"^(?<slot>Slot\d+) (?<character>.+?): (?<detail>an exact XADB job ledger is unavailable\.|the roster ledger requires refresh\.|the job ledger is empty\.|job ledger quality '.*?' is not complete\.)(?=$| )",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
            var consumed = 0;
            if (ledger.Success)
            {
                translated.Add(F("{0} {1}: {2}", ledger.Groups["slot"].Value,
                    ledger.Groups["character"].Value, T(ledger.Groups["detail"].Value)));
                consumed = ledger.Length;
            }
            else
            {
                foreach (var template in Current.levelingTemplates)
                {
                    // These literals occur in every successful match. Skip unrelated
                    // patterns before creating their lazy matcher or entering the engine.
                    if (!remaining.StartsWith(template.Prefix, StringComparison.Ordinal) ||
                        !remaining.Contains(template.RequiredLiteral, StringComparison.Ordinal)) continue;
                    var match = template.Pattern.Value.Match(remaining);
                    if (!match.Success || match.Length == 0) continue;
                    var args = Enumerable.Range(0, template.ArgumentCount)
                        .Select(index => (object)TranslateArgument(template.Key, index, match.Groups[$"arg{index}"].Value)).ToArray();
                    translated.Add(string.Format(Current.Culture, Current.Resources.GetString(Current.labels[template.Key], false)!, args));
                    consumed = match.Length;
                    break;
                }
            }
            if (consumed == 0) { translated.Add(T(remaining)); break; }
            remaining = remaining[consumed..];
            if (remaining.StartsWith(' ')) remaining = remaining[1..];
        }
        return string.Join(" ", translated);
    }
    // These argument positions were traced to DAD role/module labels or authored
    // summary/safety getters. Counts, slot IDs and arbitrary service data stay raw.
    private static bool IsAuthoredArgument(string template, int index) => template switch
    {
        "{0} | effective {1}" or "Open {0} guide" => index == 0,
        "Accepted {0} assignment." or "Participant completed {0} and exited duty." or
        "Participant running {0}." or "Dad stop policy continuing: {0}" or
        "Worker assignment is waiting for fresh safe runtime truth before execution: {0}" or
        "Worker preparation is waiting for fresh exact prequeue safety proof: {0}" or
        "Committed relog is waiting without timeout: {0}" or "Committed reset is waiting without timeout: {0}" or
        "AutoRetainer title login is waiting for VERMAXION to report exactly Idle (observed {0}); no command was issued." => index == 0,
        "Planner request blocked by module runtime: {0}" or "Planner request ready to start. {0}" or
        "Planner request blocked: {0}" or "Waiting for refreshed strict-runtime readiness: {0}" or
        "Planner request remains schedulable while runtime truth refreshes: {0}" or
        "Planner request remains terminally blocked while retaining readiness detail: {0}" or
        "Planner direct start is waiting on live readiness; scheduler takeover is allowed. {0}" or
        "Planner request built, but start is blocked by module capability: {0}" or
        "Scheduler can resolve live readiness: {0}" or "Leveling Mode does not support the {0} lane." => index == 0,
        "{0} {1} has no unlocked {2} compatible with role {3}." => index is 2 or 3,
        "Duty {0} is incompatible with the {1} lane." => index == 1,
        "Leveling Mode '{0}' completed after {1} child run(s): {2}" => index == 2,
        "Leveling Mode stopped because child {0} was cancelled: {1}" or
        "Leveling Mode stopped without replay after child {0} failed: {1}" or
        "Leveling Mode stopped after child {0}: {1} The child will not replay." or
        "Schedule '{0}' waiting for Leveling Mode: {1}" => index == 1,
        "Leveling Mode stopped after child {0}: exact world-ready refresh route for {1} {2} was {3}." => index == 3,
        "Exact roster refresh did not prove a complete saved XADB job ledger: {0}" => index == 0,
        "Active {0}: {1}" => index is 0 or 1,
        "{0} Published {1}; received {2}." => index == 0,
        "Starting {0} work for {1}." or "Waiting at strict {0} boundary: {1}" => index is 0 or 1,
        "All {0} workers completed and settled {1}. {2}" => index is 1 or 2,
        "All {0} workers completed and settled {1}." => index == 1,
        "Takeover epoch {0} queued for a fresh reservation after retryable outcome: {1} Next reserve attempt begins after the five-second cadence." => index == 1,
        _ => false,
    };
    internal static string F(string english,params object?[] args) => string.Format(Current.Culture,T(english),args.Select(value=>value is Enum e?T(e.ToString()):value).ToArray());
    internal static string F(FormattableString text) => string.Format(Current.Culture, T(text.Format),
        text.GetArguments().Select(value => value is Enum e ? T(e.ToString()) : value is string s && s is "Yes" or "No" or "Y" or "N" or "On" or "Off" or "Forced / On" or "Override Off" or "Interactive" or "Passive" ? T(s) : value).ToArray());
    internal static IDisposable Font(UiFontRole role) => Current.pushFont(role);
    internal Scope Enter() => new(this);
    internal readonly struct Scope : IDisposable
    {
        private readonly UiText? previous;
        internal Scope(UiText value) { previous=current; current=value; }
        public void Dispose() => current=previous;
    }
    internal static string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("g",Current.Culture) ?? T("Never");
    internal ushort[] GlyphRanges()
    {
        var chars=RequiredText.Select(MaterialText.NativeGlyphText).SelectMany(t=>t).Where(c=>!char.IsControl(c))
            .Concat(Enumerable.Range(0x20,0x024F-0x20+1).Select(i=>(char)i))
            .Concat(Enumerable.Range(0x0400,0x052F-0x0400+1).Select(i=>(char)i)).Concat("—").Distinct().Order().ToArray();
        var result=new List<ushort>();
        for(var index=0;index<chars.Length;index++)
        {
            var first=chars[index]; var last=first;
            while(index+1<chars.Length && chars[index+1]==last+1) last=chars[++index];
            result.Add(first); result.Add(last);
        }
        result.Add(0); return result.ToArray();
    }
    internal static IEnumerable<string> Values(ResourceSet set) => set.Cast<DictionaryEntry>().Select(e=>(string)e.Value!);
    public void Dispose() { manager.ReleaseAllResources();englishManager.ReleaseAllResources(); }
}
