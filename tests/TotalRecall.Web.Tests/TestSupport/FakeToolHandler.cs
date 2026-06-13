namespace TotalRecall.Web.Tests.TestSupport;

using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Server;

/// <summary>Canned IToolHandler so the web layer can be tested without infra.</summary>
public sealed class FakeToolHandler : IToolHandler
{
    private readonly string _resultJson;
    private readonly bool _isError;

    public FakeToolHandler(string name, string resultJson, bool isError = false)
    {
        Name = name;
        _resultJson = resultJson;
        _isError = isError;
    }

    public string Name { get; }
    public string Description => "fake";
    private static readonly JsonElement _schema =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public JsonElement InputSchema => _schema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct) =>
        Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = _resultJson } },
            IsError = _isError ? true : null,
        });
}
