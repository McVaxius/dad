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
            Assert.Equal(expectedProtection, UiText.T("Lobby protection is enabled."));

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
