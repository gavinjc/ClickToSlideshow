namespace ClickToSlideshow.App.Tests;

// Finds a file in the repo by walking up from the test output folder, for tests that read source
// files the app doesn't ship as data (the resw, the shell extension's header).
internal static class SourceTree
{
    public static string Find(params string[] relativeParts)
    {
        string relative = Path.Combine(relativeParts);
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{relative} not found above the test output folder");
    }
}
