using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace Starlight.Tools;

public sealed record ToolDefinition(string Name, string Description, Permission DefaultPermission, TimeSpan Timeout, AIFunction Function, string Group, IReadOnlySet<string> AskForActions)
{
    public bool UntrustedOutput { get; init; }
    public bool AskAfterUntrusted { get; init; }
    public int MaxResultChars { get; init; } = 6000;
    public IPermissionHint? Hint { get; init; }
}

/// <summary>Collects the tools of all providers. The JSON schema for each comes from the method's parameters.</summary>
public sealed class ToolRegistry
{

    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ToolDefinition> All => _tools.Values;

    public ToolDefinition? Find(string name) => _tools.GetValueOrDefault(name);

    public void Add(IToolProvider provider)
    {
        var group = provider.GetType().Name.Replace("Tools", "", StringComparison.Ordinal);
        foreach (var method in provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var attr = method.GetCustomAttribute<ToolAttribute>();
            if (attr is null)
            {
                continue;
            }
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
                ?? throw new InvalidOperationException($"Tool {attr.Name} needs a [Description].");
            if (_tools.ContainsKey(attr.Name))
            {
                throw new InvalidOperationException($"Two tools are named {attr.Name}.");
            }
            var function = AIFunctionFactory.Create(method, method.IsStatic ? null : provider, new AIFunctionFactoryOptions
            {
                Name = attr.Name,
                Description = description,
                // Keep the method's own return value (string, ToolOutput…); the dispatcher turns it into text.
                MarshalResult = static (value, _, _) => new ValueTask<object?>(value),
            });
            _tools[attr.Name] = new ToolDefinition(attr.Name, description, attr.Permission,
                TimeSpan.FromSeconds(attr.TimeoutSeconds), function, group,
                new HashSet<string>(attr.AskForActions, StringComparer.OrdinalIgnoreCase))
            {
                UntrustedOutput = attr.UntrustedOutput,
                AskAfterUntrusted = attr.AskAfterUntrusted,
                Hint = provider as IPermissionHint,
                MaxResultChars = attr.MaxResultChars,
            };
        }
    }

    /// <summary>What the model sees: declarations only, so nothing can run outside the dispatcher.</summary>
    public IList<AITool> Declarations() =>
        _tools.Values.Select(t => (AITool)t.Function.AsDeclarationOnly()).ToList();
}
