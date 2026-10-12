namespace ScottPlotEx.Tests.TestInfrastructure;

/// <summary>测试用到的路径。</summary>
internal static class TestPaths
{
    /// <summary>仓库根目录（包含 ScottPlotEx.sln 的目录）。</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>示例图片输出目录。</summary>
    public static string ArtifactsDirectory { get; } = CreateDirectory(Path.Combine(RepositoryRoot, "artifacts"));

    /// <summary>文档截图目录（README 引用的图片，由测试自动生成）。</summary>
    public static string DocsDirectory { get; } = CreateDirectory(Path.Combine(RepositoryRoot, "docs"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScottPlotEx.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
