using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadMcp.Core;
namespace CadMcp.Providers;

/// <summary>Read-only MCP config layers for built-in and discovered custom Codex roles.</summary>
public static class CadSubagentPolicy
{
    public static string PrimaryThreadFile(ProviderOptions options)=>Path.Combine(Path.GetTempPath(),"CadMcp","primary-threads",Portable.Hash(Encoding.UTF8.GetBytes(options.OwnerId+"|"+options.CadSessionId+"|"+options.CadDocumentId))+".txt");
    public static void BindPrimary(ProviderOptions options,string thread)
    {string path=PrimaryThreadFile(options);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,thread,new UTF8Encoding(false));}
    public static IReadOnlyList<string> Roles(ProviderOptions options)
    {
        var roles=new HashSet<string>(StringComparer.Ordinal){"default","worker","explorer","cad_reviewer"};
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string home=Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");directories.Add(home);
        for(var current=new DirectoryInfo(options.WorkingDirectory);current is not null;current=current.Parent)directories.Add(Path.Combine(current.FullName,".codex"));
        foreach(string directory in directories.Where(Directory.Exists))
        {
            foreach(string file in Directory.EnumerateFiles(directory,"*.toml",SearchOption.TopDirectoryOnly))
            {
                string text=File.ReadAllText(file);
                foreach(Match match in Regex.Matches(text,"(?m)^\\s*\\[agents\\.(?:\"([^\"]+)\"|'([^']+)'|([A-Za-z0-9_-]+))\\]"))Add(match.Groups.Cast<Group>().Skip(1).First(g=>g.Success).Value);
            }
            string agents=Path.Combine(directory,"agents");
            if(Directory.Exists(agents))foreach(string file in Directory.EnumerateFiles(agents,"*.toml"))
            {
                var match=Regex.Match(File.ReadAllText(file),"(?m)^\\s*name\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)')");
                if(!match.Success)throw new IOException("CAD helper policy cannot identify custom agent file: "+file);
                Add(match.Groups.Cast<Group>().Skip(1).First(g=>g.Success).Value);
            }
        }
        return roles.OrderBy(v=>v,StringComparer.Ordinal).ToArray();
        void Add(string role)
        {
            if(!Regex.IsMatch(role,"^[A-Za-z0-9_-]{1,64}$"))throw new IOException("Cannot enforce CAD read-only policy for custom subagent name: "+role);
            roles.Add(role);if(roles.Count>64)throw new IOException("Too many custom roles to enforce the CAD subagent policy");
        }
    }
    public static string[] Arguments(ProviderOptions options)
    {
        string directory=Path.Combine(Path.GetTempPath(),"CadMcp","reviewers");Directory.CreateDirectory(directory);
        string key=Portable.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options.CadEnvironment)));
        var arguments=new List<string>();
        foreach(string role in Roles(options))
        {
            string description="Read-only CAD helper: analyze geometry, requirements or quality; return evidence to the primary writer";
            string path=Path.Combine(directory,key+"-"+role+".toml");
            var text=new StringBuilder("name = "+JsonSerializer.Serialize(role)+"\ndescription = "+JsonSerializer.Serialize(description)+"\nsandbox_mode = \"read-only\"\ndeveloper_instructions = "+JsonSerializer.Serialize(
                "You are a read-only CAD subagent. Inspect only the assigned DWG; measure native geometry, units, the task acceptance contract and actual rendered images. Never edit, export, publish, focus, cancel, run Lisp or bypass the MCP restriction by launching another host. Return measured evidence, defects and uncertainty to the primary agent. Only the primary applies changes. Do not declare photo fidelity or normative compliance without evidence.")+"\n[mcp_servers.cad.env]\n");
            foreach(var pair in options.CadEnvironment)text.Append(pair.Key).Append(" = ").Append(JsonSerializer.Serialize(pair.Value)).Append('\n');
            text.Append("CAD_MCP_PRIMARY_THREAD_FILE = ").Append(JsonSerializer.Serialize(PrimaryThreadFile(options))).Append('\n');
            text.Append("CAD_MCP_READ_ONLY = \"1\"\n");
            string content=text.ToString();if(!File.Exists(path)||File.ReadAllText(path)!=content)File.WriteAllText(path,content,new UTF8Encoding(false));
            arguments.AddRange(["-c","agents."+role+".description="+JsonSerializer.Serialize(description),"-c","agents."+role+".config_file="+JsonSerializer.Serialize(path)]);
        }
        return arguments.ToArray();
    }
}
