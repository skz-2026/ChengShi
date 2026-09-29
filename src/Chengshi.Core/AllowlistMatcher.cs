namespace Chengshi.Core;

public sealed class AllowlistMatcher
{
    public bool IsAllowed(ProcessIdentity process, Desk desk)
    {
        if (process.Pid is 0 or 4)
        {
            return true;
        }

        if (AlwaysAllow.IsAlwaysAllowed(process.FileName, process.ImagePath))
        {
            return true;
        }

        return MatchesDeskRules(process, desk);
    }

    /// <summary>
    /// 只看书桌名单规则，不含系统关键进程的保底放行。
    /// 「整个电脑」场景把名单当拒绝名单用时用它：命中名单还要再过一遍
    /// AlwaysAllow，系统关键进程就算被家长点名也拦不掉。
    /// </summary>
    public bool MatchesDeskRules(ProcessIdentity process, Desk desk)
    {
        foreach (var rule in desk.Rules)
        {
            if (Matches(process, rule))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Matches(ProcessIdentity process, AllowRule rule) => rule switch
    {
        FileNameAllowRule fileName => string.Equals(
            Path.GetFileName(process.FileName),
            fileName.FileName,
            StringComparison.OrdinalIgnoreCase),
        PathAllowRule path when string.IsNullOrWhiteSpace(process.ImagePath) => false,
        PathAllowRule path => MatchesPath(process.ImagePath!, path),
        PublisherAllowRule publisher when string.IsNullOrWhiteSpace(process.Publisher) => false,
        PublisherAllowRule publisher => process.Publisher.Contains(
            publisher.Publisher,
            StringComparison.OrdinalIgnoreCase),
        PackageFamilyAllowRule pfn when string.IsNullOrWhiteSpace(process.PackageFamilyName) => false,
        PackageFamilyAllowRule pfn => string.Equals(
            process.PackageFamilyName,
            pfn.PackageFamilyName,
            StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool MatchesPath(string imagePath, PathAllowRule rule)
    {
        var image = Normalize(imagePath);
        var prefix = Normalize(rule.PathPrefix.TrimEnd('\\', '/'));
        if (rule.IsDirectory)
        {
            return image.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetDirectoryName(image), prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(image, prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);
}
