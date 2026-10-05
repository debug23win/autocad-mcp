using System.Text.Json.Nodes;
namespace CadMcp.Host;

/// <summary>Enforces the sole writer by trusted MCP caller metadata, including inherited connections.</summary>
internal static class CadAccess
{
    private static readonly AsyncLocal<bool?> ReadOnlyCall=new();
    internal static bool ReadOnly=>Environment.GetEnvironmentVariable("CAD_MCP_READ_ONLY")=="1"||ReadOnlyCall.Value==true;
    internal static IDisposable Scope(JsonObject? metadata)
    {
        bool? prior=ReadOnlyCall.Value;bool restricted=false;
        string? path=Environment.GetEnvironmentVariable("CAD_MCP_PRIMARY_THREAD_FILE");
        if(!string.IsNullOrWhiteSpace(path))
        {
            restricted=true;
            try
            {
                string root=File.ReadAllText(path).Trim();
                restricted=string.IsNullOrEmpty(root)||metadata?["threadId"]?.GetValue<string>()!=root;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException){ }
        }
        ReadOnlyCall.Value=restricted;return new Guard(prior);
    }
    private sealed class Guard(bool? prior):IDisposable
    {public void Dispose()=>ReadOnlyCall.Value=prior;}
}
