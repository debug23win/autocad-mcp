using System.Collections.Concurrent;

namespace CadMcp.Providers;

public sealed record ChatInput(string Prompt, IReadOnlyList<ChatAttachment> Attachments);
public sealed record InputReceipt(string State, string Message)
{
    public static InputReceipt Unavailable => new("unavailable", "Текущий ход ещё не начался или уже завершён");
}

// A receipt is acknowledged by the provider protocol, never merely by writing stdin.
// Ambiguous delivery is not retried: that could execute a CAD request twice.
internal sealed class LiveInput
{
    private readonly object gate=new();
    private readonly ConcurrentDictionary<int,TaskCompletionSource<InputReceipt>> pending=new();
    private Func<int,ChatInput,CancellationToken,Task>? sender;
    private int sequence=100, writing;
    public TimeSpan AcknowledgementTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public void Open(Func<int,ChatInput,CancellationToken,Task> write){lock(gate)sender=write;}
    public async Task<InputReceipt> SendAsync(ChatInput input,CancellationToken ct)
    {
        Func<int,ChatInput,CancellationToken,Task>? write;int id;TaskCompletionSource<InputReceipt> completion;
        lock(gate)
        {
            write=sender;if(write is null)return InputReceipt.Unavailable;
            id=Interlocked.Increment(ref sequence);completion=new(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=completion;
            writing++;
        }
        try
        {
            try { await write(id,input,ct); }
            finally { lock(gate) writing--; }
            return await completion.Task.WaitAsync(AcknowledgementTimeout,ct);
        }
        catch(Exception error) when(error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {return new("uncertain","Доставка дополнения не подтверждена. Автоматический повтор отключён: "+error.Message);}
        finally{pending.TryRemove(id,out _);}
    }
    public bool Resolve(int id,InputReceipt receipt)
    {if(!pending.TryRemove(id,out var completion))return false;completion.TrySetResult(receipt);return true;}
    /// <summary>
    /// Stop accepting input only when nothing is being written or waiting for its acknowledgement.
    /// Checked under the same lock that admits new input, so no message slips in after the decision.
    /// </summary>
    public bool TryClose()
    {
        lock(gate)
        {
            if(writing>0||!pending.IsEmpty)return false;
            sender=null;return true;
        }
    }
    public void Close()
    {
        lock(gate)sender=null;
        foreach(var item in pending.ToArray())Resolve(item.Key,new("uncertain","Ход завершился до подтверждения дополнения; автоматический повтор отключён"));
    }
    public static IReadOnlyList<object> CodexContent(ChatInput input)
    {
        var content=new List<object>{new{type="text",text=ChatAttachments.AddToPrompt(input.Prompt,input.Attachments)}};
        foreach(var file in input.Attachments.Where(f=>f.Kind==AttachmentKind.Image))content.Add(new{type="localImage",path=file.Path});
        return content;
    }
    public static object ClaudeMessage(ChatInput input,string uuid)
    {
        var content=new List<object>{new{type="text",text=ChatAttachments.AddToPrompt(input.Prompt,input.Attachments)}};
        foreach(var file in input.Attachments.Where(f=>f.Kind==AttachmentKind.Image))
            content.Add(new{type="image",source=new{type="base64",media_type=ChatAttachments.ImageMediaType(file.Path),data=Convert.ToBase64String(ChatAttachments.ImageBytes(file))}});
        return new{type="user",uuid,message=new{role="user",content}};
    }
}

/// <summary>
/// Which user messages of one Claude stream-json process have been answered. Claude can answer each
/// message with its own result or merge queued messages into one turn; a result that lists the
/// answered message ids is exact, otherwise one result per message is assumed.
/// </summary>
internal sealed class ClaudeTurns(string initial)
{
    private readonly List<string> acknowledged = [initial];
    private readonly HashSet<string> answered = new(StringComparer.Ordinal);
    private bool lastResultListedMessages = true;
    public bool AnyResult { get; private set; }
    public void Acknowledge(string uuid) { if (!acknowledged.Contains(uuid)) acknowledged.Add(uuid); }
    public void Result(IReadOnlyCollection<string>? uuids)
    {
        AnyResult = true;
        lastResultListedMessages = uuids is not null;
        if (uuids is not null) { answered.UnionWith(uuids); answered.Add(initial); return; }
        if (acknowledged.FirstOrDefault(u => !answered.Contains(u)) is { } oldest) answered.Add(oldest);
    }
    /// <summary>
    /// True when every acknowledged message has a result. After a result that did not list its messages,
    /// a quiet period is also accepted, because merged messages then never get a result of their own.
    /// </summary>
    public bool Settled(bool quietAfterResult) => acknowledged.All(answered.Contains) || quietAfterResult && AnyResult && !lastResultListedMessages;
}
