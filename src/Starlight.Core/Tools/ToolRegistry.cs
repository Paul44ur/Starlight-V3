using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace Starlight.Tools;

public sealed record ToolDefinition(string Name, string Description, Permission DefaultPermission, TimeSpan Timeout, AIFunction Function, string Group, IReadOnlySet<string> AskForActions);

/// <summary>Collects the tools of all providers. The JSON schema for each comes from the method's parameters.</summary>
public sealed class ToolRegistry
{

    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ToolDefinition> All => _tools.Values;

    public ToolDefinition? Find(string name) => _tools.GetValueOrDefault(name);

    public void Add(IToolProvider provider)
    {
        var group = provider.GetType().Name.Replace("Tools", "", StringComparison.Ordinal);
        foreach (var method in provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
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
            var function = AIFunctionFactory.Create(method, provider, new AIFunctionFactoryOptions
            {
                Name = attr.Name,
                Description = description,
            });
            _tools[attr.Name] = new ToolDefinition(attr.Name, description, attr.Permission,
                TimeSpan.FromSeconds(attr.TimeoutSeconds), function, group,
                new HashSet<string>(attr.AskForActions, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>What the model sees: declarations only, so nothing can run outside the dispatcher.</summary>
    public IList<AITool> Declarations() =>
        _tools.Values.Select(t => (AITool)t.Function.AsDeclarationOnly()).ToList();
}
