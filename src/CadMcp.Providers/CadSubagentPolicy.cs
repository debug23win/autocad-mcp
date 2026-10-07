using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadMcp.Core;
namespace CadMcp.Providers;

/// <summary>Read-only MCP config layers for built-in and discovered custom Codex roles.</summary>
public static class CadSubagentPolicy
{
    public static string PrimaryThreadFile(ProviderOptions options)=>Path.Combine(Wire.DataDirectory("agents","primary-threads"),Portable.Hash(Encoding.UTF8.GetBytes(options.OwnerId+"|"+options.CadSessionId+"|"+options.CadDocumentId))+".txt");
    /// <summary>The MCP host reads this file on every call; it must never see a partially written thread id.</summary>
    public static void BindPrimary(ProviderOptions options,string thread)=>WriteAtomic(PrimaryThreadFile(options),thread);
    internal static void WriteAtomic(string path,string content)
    {
        try { if (File.Exists(path) && File.ReadAllText(path) == content) return; }
        catch (IOException) { }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temp,content,new UTF8Encoding(false));
            // Replacing can briefly fail while a reader has the file open.
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(temp,path,true); return; }
                catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(50 * attempt); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static IReadOnlyList<string> Roles(ProviderOptions options, ICollection<string>? warnings = null)
    {
        var roles=new HashSet<string>(StringComparer.Ordinal){"default","worker","explorer","cad_reviewer"};
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");directories.Add(home);
        for(var current=new DirectoryInfo(options.WorkingDirectory);current is not null;current=current.Parent)directories.Add(Path.Combine(current.FullName,".codex"));
        // A custom role this policy cannot cover still cannot write: the MCP host admits writes only from
        // the bound primary thread. So an unreadable or unusual role is reported, not a reason to refuse the chat.
        foreach(string directory in directories)
        {
            try
            {
                if(!Directory.Exists(directory))continue;
                foreach(string file in Directory.EnumerateFiles(directory,"*.toml",SearchOption.TopDirectoryOnly))
                    foreach(Match match in Regex.Matches(File.ReadAllText(file),"(?m)^\\s*\\[agents\\.(?:\"([^\"]+)\"|'([^']+)'|([A-Za-z0-9_-]+))\\]"))
                        Add(match.Groups.Cast<Group>().Skip(1).First(g=>g.Success).Value,file);
                string agents=Path.Combine(directory,"agents");
                if(Directory.Exists(agents))foreach(string file in Directory.EnumerateFiles(agents,"*.toml"))
                {
                    var match=Regex.Match(File.ReadAllText(file),"(?m)^\\s*name\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)')");
                    if(match.Success)Add(match.Groups.Cast<Group>().Skip(1).First(g=>g.Success).Value,file);
                    else warnings?.Add("Помощник из "+file+" не распознан; запись в чертёж ему всё равно недоступна");
                }
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException)
            { warnings?.Add("Не удалось прочитать роли помощников в "+directory+": "+e.Message); }
        }
        return roles.OrderBy(v=>v,StringComparer.Ordinal).ToArray();
        void Add(string role,string file)
        {
            // Only names that are safe as TOML keys receive an override.
            if(!Regex.IsMatch(role,"^[A-Za-z0-9_-]{1,64}$")){warnings?.Add("Роль помощника «"+role+"» из "+file+" пропущена: недопустимое имя");return;}
            if(roles.Count>=64&&!roles.Contains(role)){warnings?.Add("Слишком много ролей помощников; роль «"+role+"» пропущена");return;}
            roles.Add(role);
        }
    }
    public static string[] Arguments(ProviderOptions options, ICollection<string>? warnings = null)
    {
        string directory=Wire.DataDirectory("agents","reviewers");Directory.CreateDirectory(directory);
        string key=Portable.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options.CadEnvironment)));
        var arguments=new List<string>();
        foreach(string role in Roles(options, warnings))
        {
            string description="Read-only CAD helper: analyze geometry, requirements or quality; return evidence to the primary writer";
            string path=Path.Combine(directory,key+"-"+role+".toml");
            var text=new StringBuilder("name = "+JsonSerializer.Serialize(role)+"\ndescription = "+JsonSerializer.Serialize(description)+"\nsandbox_mode = \"read-only\"\ndeveloper_instructions = "+JsonSerializer.Serialize(
                "You are a read-only CAD subagent. Inspect only the assigned DWG; measure native geometry, units, the task acceptance contract and actual rendered images. Never edit, export, publish, focus, cancel, run Lisp or bypass the MCP restriction by launching another host. Return measured evidence, defects and uncertainty to the primary agent. Only the primary applies changes. Do not declare photo fidelity or normative compliance without evidence.")+"\n[mcp_servers.cad.env]\n");
            foreach(var pair in options.CadEnvironment)text.Append(pair.Key).Append(" = ").Append(JsonSerializer.Serialize(pair.Value)).Append('\n');
            text.Append("CAD_MCP_PRIMARY_THREAD_FILE = ").Append(JsonSerializer.Serialize(PrimaryThreadFile(options))).Append('\n');
            text.Append("CAD_MCP_READ_ONLY = \"1\"\n");
            WriteAtomic(path,text.ToString());
            arguments.AddRange(["-c","agents."+role+".description="+JsonSerializer.Serialize(description),"-c","agents."+role+".config_file="+JsonSerializer.Serialize(path)]);
        }
        return arguments.ToArray();
    }
}
