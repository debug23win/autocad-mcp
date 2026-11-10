using System.Reflection;
using System.Runtime.Loader;
using CadMcp.Core;
using CadMcp.Host;
using CadMcp.Providers;
namespace CadMcp.Tests;
public sealed class PreferencesAndCompilerTests
{
    [Fact]
    public void Provider_models_and_progress_preference_survive_a_new_store_instance()
    {
        using var folder = new TempFolder(); string path = folder.File("preferences.json");
        var chosen = new ChatPreferences(1, "codex-model", "high", "claude-model", "max", false);
        new ChatPreferencesStore(path).Save(chosen);
        Assert.Equal(chosen, new ChatPreferencesStore(path).Load());
        var switched = chosen with { Provider = 0, CodexModel = "other-model", CodexEffort = "medium" };
        new ChatPreferencesStore(path).Save(switched);
        Assert.Equal(switched, new ChatPreferencesStore(path).Load());
        Assert.Equal("claude-model", new ChatPreferencesStore(path).Load()!.ClaudeModel);
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }
    [Fact]
    public void Damaged_preferences_do_not_break_chat_startup()
    {
        using var folder = new TempFolder(); string path = folder.File("preferences.json");
        File.WriteAllText(path, "{broken"); Assert.Null(new ChatPreferencesStore(path).Load());
        File.WriteAllText(path, "{\"provider\":99}"); Assert.Null(new ChatPreferencesStore(path).Load());
    }
    [Fact]
    public void Real_compiler_emits_and_executes_ordinary_csharp_without_an_installed_sdk()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        const string code = "using System.Linq; public static class CadMcpScript { public static object Run() => new[] { 1, 2, 3 }.Select(x => x*x).Sum(); }";
        byte[] bytes = CSharpReferenceCompiler.Compile(code, paths);
        var load = new AssemblyLoadContext("compiler-verification", true);
        try { using var memory = new MemoryStream(bytes); var assembly = load.LoadFromStream(memory); Assert.Equal(14, assembly.GetType("CadMcpScript")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)); }
        finally { load.Unload(); }
        var fault = Assert.Throws<CadFault>(() => CSharpReferenceCompiler.Compile("public class {", paths));
        Assert.Equal("CSHARP_COMPILE_FAILED", fault.Code);
    }
    [Fact]
    public void Progress_is_readable_and_copied_answer_stays_the_final_answer()
    {
        using var folder = new TempFolder();
        ChatLine[] lines = [new("assistant", "Готово", Steps: ["Читаю страницы PDF", "Проверяю размеры", "Завершено: cad_search"])];
        string html = ChatMarkup.ConversationHtml(lines, folder.Path);
        Assert.Contains("Ход работы", html); Assert.Contains("Читаю страницы PDF", html); Assert.DoesNotContain("cad_search", html);
        Assert.Equal("Готово", ChatMarkup.AnswerText(lines, 0));
    }
}
