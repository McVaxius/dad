using dad.Windows;
using Xunit;

namespace dad.Windows
{
    // UiText needs only the font-role token; font/native drawing stays outside this startup check.
    internal enum UiFontRole { Body, BodyStrong, Title, Caption, Small }
}

namespace dad.Tests
{
    public sealed class DadUiTextStartupTests
    {
        public static IEnumerable<object[]> SupportedLanguages()
            => UiText.Languages.Select(language => new object[] { language.Code });

        [Theory]
        [MemberData(nameof(SupportedLanguages))]
        public void EverySupportedLanguageConstructsAndTranslatesColourModes(string language)
        {
            var fontScope = new SyntheticFontScope();
            using var text = new UiText(language, _ => fontScope);
            using var frame = text.Enter();

            var expectedAuto = text.Resources.GetString("text_0222", false);
            var expectedPicker = text.Resources.GetString("Picker", false);
            var expectedProtection = text.Resources.GetString("recovery_021", false);
            Assert.False(string.IsNullOrWhiteSpace(expectedAuto));
            Assert.False(string.IsNullOrWhiteSpace(expectedPicker));
            Assert.False(string.IsNullOrWhiteSpace(expectedProtection));
            Assert.Equal(expectedAuto, UiText.T("Auto"));
            Assert.Equal(expectedPicker, UiText.T("Picker"));
            foreach (var caption in new[] { "LCARS", "Mute UI sounds", "Unmute UI sounds", "LCARS panels and outlined controls. Picker colours are retained.", "Audio output is unavailable." })
            {
                Assert.False(string.IsNullOrWhiteSpace(text.Resources.GetString(caption, false)));
                Assert.Equal(text.Resources.GetString(caption, false), UiText.T(caption));
            }
            Assert.Equal(expectedProtection, UiText.T("Lobby protection is enabled."));
            Assert.Equal(text.Resources.GetString("trust_ui_026", false), UiText.T("NPC leveling"));
            Assert.Equal(text.Resources.GetString("trust_ui_027", false), UiText.T("NPC roster"));
            Assert.Equal(text.Resources.GetString("trust_ui_048", false), UiText.T("Trust presets"));
            foreach (var (key, caption) in new[]
            {
                ("schedule_timing_001", "Schedule elapsed (including pauses)"),
                ("schedule_timing_002", "Average preset duration"),
                ("schedule_timing_004", "No completed presets in retained history."),
            })
            {
                Assert.False(string.IsNullOrWhiteSpace(text.Resources.GetString(key, false)));
                Assert.Equal(text.Resources.GetString(key, false), UiText.T(caption));
            }
            Assert.Equal(string.Format(text.Culture, text.Resources.GetString("schedule_timing_003", false)!, "00:20:00", 2),
                UiText.F("{0} | {1} completed preset(s) in retained history", "00:20:00", 2));
            Assert.Equal(text.Resources.GetString("trust_ui_028", false), UiText.T("NPC target level"));
            Assert.Equal(text.Resources.GetString("trust_ui_032", false), UiText.T("Player-job target level"));
            Assert.Equal(text.Resources.GetString("ui_accounts_enable_selected", false), UiText.T("Enable selected"));
            Assert.Equal(text.Resources.GetString("ui_accounts_revision_conflict", false),
                UiText.T("Revision conflict; cancel and edit again."));
            Assert.Equal(string.Format(text.Culture, text.Resources.GetString("ui_accounts_read_only_count", false)!, 3),
                UiText.F("{0} read-only", 3));
            Assert.Equal(string.Format(text.Culture, text.Resources.GetString("ui_accounts_unidentified_count", false)!, 2, 1),
                UiText.F("{0} connection(s) awaiting account identity; {1} coordinator(s).", 2, 1));

            using (UiText.Font(UiFontRole.Body))
                Assert.False(fontScope.Disposed);
            Assert.True(fontScope.Disposed);
        }

        private sealed class SyntheticFontScope : IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }
    }
}
