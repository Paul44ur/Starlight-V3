using Starlight.App;

namespace Starlight.Tests;

public class ModelFilesTests
{
    [Fact]
    public void Saved_model_stays_selectable_when_the_folder_does_not_have_it()
    {
        // Typing a folder path letter by letter passes through folders without the file.
        var (models, projectors) = ModelFiles.Lists([], "qwen.gguf", "mmproj-qwen.gguf");
        Assert.Contains("qwen.gguf", models);
        Assert.Contains("mmproj-qwen.gguf", projectors);
    }

    [Fact]
    public void Files_are_split_into_models_and_projectors()
    {
        var (models, projectors) = ModelFiles.Lists(["b.gguf", "a.gguf", "mmproj-a.gguf"], "", ModelFiles.None);
        Assert.Equal(["a.gguf", "b.gguf"], models);
        Assert.Equal([ModelFiles.None, "mmproj-a.gguf"], projectors);
    }

    [Fact]
    public void A_saved_model_that_is_present_is_not_listed_twice()
    {
        var (models, _) = ModelFiles.Lists(["a.gguf"], "A.gguf", ModelFiles.None);
        Assert.Single(models);
    }
}
