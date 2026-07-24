namespace Wox.Plugin;

public interface IPlugin
{
    List<Result> Query(Query query);

    void Init(PluginInitContext context);

    string Name { get; }

    string Description { get; }
}

public sealed class PluginInitContext
{
}

public sealed class Query
{
    public Query(string search, string actionKeyword = "")
    {
        Search = search;
        ActionKeyword = actionKeyword;
    }

    public string Search { get; }

    public string ActionKeyword { get; }
}

public sealed class ActionContext
{
}

public sealed class Result
{
    public string Title { get; set; } = string.Empty;

    public string SubTitle { get; set; } = string.Empty;

    public int Score { get; set; }

    public Func<ActionContext, bool>? Action { get; set; }
}
