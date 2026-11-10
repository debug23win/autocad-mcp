using System.Globalization;
using System.Text;
using System.Text.Json;
using CadMcp.Core;
using CadMcp.Providers;

namespace CadMcp.Tests;

public sealed class InstallationLanguageTests
{
    [Theory]
    [InlineData("russian", "ru")]
    [InlineData("english", "en")]
    [InlineData(" en-US\r\n", "en")]
    [InlineData("unsupported", "ru")]
    public void Installer_choice_is_read_from_its_own_file(string stored, string expected)
    {
        using var folder = new TempFolder();
        string path = Path.Combine(folder.Path, "installation-language.txt");
        Assert.Equal("ru", UiText.ReadLanguage(path));
        File.WriteAllText(path, stored);
        Assert.Equal(expected, UiText.ReadLanguage(path));
        File.WriteAllText(path, new string('x', 65));
        Assert.Equal("ru", UiText.ReadLanguage(path));
    }

    [Fact]
    public void Ui_and_agent_share_installation_language_without_changing_thread_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        Assert.Equal("Copy answer", UiText.Translate("Копировать ответ", "en"));
        Assert.Equal("Копировать ответ", UiText.Translate("Копировать ответ", "ru"));
        Assert.Contains("progress comments in English, the installation language", CadAgent.InstructionsFor("en"));
        Assert.Contains("progress comments in Russian, the installation language", CadAgent.InstructionsFor("ru"));
        Assert.DoesNotContain("Reply in the user's language.", CadAgent.InstructionsFor("en"));
        Assert.Equal("20Б1", UiText.Translate("20Б1", "en"));
        Assert.Equal("user text Ø108", UiText.Translate("user text Ø108", "en"));
        Assert.Same(culture, CultureInfo.CurrentCulture);
    }

    [Fact]
    public void Translated_formats_preserve_every_dynamic_argument_and_its_format()
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("CadMcp.Core.Resources.ui.en.json")!;
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        foreach (var (russian, english) in entries)
        {
            var source = CompositeFormat.Parse(russian);
            var target = CompositeFormat.Parse(english);
            Assert.Equal(source.MinimumArgumentCount, target.MinimumArgumentCount);
            var arguments = Enumerable.Range(0, source.MinimumArgumentCount).Select(i => (object)(i + 0.125)).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, source, arguments);
            _ = string.Format(CultureInfo.InvariantCulture, target, arguments);
        }
        string result = UiText.FormatFor($"**Бенчмарк CAD MCP: {18}/{57} тестов пройдено**\n\n", "en");
        Assert.Equal("**CAD MCP benchmark: 18/57 tests passed**\n\n", result);
    }

    [Fact]
    public void Historical_technical_steps_are_hidden_in_both_languages_without_hiding_user_content()
    {
        using var folder = new TempFolder();
        ChatLine[] lines = [new("assistant", "Keep user text: Завершено: cad_query", Steps:
            ["Завершено: cad_query", "Выполняю cad_search", "Completed: cad_query", "Running cad_search", "Independent check"] )];
        string html = ChatMarkup.ConversationHtml(lines, folder.Path);
        Assert.Contains("Keep user text: Завершено: cad_query", html);
        Assert.Contains("Independent check", html);
        Assert.DoesNotContain("<li><p>Completed: cad_query", html);
        Assert.DoesNotContain("<li><p>Выполняю cad_search", html);
    }
}
