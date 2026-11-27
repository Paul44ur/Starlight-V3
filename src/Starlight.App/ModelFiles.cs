namespace Starlight.App;

/// <summary>Builds the model and projector lists for the Settings window.</summary>
public static class ModelFiles
{
    public const string None = "(none)";

    /// <summary>
    /// Splits the .gguf files in the models folder into chat models and vision projectors.
    /// The saved choice stays in its list even when the file is not in the folder, so typing a
    /// folder path or browsing never throws the user's choice away.
    /// </summary>
    public static (List<string> Models, List<string> Projectors) Lists(IEnumerable<string> files, string chosenModel, string chosenProjector)
    {
        var all = files.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var models = all.Where(f => !IsProjector(f)).ToList();
        var projectors = all.Where(IsProjector).Prepend(None).ToList();
        if (chosenModel.Length > 0 && !models.Contains(chosenModel, StringComparer.OrdinalIgnoreCase))
        {
            models.Insert(0, chosenModel);
        }
        if (chosenProjector.Length > 0 && chosenProjector != None && !projectors.Contains(chosenProjector, StringComparer.OrdinalIgnoreCase))
        {
            projectors.Insert(1, chosenProjector);
        }
        return (models, projectors);
    }

    private static bool IsProjector(string file) => file.Contains("mmproj", StringComparison.OrdinalIgnoreCase);
}
