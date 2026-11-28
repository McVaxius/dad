using AethertekUI;
using AethertekUI.Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace dad.Windows;

internal sealed class DadFonts : IDisposable
{
    private static readonly UiFontRole[] WindowRoles =
    [
        UiFontRole.Body,
        UiFontRole.BodyStrong,
        UiFontRole.Caption,
        UiFontRole.Small,
    ];

    private readonly IFontAtlas atlas;
    private readonly Dictionary<UiFontRole, IFontHandle> roles = [];
    private readonly List<IFontHandle> handles = [];
    private int generation;

    internal int Generation => System.Threading.Volatile.Read(ref generation);
    internal IEnumerable<UiFontRole> Roles => roles.Keys;

    internal DadFonts(IFontAtlas atlas, ushort[] ranges, string language, string assetRoot)
    {
        this.atlas = atlas;
        var faces = new Dictionary<(MaterialFontWeight Weight, float Height), IFontHandle>();
        atlas.BuildStepChange += MaterialFontAtlas.ReleaseRasterizedInputs;
        try
        {
            foreach (var role in WindowRoles)
            {
                var face = (DadPresentation.FontWeights[(int)role], DadPresentation.AtlasHeight(role));
                if (!faces.TryGetValue(face, out var handle))
                {
                    handle = MaterialFontAtlas.CreateFont(atlas, face.Item1, face.Item2, ranges, language, assetRoot,
                        pageSize: 2048, includeHostLanguage: false);
                    handles.Add(handle);
                    faces.Add(face, handle);
                    handle.ImFontChanged += FontChanged;
                }
                roles.Add(role, handle);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void FontChanged(IFontHandle handle, ILockedImFont font)
        => System.Threading.Interlocked.Increment(ref generation);

    internal bool Ready => handles.All(handle => handle.Available && handle.LoadException is null);
    internal Exception? LoadException => handles.FirstOrDefault(handle => handle.LoadException is not null)?.LoadException;

    internal unsafe void CheckGlyphs(IEnumerable<string> strings, MaterialTextRenderer shapedText)
    {
        var required = strings.ToArray();
        foreach (var (role, handle) in roles)
        {
            using var font = handle.Lock();
            shapedText.CheckGlyphs(required, DadPresentation.AtlasHeight(role) * ImGui.GetIO().FontGlobalScale);
            foreach (var text in required.Select(MaterialText.NativeGlyphText))
                foreach (var character in text.Where(c => !char.IsControl(c)))
                    if (ImGui.FindGlyphNoFallback(font.ImFont, character).Handle == null)
                        throw new InvalidOperationException($"Required UI glyph missing: U+{(int)character:X4} in {role}");
        }
    }

    internal IDisposable Push(UiFontRole role)
    {
        var handle = roles[role];
        if (!handle.Available || handle.LoadException is not null)
            throw new InvalidOperationException("Dad fonts are not ready.", handle.LoadException);
        return handle.Push();
    }

    public void Dispose()
    {
        atlas.BuildStepChange -= MaterialFontAtlas.ReleaseRasterizedInputs;
        foreach (var handle in handles)
        {
            handle.ImFontChanged -= FontChanged;
            handle.Dispose();
        }
        atlas.Dispose();
    }
}
