using M32LiveConsoleTool.Infrastructure;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for <see cref="AppPaths"/> preset-path resolution, in particular that a
/// preset name cannot escape the presets folder.
/// </summary>
public class AppPathsTests
{
    [Fact]
    public void GetPresetPath_appends_json_and_stays_in_the_presets_folder()
    {
        string path = AppPaths.GetPresetPath("myshow");

        Assert.EndsWith("myshow.json", path);
        Assert.StartsWith(AppPaths.PresetsDirectory, path);
    }

    [Fact]
    public void GetPresetPath_does_not_double_the_json_extension()
    {
        string path = AppPaths.GetPresetPath("myshow.json");

        Assert.EndsWith("myshow.json", path);
        Assert.DoesNotContain("myshow.json.json", path);
    }

    [Fact]
    public void GetSessionLogPath_is_a_dated_file_under_the_logs_folder()
    {
        string path = AppPaths.GetSessionLogPath();

        Assert.StartsWith(AppPaths.LogsDirectory, path);
        Assert.EndsWith(".log", path);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("../../secret")]
    [InlineData("sub/dir")]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPresetPath_rejects_path_traversal_and_empty_names(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => AppPaths.GetPresetPath(name));
    }

    [Fact]
    public void PruneOldLogs_keeps_only_the_newest_logs_and_leaves_other_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "m32lct_logtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 5; i++)
            {
                string p = Path.Combine(dir, $"log{i}.log");
                File.WriteAllText(p, "x");
                File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(i)); // log4 is newest
            }
            string nonLog = Path.Combine(dir, "notes.txt");
            File.WriteAllText(nonLog, "x");

            AppPaths.PruneOldLogs(dir, 2);

            string[] logs = Directory.GetFiles(dir, "*.log").Select(Path.GetFileName).OrderBy(n => n).ToArray()!;
            Assert.Equal(new[] { "log3.log", "log4.log" }, logs);
            Assert.True(File.Exists(nonLog)); // non-log files are left untouched
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
