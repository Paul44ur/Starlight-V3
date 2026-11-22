using Starlight.Tools;
using Starlight.Tools.Pc;

namespace Starlight.Tests;

public sealed class PcToolsTests
{
    [Fact]
    public void System_info_names_os_memory_and_drives()
    {
        var text = new PcTools([]).SystemInfo();
        Assert.Contains("OS:", text);
        Assert.Contains("Memory:", text);
        Assert.Contains("Drive", text);
    }

    [Fact]
    public void Finds_files_by_part_of_name_and_pattern_newest_first()
    {
        using var env = new TestEnv();
        var docs = Directory.CreateDirectory(Path.Combine(env.Root, "Documents", "sub")).FullName;
        var older = Path.Combine(docs, "Tax Report 2025.pdf");
        var newer = Path.Combine(env.Root, "Documents", "report-final.docx");
        File.WriteAllText(older, "x");
        File.WriteAllText(newer, "y");
        File.SetLastWriteTime(older, DateTime.Now.AddDays(-10));
        var tools = new PcTools([Path.Combine(env.Root, "Documents")]);

        var hits = tools.FindFiles("REPORT").Split('\n');
        Assert.Equal(2, hits.Length);
        Assert.StartsWith(newer, hits[0]);
        Assert.Single(tools.FindFiles("*.pdf").Split('\n'));
        Assert.StartsWith("No files", tools.FindFiles("holiday"));
    }

    [Fact]
    public void Missing_folder_and_empty_name_are_tool_errors()
    {
        var tools = new PcTools([]);
        Assert.Throws<ToolException>(() => tools.FindFiles("x", "/does/not/exist"));
        Assert.Throws<ToolException>(() => tools.FindFiles(" "));
    }
}
