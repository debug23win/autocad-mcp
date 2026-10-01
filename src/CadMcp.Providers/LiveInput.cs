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
    private int sequence=100;
    public void Open(Func<int,ChatInput,CancellationToken,Task> write){lock(gate)sender=write;}
    public async Task<InputReceipt> SendAsync(ChatInput input,CancellationToken ct)
    {
        Func<int,ChatInput,CancellationToken,Task>? write;int id;TaskCompletionSource<InputReceipt> completion;
        lock(gate)
        {
            write=sender;if(write is null)return InputReceipt.Unavailable;
            id=Interlocked.Increment(ref sequence);completion=new(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=completion;
        }
        try
        {
            await write(id,input,ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15),ct);
        }
        catch(Exception error) when(error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {return new("uncertain","Доставка дополнения не подтверждена. Автоматический повтор отключён: "+error.Message);}
        finally{pending.TryRemove(id,out _);}
    }
    public bool Resolve(int id,InputReceipt receipt)
    {if(!pending.TryRemove(id,out var completion))return false;completion.TrySetResult(receipt);return true;}
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
